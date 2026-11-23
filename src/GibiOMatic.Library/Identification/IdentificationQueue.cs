using System.Text.Json;
using Dapper;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Json;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Core.Settings;
using GibiOMatic.Library.Editing;
using GibiOMatic.Library.Index;
using GibiOMatic.Metadata;
using GibiOMatic.Metadata.Assisted;
using GibiOMatic.Metadata.Http;
using GibiOMatic.Metadata.Identification;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Library.Identification;

public enum IdentificationState
{
    /// <summary>Waiting for its turn.</summary>
    Queued,
    Searching,

    /// <summary>Has candidates; waits for the user in the Review.</summary>
    NeedsReview,

    /// <summary>Nothing found; waits for the user (search again or edit by hand).</summary>
    NoMatch,

    /// <summary>The sources failed; can be queued again.</summary>
    Failed,
    AutoApproved,
    Approved,
}

/// <summary>The identification of one book as stored in the index.</summary>
public sealed record BookIdentification(string BookId, IdentificationState State, IdentificationResult? Result, string? Message, bool Skipped);

/// <summary>
/// Identifies books in the background after they are imported (decision of 2026-10-17): one book
/// at a time, at the pace the sources allow. Safe matches are approved automatically (SPEC §8.2);
/// the others wait in the Review with their candidates. The queue lives in the index.
/// </summary>
public sealed class IdentificationQueue : IAsyncDisposable
{
    private const string PausedKey = "identify.paused";

    private readonly LibraryInfo _library;
    private readonly LibraryIndex _index;
    private readonly IBookIdentifier _identifier;
    private readonly BookEditor _editor;
    private readonly CoverImageCache _covers;
    private readonly ISettingsService _settings;
    private readonly ILogger<IdentificationQueue> _logger;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _current;
    private Task _worker = Task.CompletedTask;

    public IdentificationQueue(LibraryInfo library, LibraryIndex index, IBookIdentifier identifier, BookEditor editor,
        CoverImageCache covers, ISettingsService settings, ILogger<IdentificationQueue> logger)
    {
        _covers = covers;
        _library = library;
        _index = index;
        _identifier = identifier;
        _editor = editor;
        _settings = settings;
        _logger = logger;
        Assisted = new AssistedStore(index);
    }

    /// <summary>Claude's checked answers (SPEC §8.4); they join every result stored for the book.</summary>
    public AssistedStore Assisted { get; }

    /// <summary>The identification left a book for the user (with candidates or none): the automatic Claude mode starts there.</summary>
    public event EventHandler<string>? ReviewNeeded;

    /// <summary>Identification states changed (queue, review, approvals).</summary>
    public event EventHandler? Changed;

    /// <summary>A book's metadata was saved (auto-approval or Review).</summary>
    public event EventHandler<string>? BookUpdated;

    public bool IsPaused { get; private set; }

    /// <summary>Title of the book being identified, if any.</summary>
    public string? CurrentTitle { get; private set; }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await using (var connection = await _index.OpenAsync(ct))
        {
            await connection.ExecuteAsync(new CommandDefinition("UPDATE identifications SET state = 'queued' WHERE state = 'searching';", cancellationToken: ct));
        }

        IsPaused = await _index.GetStateAsync(PausedKey, ct) == "true";

