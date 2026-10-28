using System.Collections.Concurrent;
using System.Threading.Channels;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Core.Settings;
using GibiOMatic.Destinations.Kindle;
using GibiOMatic.Library;
using GibiOMatic.Library.Index;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Destinations.Sync;

/// <summary>How a book's copy is in a destination.</summary>
public enum CopyState
{
    /// <summary>The book is not in this destination ("which books" or not reviewed yet).</summary>
    NotIncluded,
    NotGenerated,
    UpToDate,
    Outdated,
}

public sealed record DestinationSummary(
    DestinationConfig Config,
    DestinationStatus Status,
    bool Confirmed,
    int Included,
    int UpToDate,
    int Pending,
    DateTimeOffset? LastWrite);

/// <summary>Progress of the sync being run in one destination.</summary>
/// <param name="FileFraction">How far the current copy got (0–1).</param>
/// <param name="Queued">Books changed during this run; the next run takes them.</param>
/// <param name="IsFinished">The destination's job ended (nothing of it is running now).</param>
public sealed record SyncProgress(string DestinationId, int Done, int Total, string Title, double FileFraction = 0, int Queued = 0,
    bool IsFinished = false);

/// <summary>
/// The destinations of one library. Batch syncs run only after the user saw the plan and
/// confirmed it; once a destination had its first confirmed sync, automatic destinations follow
/// each saved book on their own (decision of 2026-10-17). Each destination has its own queue and
/// works one job at a time, in the background, so a long Komga run does not hold the Kindle
/// (decision of 2026-10-18). Changed books are taken together: one run for all that are waiting,
/// and one server scan when nothing else waits.
/// </summary>
public sealed class DestinationService : IAsyncDisposable
{
    private readonly LibraryInfo _library;
    private readonly LibraryIndex _index;
    private readonly IReadOnlyDictionary<string, IDestinationType> _types;
    private readonly ISettingsService _settings;
    private readonly ILogger<DestinationService> _logger;
    private readonly DestinationSync _sync;
    private readonly PendingBookStore _pendingBooks;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, SyncResult> _lastResults = new();
    private readonly ConcurrentDictionary<string, Lazy<Worker>> _workers = new();
    private readonly ConcurrentDictionary<string, DestinationHealth> _health = new();
    private readonly Lock _gate = new();
    private bool _started;

    /// <summary>Book changes being recorded (busy before their run is in line).</summary>
    private int _recording;

    public DestinationService(LibraryInfo library, LibraryIndex index, IEnumerable<IDestinationType> types, ISettingsService settings,
        ILogger<DestinationService> logger)
    {
        _library = library;
        _index = index;
        _types = types.ToDictionary(t => t.Id);
        _settings = settings;
        _logger = logger;
        _sync = new DestinationSync(library, index, logger);
        _pendingBooks = new PendingBookStore(index);
        Kindle = new KindleSender(index, logger);
    }

    /// <summary>Sending prepared books to a connected Kindle, and the Kindle manager's deletions.</summary>
    public KindleSender Kindle { get; }

    public event EventHandler? Changed;

    /// <summary>Progress of one destination's job; <see cref="SyncProgress.IsFinished"/> when it ends.</summary>
    public event EventHandler<SyncProgress>? ProgressChanged;

    /// <summary>What the destination is doing now, if anything.</summary>
    public SyncProgress? CurrentOf(string destinationId) =>
        _workers.TryGetValue(destinationId, out var worker) ? worker.Value.Current : null;

    /// <summary>Jobs waiting or running in any destination.</summary>
    public bool IsBusy => Volatile.Read(ref _recording) > 0 || _workers.Values.Any(w => w.Value.IsBusy);

    /// <summary>Jobs waiting or running in this destination.</summary>
    public bool IsBusyOf(string destinationId) => _workers.TryGetValue(destinationId, out var worker) && worker.Value.IsBusy;

    public IReadOnlyList<IDestination> Destinations =>
        _settings.Current.Destinations.Where(d => _types.ContainsKey(d.Type)).Select(d => _types[d.Type].Create(d)).ToList();

    /// <summary>A destination was added, changed or removed in the settings (the counts follow).</summary>
    public void NotifyDestinationsChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public IDestination? Find(string destinationId) => Destinations.FirstOrDefault(d => d.Config.Id == destinationId);

