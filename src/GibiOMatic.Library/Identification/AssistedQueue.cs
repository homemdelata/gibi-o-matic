using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Core.Settings;
using GibiOMatic.Library.Index;
using GibiOMatic.Metadata.Assisted;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Library.Identification;

/// <summary>
/// The runs of the user's Claude Code for one library (SPEC §8.4, "When it runs"): one at a time,
/// whether asked for one book, for a group, for a batch, or automatically for the books that go to
/// the Review. Each answer is checked, stored, joins the book's candidates and, if the user allowed
/// it, may approve the book.
/// </summary>
public sealed class AssistedQueue : IAsyncDisposable
{
    private readonly LibraryInfo _library;
    private readonly LibraryIndex _index;
    private readonly IdentificationQueue _identification;
    private readonly AssistedIdentifier _identifier;
    private readonly ISettingsService _settings;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly LinkedList<(string BookId, AssistedLevel Level)> _batch = new();
    private CancellationTokenSource _batchCancel = new();
    private Task _worker = Task.CompletedTask;

    public AssistedQueue(LibraryInfo library, LibraryIndex index, IdentificationQueue identification, AssistedIdentifier identifier,
        ISettingsService settings, ILogger<AssistedQueue> logger)
    {
        _library = library;
        _index = index;
        _identification = identification;
        _identifier = identifier;
        _settings = settings;
        _logger = logger;
        identification.ReviewNeeded += OnReviewNeeded;
    }

    /// <summary>A run started or ended, or the batch changed. Raised on a background thread.</summary>
    public event EventHandler? Changed;

    /// <summary>A book's answer was stored (its candidates changed). Raised on a background thread.</summary>
    public event EventHandler<string>? BookAnswered;

    public AssistedStore Store => _identification.Assisted;

    /// <summary>What is running now ("Saga, Vol. 3"), or null.</summary>
    public string? CurrentTitle { get; private set; }

    /// <summary>Books waiting in the batch (not counting the one running).</summary>
    public int Pending
    {
        get
        {
            lock (_batch)
            {
                return _batch.Count;
            }
        }
    }

    /// <summary>Books of the current batch already answered.</summary>
    public int Done { get; private set; }

    public bool IsBusy => CurrentTitle is not null || Pending > 0;

    public Task<ClaudeStatus> GetStatusAsync(CancellationToken ct = default) => _identifier.GetStatusAsync(ct);

