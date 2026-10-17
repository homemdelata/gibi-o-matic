using System.Text.Json;
using Dapper;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Json;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Core.Settings;
using GibiOMatic.Library.Editing;
using GibiOMatic.Library.Index;
using GibiOMatic.Metadata;
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
    }

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
    /// <paramref name="force"/> ("Buscar metadados").
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

    public async Task<IdentificationCounts> GetCountsAsync(CancellationToken ct = default)
    {
        await using var connection = await _index.OpenAsync(ct);
        var rows = (await connection.QueryAsync<(string State, int Count)>(new CommandDefinition(
            "SELECT state, COUNT(*) FROM identifications GROUP BY state;", cancellationToken: ct))).ToDictionary(r => r.State, r => r.Count);
        int Of(IdentificationState s) => rows.GetValueOrDefault(Name(s));
        return new IdentificationCounts(
            Of(IdentificationState.Queued) + Of(IdentificationState.Searching),
            Of(IdentificationState.NeedsReview) + Of(IdentificationState.NoMatch) + Of(IdentificationState.Failed),
            Of(IdentificationState.AutoApproved),
            Of(IdentificationState.Approved));
    }

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

    /// <summary>The user approved the book in the Editor (matched by hand).</summary>
    public async Task MarkApprovedAsync(string bookId, CancellationToken ct = default)
    {
        await SetStateAsync(bookId, IdentificationState.Approved, null, null, ct);
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

    /// <summary>"Buscar de novo" from the Review with other words; runs now and stores the result.</summary>
    public async Task<IdentificationResult> SearchAgainAsync(string bookId, string? searchText, CancellationToken ct = default)
    {
        var book = await _index.GetBookAsync(bookId, ct) ?? throw new InvalidOperationException($"Book {bookId} not found.");
        var result = await _identifier.IdentifyAsync(book.Metadata, _settings.Current.Metadata, searchText, ct);
        await SetStateAsync(bookId, result.Candidates.Count > 0 ? IdentificationState.NeedsReview : IdentificationState.NoMatch, result,
            MessageOf(result), ct);
        OnChanged();
        return result;
    }

    /// <summary>A ComicVine link pasted in the Review: its book becomes the candidates. Returns how many.</summary>
    public async Task<int> UseComicVineLinkAsync(string bookId, string link, CancellationToken ct = default)
    {
        var book = await _index.GetBookAsync(bookId, ct) ?? throw new InvalidOperationException($"Book {bookId} not found.");
        var candidates = await _identifier.FromComicVineLinkAsync(book.Metadata, link, ct);
        if (candidates.Count > 0)
        {
            await SetStateAsync(bookId, IdentificationState.NeedsReview, new IdentificationResult(BookQuery.From(book.Metadata), candidates, []), null, ct);
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
    }

    private static string? MessageOf(IdentificationResult result) => result.Notes.Count == 0 ? null : string.Join(';', result.Notes);

    private async Task SetStateAsync(string bookId, IdentificationState state, IdentificationResult? result, string? message, CancellationToken ct)
    {
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