    /// <summary>Starts working; books changed in an earlier session and not updated yet go first.</summary>
    public void Start()
    {
        lock (_gate)
        {
            _started = true;
        }

        Interlocked.Increment(ref _recording);
        _ = Task.Run(async () =>
        {
            try
            {
                // Pending books of the single queue of earlier versions go to every automatic destination.
                var shared = await _pendingBooks.TakeSharedAsync(_stop.Token);
                foreach (var destination in Destinations.Where(d => d.Config.When == DestinationTiming.Automatic))
                {
                    foreach (var bookId in shared)
                    {
                        await _pendingBooks.AddAsync(destination.Config.Id, bookId, _stop.Token);
                    }

                    RequestAutomaticRun(destination.Config.Id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Could not read the pending books");
            }
            finally
            {
                Interlocked.Decrement(ref _recording);
            }
        });
        _ = Task.Run(() => WatchHealthAsync(_stop.Token), CancellationToken.None);
    }

    /// <summary>How often the destination folders are checked (a network drive that went away or came back).</summary>
    public TimeSpan HealthCheckInterval { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Nothing else notices when a network drive comes back, so the folders are checked now and then;
    /// a check that does not answer holds only this loop. Not awaited on dispose for the same reason.
    /// </summary>
    private async Task WatchHealthAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(HealthCheckInterval);
        try
        {
            do
            {
                foreach (var destination in Destinations)
                {
                    try
                    {
                        Observe(destination, destination.GetStatus().Health);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Could not check destination {Destination}", destination.Config.Id);
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Records what a check found. A change is announced (the sidebar and the page follow), and a
    /// destination that is ready again takes the books that waited for it.
    /// </summary>
    private void Observe(IDestination destination, DestinationHealth health)
    {
        var id = destination.Config.Id;
        var known = _health.TryGetValue(id, out var before);
        _health[id] = health;
        if (!known || before == health)
        {
            return;
        }

        _logger.LogInformation("Destination {Destination} is now {Health}", id, health);
        if (health == DestinationHealth.Ready && destination.Config.When == DestinationTiming.Automatic)
        {
            RequestAutomaticRun(id);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Changes that come close together (a batch edit) wait this long to go in the same run.</summary>
    public TimeSpan GatherDelay { get; init; } = TimeSpan.FromSeconds(1.5);

    // Planning and the summary check the destination folder (a network drive that does not answer
    // takes up to a minute), so they never run on the caller's (UI) thread.
    public Task<SyncPlan> PlanAsync(string destinationId, CancellationToken ct = default)
    {
        var destination = Find(destinationId) ?? throw new InvalidOperationException($"Unknown destination {destinationId}.");
        return Task.Run(() => _sync.PlanAsync(destination, ct), ct);
    }

    /// <summary>Runs a plan the user confirmed; afterwards the destination follows edits on its own.</summary>
    public void Run(SyncPlan plan) => WorkerOf(plan.DestinationId).Enqueue(async ct =>
    {
        if (Find(plan.DestinationId) is not { } destination || destination.GetStatus().Health != DestinationHealth.Ready)
        {
            return;
        }

        var worker = WorkerOf(destination.Config.Id);
        var result = await _sync.ExecuteAsync(destination, plan, Progress(destination), ct);
        _lastResults[destination.Config.Id] = result;
        await _index.SetStateAsync(ConfirmedKey(destination.Config.Id), "true", ct);
        if (result.Written + result.Deleted == 0 && worker.ScanOwed)
        {
            await _sync.NotifyServerAsync(destination, ct);
        }

        worker.ScanOwed = false;
    });

    /// <summary>
    /// A book was imported, approved or edited: automatic destinations update its copy. With
    /// <paramref name="onlyDestinationId"/>, only that one (the book entered or left its list).
    /// </summary>
    public void OnBookChanged(string bookId, string? onlyDestinationId = null)
    {
        var automatic = Destinations.Where(d => d.Config.When == DestinationTiming.Automatic && (onlyDestinationId is null || d.Config.Id == onlyDestinationId))
            .Select(d => d.Config.Id).ToList();
        foreach (var id in automatic)
        {
            var worker = WorkerOf(id);
            worker.Arrived[bookId] = 0;
            if (worker.Current is { IsFinished: false } current)
            {
                // "3 of 10 · 2 more queued" while a run is going.
                worker.Current = current with { Queued = worker.Arrived.Count };
                ProgressChanged?.Invoke(this, worker.Current);
            }
        }

        // Busy from now on, not only once the run is in line.
        Interlocked.Increment(ref _recording);
        _ = RecordAsync(bookId, automatic);
    }

    /// <summary>Recorded in the index first: the run may wait behind long ones, and the app may close before it.</summary>
    private async Task RecordAsync(string bookId, IReadOnlyList<string> destinationIds)
    {
        try
        {
            foreach (var id in destinationIds)
            {
                try
                {
                    await _pendingBooks.AddAsync(id, bookId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // The book is still in memory (Arrived): only a crash before its run would lose it.
                    _logger.LogError(ex, "Could not record book {BookId} as pending for {Destination}", bookId, id);
                }

                RequestAutomaticRun(id);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _recording);
            // The counts ("4 to prepare") follow every book, not only the first of a run.
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>One automatic run in line at most per destination: it takes every book waiting when it starts.</summary>
    private void RequestAutomaticRun(string destinationId)
    {
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }
        }

        var worker = WorkerOf(destinationId);
        if (Interlocked.Exchange(ref worker.AutomaticRunQueued, 1) == 0)
        {
            worker.Enqueue(ct => RunAutomaticAsync(destinationId, ct));
        }
    }

    /// <summary>
    /// Updates the copies of every book waiting for this destination. The books stay pending while
    /// the destination is unavailable (a disconnected drive, no KCC), so a later start tries again.
    /// The server is asked to scan once nothing else waits.
    /// </summary>
    private async Task RunAutomaticAsync(string destinationId, CancellationToken ct)
    {
        await Task.Delay(GatherDelay, ct);
        var worker = WorkerOf(destinationId);
        Volatile.Write(ref worker.AutomaticRunQueued, 0);
        var startedAt = DateTimeOffset.UtcNow;
        var arrived = worker.Arrived.Keys.ToList();
        foreach (var id in arrived)
        {
            worker.Arrived.TryRemove(id, out _);
        }

        var bookIds = (await _pendingBooks.GetAsync(destinationId, ct)).Concat(arrived).ToHashSet();
        if (bookIds.Count == 0 || Find(destinationId) is not { } destination || destination.Config.When != DestinationTiming.Automatic)
        {
            await ForgetPendingAsync(destinationId, bookIds, startedAt, ct);
            return;
        }

        // Before the first confirmed sync there is nothing to follow: that sync plans every book.
        if (!await IsConfirmedAsync(destinationId, ct))
        {
            await ForgetPendingAsync(destinationId, bookIds, startedAt, ct);
            return;
        }

        if (destination.GetStatus().Health != DestinationHealth.Ready)
        {
            return;
        }

        var plan = await _sync.PlanAsync(destination, ct, bookIds);
        if (!plan.IsEmpty)
        {
            var result = await _sync.ExecuteAsync(destination, plan, Progress(destination), ct, notifyServer: false);
            _lastResults[destinationId] = result;
            if (result.Written + result.Deleted > 0)
            {
                worker.ScanOwed = true;
            }
        }

        await ForgetPendingAsync(destinationId, bookIds, startedAt, ct);
        if (Volatile.Read(ref worker.AutomaticRunQueued) == 0 && worker.ScanOwed)
        {
            worker.ScanOwed = false;
            await _sync.NotifyServerAsync(destination, ct);
        }
    }

    private async Task ForgetPendingAsync(string destinationId, IEnumerable<string> bookIds, DateTimeOffset handledAt, CancellationToken ct)
    {
        foreach (var bookId in bookIds)
        {
            await _pendingBooks.RemoveAsync(destinationId, bookId, handledAt, ct);
        }
    }

    /// <summary>
    /// A destination that needs no first confirmed sync: a Kindle only prepares copies in a local
    /// folder, of books the user picked one by one, so it starts preparing at once.
    /// </summary>
    public Task MarkConfirmedAsync(string destinationId, CancellationToken ct = default) =>
        _index.SetStateAsync(ConfirmedKey(destinationId), "true", ct);

    /// <summary>Prepares (again) every book of a destination's list: after the KCC was installed, or to retry failures.</summary>
    public void Refresh(string destinationId)
    {
        foreach (var bookId in Find(destinationId)?.Config.ChosenBookIds ?? [])
        {
            OnBookChanged(bookId, destinationId);
        }
    }

    public async Task<bool> IsConfirmedAsync(string destinationId, CancellationToken ct = default) =>
        await _index.GetStateAsync(ConfirmedKey(destinationId), ct) == "true";

    public SyncResult? LastResult(string destinationId) => _lastResults.GetValueOrDefault(destinationId);

    /// <summary>What the destination did, newest first (for its history).</summary>
    public Task<IReadOnlyList<DestinationLogEntry>> HistoryAsync(string destinationId, int limit = 200, CancellationToken ct = default) =>
        _sync.HistoryAsync(destinationId, limit, ct);

    public Task<DestinationSummary> SummaryAsync(IDestination destination, CancellationToken ct = default) =>
        Task.Run(() => SummarizeAsync(destination, ct), ct);

    private async Task<DestinationSummary> SummarizeAsync(IDestination destination, CancellationToken ct)
    {
        var plan = await _sync.PlanAsync(destination, ct);
        var recorded = await _sync.RecordedAsync(destination.Config.Id, ct);
        var included = plan.UpToDate + plan.Creates + plan.Updates;
        var status = destination.GetStatus();
        Observe(destination, status.Health);
        return new DestinationSummary(destination.Config, status, await IsConfirmedAsync(destination.Config.Id, ct),
            included, plan.UpToDate, plan.Actions.Count, recorded.Values.Select(r => (DateTimeOffset?)r.GeneratedAt).Max());
    }

    /// <summary>The copy of one book in every destination (for the book panel).</summary>
    public async Task<IReadOnlyList<(DestinationConfig Destination, CopyState State, string? Path)>> CopiesOfAsync(CatalogBook book, CancellationToken ct = default)
    {
        var states = new List<(DestinationConfig, CopyState, string?)>();
        foreach (var destination in Destinations)
        {
            var recorded = (await _sync.RecordedAsync(destination.Config.Id, ct)).GetValueOrDefault(book.Metadata.Id);
            var state = !destination.Includes(book.Metadata)
                ? CopyState.NotIncluded
                : recorded is null
                    ? CopyState.NotGenerated
                    : recorded.ContentHash == destination.ContentSignature(book) && recorded.Path == destination.OutputPath(book)
                        ? CopyState.UpToDate
                        : CopyState.Outdated;
            states.Add((destination.Config, state, recorded?.Path));
        }

        return states;
    }

    /// <summary>Forgets a removed destination's copies (the files stay in its folder).</summary>
    public Task ForgetAsync(string destinationId, CancellationToken ct = default) => _sync.ForgetAsync(destinationId, ct);

    /// <summary>Waits until no job is waiting or running (for tests).</summary>
    public async Task WaitUntilIdleAsync(CancellationToken ct = default)
    {
        while (IsBusy)
        {
            await Task.Delay(25, ct);
        }
    }

    private static string ConfirmedKey(string destinationId) => $"destination.{destinationId}.confirmed";

    private Worker WorkerOf(string destinationId) => _workers.GetOrAdd(destinationId, id => new Lazy<Worker>(() => new Worker(this, id))).Value;

    private IProgress<SyncStep> Progress(IDestination destination) =>
        new SyncProgressReporter(p =>
        {
            var worker = WorkerOf(destination.Config.Id);
            worker.Current = new SyncProgress(destination.Config.Id, p.Done, p.Total, p.Title, p.FileFraction, worker.Arrived.Count);
            ProgressChanged?.Invoke(this, worker.Current);
        });

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        foreach (var worker in _workers.Values)
        {
            await worker.Value.StopAsync().ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    private sealed class SyncProgressReporter(Action<SyncStep> report) : IProgress<SyncStep>
    {
        public void Report(SyncStep value) => report(value);
    }

    /// <summary>One destination's queue: its jobs run one after the other, beside the other destinations'.</summary>
    private sealed class Worker
    {
        private readonly DestinationService _service;
        private readonly string _destinationId;
        private readonly Channel<Func<CancellationToken, Task>> _jobs = Channel.CreateUnbounded<Func<CancellationToken, Task>>();
        private readonly Task _loop;
        private int _pending;

        public Worker(DestinationService service, string destinationId)
        {
            _service = service;
            _destinationId = destinationId;
            _loop = Task.Run(() => RunAsync(service._stop.Token), CancellationToken.None);
        }

        /// <summary>Books changed since the current automatic run took its list.</summary>
        public ConcurrentDictionary<string, byte> Arrived { get; } = new();

        public int AutomaticRunQueued;

        /// <summary>Copies were written: the server has to be asked to scan.</summary>
        public bool ScanOwed { get; set; }

        public SyncProgress? Current { get; set; }

        public bool IsBusy => Volatile.Read(ref _pending) > 0;

        public void Enqueue(Func<CancellationToken, Task> job)
        {
            Interlocked.Increment(ref _pending);
            _jobs.Writer.TryWrite(job);
            _service.Changed?.Invoke(_service, EventArgs.Empty);
        }

        private async Task RunAsync(CancellationToken stop)
        {
            try
            {
                await foreach (var job in _jobs.Reader.ReadAllAsync(stop))
                {
                    try
                    {
                        await job(stop);
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _service._logger.LogError(ex, "Job of destination {Destination} failed", _destinationId);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _pending);
                        Current = null;
                        _service.ProgressChanged?.Invoke(_service, new SyncProgress(_destinationId, 0, 0, string.Empty, IsFinished: true));
                        _service.Changed?.Invoke(_service, EventArgs.Empty);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        public async Task StopAsync()
        {
            _jobs.Writer.TryComplete();
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}

/// <summary>Creates the destination service of an open library.</summary>
public sealed class DestinationServiceFactory(LibraryService libraries, IEnumerable<IDestinationType> types, ISettingsService settings,
    ILogger<DestinationService> logger)
{
    /// <summary>See <see cref="DestinationService.GatherDelay"/> (tests shorten it).</summary>
    public TimeSpan GatherDelay { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>See <see cref="DestinationService.HealthCheckInterval"/>.</summary>
    public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(20);

    public DestinationService Create(LibraryInfo library) =>
        new(library, libraries.GetIndex(library), types, settings, logger) { GatherDelay = GatherDelay, HealthCheckInterval = HealthCheckInterval };
}