        // Books imported before identification existed, or before this machine knew them.
        var pending = (await _index.GetBooksAsync(ct)).Where(b => b.Metadata.Review.Status == ReviewStatus.Pending).Select(b => b.Metadata.Id);
        await EnqueueAsync(pending, force: false, onlyNew: true, ct);
        _worker = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
    }

    /// <summary>
    /// Queues books for identification. Books already identified are queued again only with
    /// <paramref name="force"/> ("Fetch metadata").
    /// </summary>
    public async Task<int> EnqueueAsync(IEnumerable<string> bookIds, bool force, CancellationToken ct = default) =>
        await EnqueueAsync(bookIds, force, onlyNew: false, ct);

    private async Task<int> EnqueueAsync(IEnumerable<string> bookIds, bool force, bool onlyNew, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var count = 0;
        await using var connection = await _index.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        foreach (var id in bookIds.Distinct())
        {
            var sql = onlyNew
                ? "INSERT OR IGNORE INTO identifications (book_id, state, updated_at) VALUES (@id, 'queued', @now);"
                : force
                    ? """
                      INSERT INTO identifications (book_id, state, updated_at) VALUES (@id, 'queued', @now)
                      ON CONFLICT(book_id) DO UPDATE SET state = 'queued', skipped = 0, message = NULL, updated_at = @now
                      WHERE state <> 'searching';
                      """
                    : """
                      INSERT INTO identifications (book_id, state, updated_at) VALUES (@id, 'queued', @now)
                      ON CONFLICT(book_id) DO UPDATE SET state = 'queued', message = NULL, updated_at = @now
                      WHERE state IN ('failed');
                      """;
            count += await connection.ExecuteAsync(new CommandDefinition(sql, new { id, now }, transaction: tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        if (count > 0)
        {
            _logger.LogInformation("Identification: {Count} books queued", count);
            OnChanged();
            _wake.Release();
        }

        return count;
    }

    public async Task<BookIdentification?> GetAsync(string bookId, CancellationToken ct = default) =>
        (await QueryAsync("WHERE book_id = @bookId", new { bookId }, ct)).FirstOrDefault();

    /// <summary>Books waiting for the user: with candidates, without a match, or with failed sources.</summary>
    public Task<IReadOnlyList<BookIdentification>> GetReviewItemsAsync(CancellationToken ct = default) =>
        QueryAsync("WHERE state IN ('needsReview', 'noMatch', 'failed')", null, ct);

    /// <summary>
    /// Counts as the Review lists: a book is in the Review while its identification waits for the user,
    /// approved or not (an approved book sent back to it stays until approved again or kept as it is).
    /// A row whose book is gone counts nowhere.
    /// </summary>
    public async Task<IdentificationCounts> GetCountsAsync(CancellationToken ct = default)
    {
        await using var connection = await _index.OpenAsync(ct);
        var rows = (await connection.QueryAsync<(string State, int Count)>(new CommandDefinition(
            "SELECT i.state, COUNT(*) FROM identifications i JOIN books b ON b.id = i.book_id GROUP BY i.state;",
            cancellationToken: ct))).ToDictionary(r => r.State, r => r.Count);
        int Of(IdentificationState s) => rows.GetValueOrDefault(Name(s));
        return new IdentificationCounts(
            Of(IdentificationState.Queued) + Of(IdentificationState.Searching),
            Of(IdentificationState.NeedsReview) + Of(IdentificationState.NoMatch) + Of(IdentificationState.Failed),
            Of(IdentificationState.AutoApproved),
            Of(IdentificationState.Approved));
    }

    /// <summary>Whether the book waits in the Review (approved or not): its identification waits for the user.</summary>
    public async Task<bool> IsInReviewAsync(string bookId, CancellationToken ct = default) =>
        await GetAsync(bookId, ct) is { State: IdentificationState.NeedsReview or IdentificationState.NoMatch or IdentificationState.Failed };

    /// <summary>Applies the chosen fields of a candidate and marks the book approved.</summary>
    public async Task<CatalogBook> ApproveAsync(string bookId, MetadataCandidate candidate, IReadOnlySet<ProposalField> fields, CancellationToken ct = default)
    {
        var matchedBy = candidate.IsbnMatch ? MatchedBy.Isbn : candidate.Record.Source == SourceIds.ComicVine ? MatchedBy.ComicVineVolume : MatchedBy.Manual;
        var saved = await _editor.UpdateAsync(_library, bookId, book => Proposal.Apply(book, candidate.Record, fields) with
        {
            Review = new ReviewInfo { Status = ReviewStatus.Approved, ApprovedAt = DateTimeOffset.UtcNow, MatchedBy = matchedBy, Confidence = candidate.Confidence },
        }, ct);
        // The source's cover only when the user checked it; a failed download keeps the file's cover.
        if (fields.Contains(ProposalField.Cover) && await _covers.GetAsync(candidate.Record.CoverFullUrl, ct) is { } image
            && !await _editor.SetCoverAsync(_library, bookId, image, ct))
        {
            _logger.LogWarning("The cover of {Source} could not be used for book {BookId}", candidate.Record.Source, bookId);
        }

        await SetStateAsync(bookId, IdentificationState.Approved, null, null, ct);
        BookUpdated?.Invoke(this, bookId);
        OnChanged();
        return saved;
    }

    /// <summary>
    /// Approves the books of a group of the Review by series that have a record (SPEC §7.1), as the
    /// Review does: the fields it would tick, plus the group's series and the title. Every book gets
    /// its own history entry, all in one batch that can be undone at once. Returns the batch, or
    /// null when nothing was saved.
    /// </summary>
    public async Task<HistoryBatch?> ApproveGroupAsync(string label, GroupSeries series, IReadOnlyList<SeriesPlanRow> rows, IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var entries = new List<HistoryBatchEntry>();
        var batch = new HistoryBatch { Id = Core.Ids.Ulid.NewUlid(), Label = label, CreatedAt = DateTimeOffset.UtcNow };
        var done = 0;
        try
        {
            foreach (var row in rows.Where(r => r.Candidate is not null))
            {
                var candidate = row.Candidate!;
                if (!candidate.Record.IsDetailed)
                {
                    try
                    {
                        candidate = await _identifier.CompleteAsync(candidate, ct);
                    }
                    catch (Exception ex) when (ex is MetadataSourceException or HttpRequestException)
                    {
                        // The summary has the title, number, date and cover; credits wait for another search.
                        _logger.LogWarning(ex, "Could not load the details of {ComicVineId} for book {BookId}", candidate.Record.ComicVineId, row.BookId);
                    }
                }

                var record = SeriesPlan.Shape(candidate.Record, series, row.NewTitle);
                await _editor.UpdateAsync(_library, row.BookId, book => Proposal.Apply(book, record, Proposal.DefaultSelection(Proposal.Compare(book, record))) with
                {
                    Review = new ReviewInfo
                    {
                        Status = ReviewStatus.Approved, ApprovedAt = DateTimeOffset.UtcNow, MatchedBy = MatchedBy.ComicVineVolume, Confidence = candidate.Confidence,
                    },
                }, entries, ct);
                await SetStateAsync(row.BookId, IdentificationState.Approved, null, null, ct);
                BookUpdated?.Invoke(this, row.BookId);
                progress?.Report(++done);
            }
        }
        finally
        {
            if (entries.Count > 0)
            {
                batch = batch with { Entries = entries };
                await _editor.SaveBatchAsync(_library, batch, CancellationToken.None);
            }

            _logger.LogInformation("Review by series: {Count} books of '{Label}' approved", done, label);
            OnChanged();
        }

        return entries.Count > 0 ? batch : null;
    }

    /// <summary>"Undo the batch": the books get back their metadata from before it and return to the Review.</summary>
    public async Task<IReadOnlyList<string>> UndoBatchAsync(string batchId, CancellationToken ct = default)
    {
        var restored = await _editor.UndoBatchAsync(_library, batchId, ct);
        foreach (var bookId in restored)
        {
            var identification = await GetAsync(bookId, ct);
            var state = identification?.Result?.Candidates.Count > 0 || (await Assisted.GetCandidatesAsync(bookId, ct)).Count > 0
                ? IdentificationState.NeedsReview
                : IdentificationState.NoMatch;
            await SetStateAsync(bookId, state, null, null, ct);
            BookUpdated?.Invoke(this, bookId);
        }

        OnChanged();
        return restored;
    }

    /// <summary>The user approved the book in the Editor (matched by hand).</summary>
    public async Task MarkApprovedAsync(string bookId, CancellationToken ct = default)
    {
        await SetStateAsync(bookId, IdentificationState.Approved, null, null, ct);
        OnChanged();
    }

    /// <summary>
    /// "Keep as it is": an approved book sent back to the Review leaves it with the metadata it has.
    /// Its candidates are kept; it counts again as approved the way it was (by the user or automatically).
    /// </summary>
    public async Task KeepAsync(string bookId, CancellationToken ct = default)
    {
        var book = await _index.GetBookAsync(bookId, ct) ?? throw new InvalidOperationException($"Book {bookId} not found.");
        if (book.Metadata.Review.Status != ReviewStatus.Approved)
        {
            throw new InvalidOperationException($"Book {bookId} is not approved.");
        }

        var state = book.Metadata.Review.MatchedBy is MatchedBy.Auto or MatchedBy.ClaudeAuto ? IdentificationState.AutoApproved : IdentificationState.Approved;
        await SetStateAsync(bookId, state, null, null, ct);
        OnChanged();
    }

    /// <summary>"Pular": the book goes to the end of the Review.</summary>
    public async Task SkipAsync(string bookId, CancellationToken ct = default)
    {
        await using var connection = await _index.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE identifications SET skipped = 1, updated_at = @now WHERE book_id = @bookId;",
            new { bookId, now = DateTimeOffset.UtcNow.ToString("O") }, cancellationToken: ct));
        OnChanged();
    }

    /// <summary>"Search again" from the Review with other words (or an ISBN); runs now and stores the result.</summary>
    public async Task<IdentificationResult> SearchAgainAsync(string bookId, string? searchText, CancellationToken ct = default)
    {
        if (Core.Text.Isbn.IsValid(searchText))
        {
            return await SearchByIsbnAsync(bookId, Core.Text.Isbn.ToIsbn13(searchText!), ct);
        }

        var book = await _index.GetBookAsync(bookId, ct) ?? throw new InvalidOperationException($"Book {bookId} not found.");
        var result = await _identifier.IdentifyAsync(book.Metadata, _settings.Current.Metadata, searchText, ct);
        await StoreSearchAsync(bookId, result, ct);
        return result;
    }

    /// <summary>
    /// An ISBN the user pasted (from Amazon, Goodreads or the book itself): searched as if it were the
    /// file's, so its record is a candidate and its title searches ComicVine. The book is not changed.
    /// </summary>
    public async Task<IdentificationResult> SearchByIsbnAsync(string bookId, string isbn, CancellationToken ct = default)
    {
        var book = await _index.GetBookAsync(bookId, ct) ?? throw new InvalidOperationException($"Book {bookId} not found.");
        var withIsbn = book.Metadata with { Identifiers = book.Metadata.Identifiers with { Isbn = isbn } };
        var result = await _identifier.IdentifyAsync(withIsbn, _settings.Current.Metadata, null, ct);
        await StoreSearchAsync(bookId, result, ct);
        return result;
    }

    private async Task StoreSearchAsync(string bookId, IdentificationResult result, CancellationToken ct)
    {
        await SetStateAsync(bookId, result.Candidates.Count > 0 ? IdentificationState.NeedsReview : IdentificationState.NoMatch, result,
            MessageOf(result), ct);
        OnChanged();
    }

    /// <summary>A ComicVine link pasted in the Review: its book becomes the candidates. Returns how many.</summary>
    public async Task<int> UseComicVineLinkAsync(string bookId, string link, CancellationToken ct = default)
    {
        var book = await _index.GetBookAsync(bookId, ct) ?? throw new InvalidOperationException($"Book {bookId} not found.");
        var candidates = await _identifier.FromComicVineLinkAsync(book.Metadata, link, ct);
        if (candidates.Count > 0)
        {
            await SetStateAsync(bookId, IdentificationState.NeedsReview, new IdentificationResult(BookQuery.From(book.Metadata), candidates, []) { Algorithm = BookIdentifier.Algorithm }, null, ct);
            OnChanged();
        }

        return candidates.Count;
    }

    /// <summary>Details (credits, synopsis) of a candidate picked in the Review.</summary>
    public Task<MetadataCandidate> CompleteAsync(MetadataCandidate candidate, CancellationToken ct = default) =>
        _identifier.CompleteAsync(candidate, ct);

    public async Task PauseAsync(CancellationToken ct = default)
    {
        IsPaused = true;
        await _index.SetStateAsync(PausedKey, "true", ct);
        _current?.Cancel();
        OnChanged();
    }

    public async Task ResumeAsync(CancellationToken ct = default)
    {
        IsPaused = false;
        await _index.SetStateAsync(PausedKey, "false", ct);
        OnChanged();
        _wake.Release();
    }

    /// <summary>Waits until nothing is queued (for tests).</summary>
    public async Task WaitUntilIdleAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested && !IsPaused && (await GetCountsAsync(ct)).Queued > 0)
        {
            await Task.Delay(25, ct);
        }
    }

    private async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var next = IsPaused ? null : (await QueryAsync("WHERE state = 'queued' ORDER BY updated_at LIMIT 1", null, stop)).FirstOrDefault();
                if (next is null)
                {
                    await _wake.WaitAsync(stop);
                    continue;
                }

                using var current = CancellationTokenSource.CreateLinkedTokenSource(stop);
                _current = current;
                try
                {
                    await IdentifyAsync(next.BookId, current.Token);
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                    await SetStateAsync(next.BookId, IdentificationState.Queued, null, null, stop);
                }
                finally
                {
                    _current = null;
                    CurrentTitle = null;
                    OnChanged();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Identification worker error");
                await Task.Delay(TimeSpan.FromSeconds(1), stop).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    private async Task IdentifyAsync(string bookId, CancellationToken ct)
    {
        var entry = await _index.GetBookAsync(bookId, ct);
        if (entry is null)
        {
            await DeleteAsync(bookId, ct);
            return;
        }

        CurrentTitle = entry.Metadata.Title;
        await SetStateAsync(bookId, IdentificationState.Searching, null, null, ct);
        OnChanged();

        var settings = _settings.Current.Metadata;
        var result = await _identifier.IdentifyAsync(entry.Metadata, settings, null, ct);
        if (result.Candidates.Count == 0)
        {
            var failed = result.Notes.Any(n => n.EndsWith(":Unavailable", StringComparison.Ordinal) || n.EndsWith(":RateLimited", StringComparison.Ordinal)
                                               || n.EndsWith(":BadResponse", StringComparison.Ordinal));
            await SetStateAsync(bookId, failed ? IdentificationState.Failed : IdentificationState.NoMatch, result, MessageOf(result), ct);
            if (!failed)
            {
                ReviewNeeded?.Invoke(this, bookId);
            }

            return;
        }

        // Approved books are never changed automatically; they only get new candidates.
        if (entry.Metadata.Review.Status == ReviewStatus.Pending && Proposal.CanAutoApprove(entry.Metadata, result, settings, out var reason))
        {
            var best = result.Best!;
            var fields = Proposal.DefaultSelection(Proposal.Compare(entry.Metadata, best.Record));
            await _editor.UpdateAsync(_library, bookId, book => Proposal.Apply(book, best.Record, fields) with
            {
                Review = new ReviewInfo { Status = ReviewStatus.Approved, ApprovedAt = DateTimeOffset.UtcNow, MatchedBy = MatchedBy.Auto, Confidence = best.Confidence },
            }, ct);
            await SetStateAsync(bookId, IdentificationState.AutoApproved, result, null, ct);
            _logger.LogInformation("Book {BookId} approved automatically ({Confidence:P0})", bookId, best.Confidence);
            BookUpdated?.Invoke(this, bookId);
            return;
        }

        await SetStateAsync(bookId, IdentificationState.NeedsReview, result, MessageOf(result), ct);
        ReviewNeeded?.Invoke(this, bookId);
    }

    /// <summary>
    /// Claude's answers of a book (stored in <see cref="Assisted"/>) join its candidates; a book that
    /// had none goes to the Review with them. Approved books keep their state.
    /// </summary>
    public async Task ApplyAssistedAsync(string bookId, CancellationToken ct = default)
    {
        var book = await _index.GetBookAsync(bookId, ct) ?? throw new InvalidOperationException($"Book {bookId} not found.");
        var current = await GetAsync(bookId, ct);
        var result = current?.Result ?? new IdentificationResult(BookQuery.From(book.Metadata), [], []) { Algorithm = BookIdentifier.Algorithm };
        var assisted = await Assisted.GetCandidatesAsync(bookId, ct);
        var state = current?.State is IdentificationState.Approved or IdentificationState.AutoApproved
            ? current.State
            : assisted.Count > 0 || result.Candidates.Count > 0 ? IdentificationState.NeedsReview : current?.State ?? IdentificationState.NoMatch;
        await SetStateAsync(bookId, state, result, current?.Message, ct);
        OnChanged();
    }

    /// <summary>
    /// Approves Claude's candidate without the user (SPEC §8.4, "Approval"), when
    /// <see cref="AssistedApproval.CanApprove"/> says so. Returns whether it did.
    /// </summary>
    public async Task<bool> AutoApproveAssistedAsync(string bookId, MetadataCandidate candidate, CancellationToken ct = default)
    {
        var book = await _index.GetBookAsync(bookId, ct);
        var current = await GetAsync(bookId, ct);
        if (book is null)
        {
            return false;
        }

        var algorithm = current?.Result is { } r ? r with { Candidates = r.Candidates.Where(c => c.Assisted is null).ToList() } : null;
        var others = await Assisted.GetCandidatesAsync(bookId, ct);
        if (!AssistedApproval.CanApprove(book.Metadata, candidate, algorithm, others, _settings.Current.Claude, _settings.Current.Metadata, out var reason))
        {
            _logger.LogInformation("Claude's answer for book {BookId} left for the Review ({Reason})", bookId, reason);
            return false;
        }

        var fields = Proposal.DefaultSelection(Proposal.Compare(book.Metadata, candidate.Record)).Except(candidate.Assisted?.Unticked ?? []).ToHashSet();
        await _editor.UpdateAsync(_library, bookId, b => Proposal.Apply(b, candidate.Record, fields) with
        {
            Review = new ReviewInfo { Status = ReviewStatus.Approved, ApprovedAt = DateTimeOffset.UtcNow, MatchedBy = MatchedBy.ClaudeAuto, Confidence = candidate.Confidence },
        }, ct);
        await SetStateAsync(bookId, IdentificationState.AutoApproved, null, null, ct);
        _logger.LogInformation("Book {BookId} approved automatically from Claude's answer ({Confidence:P0})", bookId, candidate.Confidence);
        BookUpdated?.Invoke(this, bookId);
        OnChanged();
        return true;
    }

    private static string? MessageOf(IdentificationResult result) => result.Notes.Count == 0 ? null : string.Join(';', result.Notes);

    private async Task SetStateAsync(string bookId, IdentificationState state, IdentificationResult? result, string? message, CancellationToken ct)
    {
        // Claude's answers survive a new search of the algorithm: they are kept apart and merged in.
        if (result is not null)
        {
            result = AssistedRecords.Merge(result, await Assisted.GetCandidatesAsync(bookId, ct));
        }

        await using var connection = await _index.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO identifications (book_id, state, result_json, confidence, message, updated_at)
            VALUES (@bookId, @state, @json, @confidence, @message, @now)
            ON CONFLICT(book_id) DO UPDATE SET state = @state,
                result_json = COALESCE(@json, result_json),
                confidence = COALESCE(@confidence, confidence),
                message = CASE WHEN @json IS NULL THEN message ELSE @message END,
                updated_at = @now;
            """,
            new
            {
                bookId,
                state = Name(state),
                json = result is null ? null : JsonSerializer.Serialize(result, GibiJson.Options),
                confidence = result?.Best?.Confidence,
                message,
                now = DateTimeOffset.UtcNow.ToString("O"),
            },
            cancellationToken: ct));
    }

    private async Task DeleteAsync(string bookId, CancellationToken ct)
    {
        await using var connection = await _index.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM identifications WHERE book_id = @bookId;", new { bookId }, cancellationToken: ct));
    }

    private sealed record Row(string BookId, string State, string? ResultJson, string? Message, long Skipped);

    private async Task<IReadOnlyList<BookIdentification>> QueryAsync(string where, object? args, CancellationToken ct)
    {
        await using var connection = await _index.OpenAsync(ct);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            $"SELECT book_id AS BookId, state AS State, result_json AS ResultJson, message AS Message, skipped AS Skipped FROM identifications {where};",
            args, cancellationToken: ct));
        return rows.Select(r => new BookIdentification(
            r.BookId,
            Enum.Parse<IdentificationState>(r.State, ignoreCase: true),
            r.ResultJson is null ? null : JsonSerializer.Deserialize<IdentificationResult>(r.ResultJson, GibiJson.Options),
            r.Message,
            r.Skipped != 0)).ToList();
    }

    private static string Name(IdentificationState state) => JsonNamingPolicy.CamelCase.ConvertName(state.ToString());

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
        _wake.Dispose();
    }
}

/// <param name="Queued">Waiting or being identified.</param>
/// <param name="ToReview">Waiting for the user in the Review.</param>
public sealed record IdentificationCounts(int Queued, int ToReview, int AutoApproved, int Approved);

/// <summary>Creates the identification queue of an open library.</summary>
public sealed class IdentificationQueueFactory(
    LibraryService libraries,
    IBookIdentifier identifier,
    BookEditor editor,
    CoverImageCache covers,
    ISettingsService settings,
    ILogger<IdentificationQueue> logger)
{
    public IdentificationQueue Create(LibraryInfo library) =>
        new(library, libraries.GetIndex(library), identifier, editor, covers, settings, logger);
}
