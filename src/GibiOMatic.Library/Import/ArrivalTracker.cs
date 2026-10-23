using GibiOMatic.Core.Domain;
using GibiOMatic.Library.Identification;
using GibiOMatic.Library.Index;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Library.Import;

/// <summary>What happened to a batch of files from the watched folder.</summary>
/// <param name="Files">Files processed.</param>
/// <param name="Approved">Books that came in or got a new file and are approved (automatically or before).</param>
/// <param name="ToReview">Books that wait for the user in the Review.</param>
/// <param name="AlreadyInLibrary">Files that were already in the library.</param>
/// <param name="Failed">Files that could not be imported.</param>
public sealed record ArrivalSummary(int Files, int Approved, int ToReview, int AlreadyInLibrary, int Failed);

/// <summary>
/// Follows the files of the watched folder (F5) through import and identification and, once a
/// batch is done, reports it in one summary (decision of 2026-10-23: one notification per batch,
/// not one per book). A batch ends when nothing arrived for <see cref="QuietTime"/>, no file of
/// the folder is still in the inbox and its books are identified.
/// </summary>
public sealed class ArrivalTracker : IDisposable
{
    private readonly ImportQueue _imports;
    private readonly IdentificationQueue _identification;
    private readonly LibraryIndex _index;
    private readonly ILogger<ArrivalTracker> _logger;
    private readonly Lock _gate = new();
    private readonly List<InboxItemFinished> _finished = [];
    private DateTimeOffset _lastActivity;
    private Timer? _timer;
    private int _checking;
    private bool _disposed;

    public ArrivalTracker(ImportQueue imports, IdentificationQueue identification, LibraryIndex index, InboxWatchOptions options,
        ILogger<ArrivalTracker> logger)
    {
        QuietTime = options.QuietTime;
        _imports = imports;
        _identification = identification;
        _index = index;
        _logger = logger;
        imports.ItemFinished += OnItemFinished;
    }

    /// <summary>Files arriving close together make one batch.</summary>
    public TimeSpan QuietTime { get; }

    /// <summary>Raised on a background thread when a batch is done.</summary>
    public event EventHandler<ArrivalSummary>? Completed;

    private void OnItemFinished(object? sender, InboxItemFinished e)
    {
        if (!e.Auto)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _finished.Add(e);
            _lastActivity = DateTimeOffset.UtcNow;
            _timer ??= new Timer(_ => _ = CheckAsync(), null, QuietTime, QuietTime);
        }
    }

    /// <summary>Reports the batch if it is done (the timer calls it; tests too).</summary>
    public async Task CheckAsync()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1)
        {
            return;
        }

        try
        {
            List<InboxItemFinished> batch;
            lock (_gate)
            {
                if (_finished.Count == 0 || DateTimeOffset.UtcNow - _lastActivity < QuietTime)
                {
                    return;
                }

                batch = [.. _finished];
            }

            if (await _imports.CountOpenAutoAsync() > 0)
            {
                return;
            }

            var bookIds = batch.Where(f => f.State == InboxItemState.Imported && f.BookId is not null).Select(f => f.BookId!).Distinct().ToList();
            var approved = 0;
            var toReview = 0;
            foreach (var bookId in bookIds)
            {
                var book = await _index.GetBookAsync(bookId);
                if (book is null)
                {
                    continue;
                }

                if (book.Metadata.Review.Status == ReviewStatus.Approved)
                {
                    approved++;
                    continue;
                }

                // Still to be identified: wait, unless identification is paused (then it waits for the user anyway).
                var state = (await _identification.GetAsync(bookId))?.State;
                if (!_identification.IsPaused && state is null or IdentificationState.Queued or IdentificationState.Searching)
                {
                    return;
                }

                toReview++;
            }

            lock (_gate)
            {
                // Files that finished meanwhile belong to the next check.
                if (_finished.Count != batch.Count)
                {
                    return;
                }

                _finished.Clear();
                _timer?.Dispose();
                _timer = null;
            }

            var summary = new ArrivalSummary(
                batch.Count,
                approved,
                toReview,
                batch.Count(f => f.State == InboxItemState.Skipped),
                batch.Count(f => f.State == InboxItemState.Failed));
            _logger.LogInformation("Inbox batch done: {Summary}", summary);
            Completed?.Invoke(this, summary);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check the inbox batch");
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    public void Dispose()
    {
        _imports.ItemFinished -= OnItemFinished;
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