    public void Start() => _worker = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);

    /// <summary>"Ask Claude about N books": they join the batch, one run each.</summary>
    public int Enqueue(IEnumerable<string> bookIds, AssistedLevel level = AssistedLevel.Quick)
    {
        var added = 0;
        lock (_batch)
        {
            if (_batch.Count == 0 && CurrentTitle is null)
            {
                Done = 0;
            }

            foreach (var id in bookIds.Distinct())
            {
                if (_batch.All(b => b.BookId != id))
                {
                    _batch.AddLast((id, level));
                    added++;
                }
            }
        }

        if (added > 0)
        {
            _logger.LogInformation("Claude: {Count} books queued ({Level})", added, level);
            OnChanged();
            _wake.Release();
        }

        return added;
    }

    /// <summary>Stops the batch: the book running is cancelled, the waiting ones are dropped.</summary>
    public void CancelBatch()
    {
        lock (_batch)
        {
            _batch.Clear();
        }

        _batchCancel.Cancel();
        OnChanged();
    }

    /// <summary>Asks Claude about one book now (after the run in progress, if any).</summary>
    public async Task<AssistedBookResult?> RunBookAsync(string bookId, AssistedLevel level, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await ProcessAsync(bookId, level, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Asks Claude about a whole group of the Review by series; the result is stored for the group.</summary>
    public async Task<AssistedGroupResult> RunGroupAsync(ReviewGroup group, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            CurrentTitle = group.Name;
            OnChanged();
            var result = await _identifier.IdentifyGroupAsync(_library, group, ct);
            if (result.Failed)
            {
                return result;
            }

            await Store.SaveGroupAsync(result, ct);

            // Every checked row is also a candidate of its book, for the Review by book.
            foreach (var row in result.Rows.Where(r => r.Candidate is not null))
            {
                await Store.SaveAsync(new AssistedBookResult(row.BookId, AssistedLevel.Group, AssistedOutcome.Found, row.Candidate, null, null,
                    TimeSpan.Zero, 0), ct);
                await _identification.ApplyAssistedAsync(row.BookId, ct);
                BookAnswered?.Invoke(this, row.BookId);
            }

            return result;
        }
        finally
        {
            CurrentTitle = null;
            _gate.Release();
            OnChanged();
        }
    }

    private async Task<AssistedBookResult?> ProcessAsync(string bookId, AssistedLevel level, CancellationToken ct)
    {
        if (await _index.GetBookAsync(bookId, ct) is not { } book)
        {
            return null;
        }

        CurrentTitle = book.Metadata.Title;
        OnChanged();
        try
        {
            var current = await _identification.GetAsync(bookId, ct);
            var previous = level == AssistedLevel.Deep
                ? (await Store.GetAsync(bookId, ct)).FirstOrDefault(r => r.Level == AssistedLevel.Quick)?.Answer
                : null;
            var result = await _identifier.IdentifyBookAsync(_library, book, current?.Result, level, previous, ct);
            if (result.Outcome == AssistedOutcome.Failed)
            {
                return result;
            }

            await Store.SaveAsync(result, ct);
            await _identification.ApplyAssistedAsync(bookId, ct);
            BookAnswered?.Invoke(this, bookId);

            var config = _settings.Current.Claude;
            if (level == AssistedLevel.Quick && config.DeepAfterQuick && AssistedRecords.NeedsDeep(result.Candidate, config.AutoApproveMinimum))
            {
                _logger.LogInformation("Claude: the quick answer for book {BookId} asks for a deep search", bookId);
                return await ProcessAsync(bookId, AssistedLevel.Deep, ct);
            }

            if (result.Candidate is { } candidate && book.Metadata.Review.Status == ReviewStatus.Pending && config.AutoApprove)
            {
                await _identification.AutoApproveAssistedAsync(bookId, candidate, ct);
            }

            return result;
        }
        finally
        {
            CurrentTitle = null;
            OnChanged();
        }
    }

    private void OnReviewNeeded(object? sender, string bookId)
    {
        var config = _settings.Current.Claude;
        if (config.Enabled && config.When == ClaudeRunMode.Automatic)
        {
            _ = EnqueueIfNewAsync(bookId);
        }
    }

    private async Task EnqueueIfNewAsync(string bookId)
    {
        try
        {
            if (!(await Store.GetAnsweredAsync(_stop.Token)).Contains(bookId))
            {
                Enqueue([bookId]);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not queue book {BookId} for Claude", bookId);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                (string BookId, AssistedLevel Level)? next = null;
                lock (_batch)
                {
                    if (_batch.First is { } first)
                    {
                        next = first.Value;
                        _batch.RemoveFirst();
                    }
                }

                if (next is null)
                {
                    await _wake.WaitAsync(stop);
                    continue;
                }

                if (!_settings.Current.Claude.Enabled)
                {
                    lock (_batch)
                    {
                        _batch.Clear();
                    }

                    OnChanged();
                    continue;
                }

                if (_batchCancel.IsCancellationRequested)
                {
                    _batchCancel.Dispose();
                    _batchCancel = new CancellationTokenSource();
                }

                using var run = CancellationTokenSource.CreateLinkedTokenSource(stop, _batchCancel.Token);
                await _gate.WaitAsync(run.Token);
                try
                {
                    var result = await ProcessAsync(next.Value.BookId, next.Value.Level, run.Token);
                    Done++;

                    // Not logged in or not installed: the rest of the batch would fail the same way.
                    if (result is { Outcome: AssistedOutcome.Failed, Message: "not-installed" or "not-logged-in" })
                    {
                        CancelBatch();
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // The batch was cancelled.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Claude worker error");
            }
            finally
            {
                OnChanged();
            }
        }
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public async ValueTask DisposeAsync()
    {
        _identification.ReviewNeeded -= OnReviewNeeded;
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
        _batchCancel.Dispose();
        _wake.Dispose();
        _gate.Dispose();
    }
}

/// <summary>Creates the Claude queue of an open library.</summary>
public sealed class AssistedQueueFactory(LibraryService libraries, AssistedIdentifier identifier, ISettingsService settings, ILogger<AssistedQueue> logger)
{
    public AssistedQueue Create(LibraryInfo library, IdentificationQueue identification) =>
        new(library, libraries.GetIndex(library), identification, identifier, settings, logger);
}
