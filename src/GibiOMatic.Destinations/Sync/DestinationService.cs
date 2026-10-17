using System.Threading.Channels;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Core.Settings;
using GibiOMatic.Library;
using GibiOMatic.Library.Index;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Destinations.Sync;

/// <summary>How a book's copy is in a destination.</summary>
public enum CopyState
{
    /// <summary>The book is not in this destination ("quais livros" or not reviewed yet).</summary>
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

/// <summary>Progress of the sync being run.</summary>
public sealed record SyncProgress(string DestinationId, int Done, int Total, string Title);

/// <summary>
/// The destinations of one library. Batch syncs run only after the user saw the plan and
/// confirmed it; once a destination had its first confirmed sync, automatic destinations follow
/// each saved book on their own (decision of 2026-10-17). One job at a time, in the background.
/// </summary>
public sealed class DestinationService : IAsyncDisposable
{
    private readonly LibraryInfo _library;
    private readonly LibraryIndex _index;
    private readonly IReadOnlyDictionary<string, IDestinationType> _types;
    private readonly ISettingsService _settings;
    private readonly ILogger<DestinationService> _logger;
    private readonly DestinationSync _sync;
    private readonly Channel<Func<CancellationToken, Task>> _jobs = Channel.CreateUnbounded<Func<CancellationToken, Task>>();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, SyncResult> _lastResults = [];
    private Task _worker = Task.CompletedTask;
    private int _pending;

    public DestinationService(LibraryInfo library, LibraryIndex index, IEnumerable<IDestinationType> types, ISettingsService settings,
        ILogger<DestinationService> logger)
    {
        _library = library;
        _index = index;
        _types = types.ToDictionary(t => t.Id);
        _settings = settings;
        _logger = logger;
        _sync = new DestinationSync(library, index, logger);
    }

    public event EventHandler? Changed;

    public event EventHandler<SyncProgress?>? ProgressChanged;

    public SyncProgress? Current { get; private set; }

    /// <summary>Jobs waiting or running.</summary>
    public bool IsBusy => Volatile.Read(ref _pending) > 0;

    public IReadOnlyList<IDestination> Destinations =>
        _settings.Current.Destinations.Where(d => _types.ContainsKey(d.Type)).Select(d => _types[d.Type].Create(d)).ToList();

    public IDestination? Find(string destinationId) => Destinations.FirstOrDefault(d => d.Config.Id == destinationId);

    public void Start() => _worker = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);

    public async Task<SyncPlan> PlanAsync(string destinationId, CancellationToken ct = default) =>
        await _sync.PlanAsync(Find(destinationId) ?? throw new InvalidOperationException($"Unknown destination {destinationId}."), ct);

    /// <summary>Runs a plan the user confirmed; afterwards the destination follows edits on its own.</summary>
    public void Run(SyncPlan plan) => Enqueue(async ct =>
    {
        if (Find(plan.DestinationId) is not { } destination || destination.GetStatus().Health != DestinationHealth.Ready)
        {
            return;
        }

        var result = await _sync.ExecuteAsync(destination, plan, Progress(destination), ct);
        _lastResults[destination.Config.Id] = result;
        await _index.SetStateAsync(ConfirmedKey(destination.Config.Id), "true", ct);
    });

    /// <summary>A book was imported, approved or edited: automatic destinations update its copy.</summary>
    public void OnBookChanged(string bookId) => Enqueue(async ct =>
    {
        foreach (var destination in Destinations.Where(d => d.Config.When == DestinationTiming.Automatic))
        {
            if (!await IsConfirmedAsync(destination.Config.Id, ct) || destination.GetStatus().Health != DestinationHealth.Ready)
            {
                continue;
            }

            var plan = await _sync.PlanAsync(destination, ct, bookId);
            if (!plan.IsEmpty)
            {
                _lastResults[destination.Config.Id] = await _sync.ExecuteAsync(destination, plan, Progress(destination), ct);
            }
        }
    });

    public async Task<bool> IsConfirmedAsync(string destinationId, CancellationToken ct = default) =>
        await _index.GetStateAsync(ConfirmedKey(destinationId), ct) == "true";

    public SyncResult? LastResult(string destinationId) => _lastResults.GetValueOrDefault(destinationId);

    /// <summary>What the destination did, newest first (for its history).</summary>
    public Task<IReadOnlyList<DestinationLogEntry>> HistoryAsync(string destinationId, int limit = 200, CancellationToken ct = default) =>
        _sync.HistoryAsync(destinationId, limit, ct);

    public async Task<DestinationSummary> SummaryAsync(IDestination destination, CancellationToken ct = default)
    {
        var plan = await _sync.PlanAsync(destination, ct);
        var recorded = await _sync.RecordedAsync(destination.Config.Id, ct);
        var included = plan.UpToDate + plan.Creates + plan.Updates;
        return new DestinationSummary(destination.Config, destination.GetStatus(), await IsConfirmedAsync(destination.Config.Id, ct),
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

    private void Enqueue(Func<CancellationToken, Task> job)
    {
        Interlocked.Increment(ref _pending);
        _jobs.Writer.TryWrite(job);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private IProgress<(int Done, int Total, string Title)> Progress(IDestination destination) =>
        new SyncProgressReporter(p =>
        {
            Current = new SyncProgress(destination.Config.Id, p.Done, p.Total, p.Title);
            ProgressChanged?.Invoke(this, Current);
        });

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
                    _logger.LogError(ex, "Destination job failed");
                }
                finally
                {
                    Interlocked.Decrement(ref _pending);
                    Current = null;
                    ProgressChanged?.Invoke(this, null);
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _jobs.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private sealed class SyncProgressReporter(Action<(int Done, int Total, string Title)> report) : IProgress<(int Done, int Total, string Title)>
    {
        public void Report((int Done, int Total, string Title) value) => report(value);
    }
}

/// <summary>Creates the destination service of an open library.</summary>
public sealed class DestinationServiceFactory(LibraryService libraries, IEnumerable<IDestinationType> types, ISettingsService settings,
    ILogger<DestinationService> logger)
{
    public DestinationService Create(LibraryInfo library) => new(library, libraries.GetIndex(library), types, settings, logger);
}
