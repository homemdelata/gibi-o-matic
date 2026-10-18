using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Core.Settings;
using GibiOMatic.Library.Index;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Library.Import;

/// <summary>
/// The inbox of one library: files are analysed in the background as soon as they are added,
/// wait for the user's confirmation, then are imported one at a time. The queue lives in the
/// index, so closing the app loses nothing; pausing is remembered too.
/// </summary>
public sealed class ImportQueue : IAsyncDisposable
{
    private const string PausedKey = "inbox.paused";

    private static readonly HashSet<string> BookExtensions =
        new([".pdf", ".cbz", ".cbr", ".epub", ".zip", ".rar"], StringComparer.OrdinalIgnoreCase);

    private readonly LibraryInfo _library;
    private readonly LibraryIndex _index;
    private readonly InboxStore _store;
    private readonly ImportAnalyzer _analyzer;
    private readonly BookImporter _importer;
    private readonly ThumbnailCache _thumbnails;
    private readonly ISettingsService _settings;
    private readonly ILogger<ImportQueue> _logger;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private volatile CancellationTokenSource? _current;
    private volatile bool _paused;
    private Task _worker = Task.CompletedTask;

    public ImportQueue(LibraryInfo library, LibraryIndex index, ImportAnalyzer analyzer, BookImporter importer,
        ThumbnailCache thumbnails, ISettingsService settings, ILogger<ImportQueue> logger)
    {
        _library = library;
        _index = index;
        _store = new InboxStore(index);
        _analyzer = analyzer;
        _importer = importer;
        _thumbnails = thumbnails;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Items were added, analysed, imported or removed.</summary>
    public event EventHandler? Changed;

    /// <summary>A book was created or received a new file.</summary>
    public event EventHandler<string>? BookImported;

    /// <summary>Progress of the item being processed (raised often; throttle in the UI).</summary>
    public event EventHandler<ImportProgress?>? ProgressChanged;

    public LibraryInfo Library => _library;

    public bool IsPaused
    {
        get => _paused;
        private set => _paused = value;
    }

    public ImportProgress? Current { get; private set; }

    /// <summary>Puts interrupted items back in line and starts the background worker.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _store.ResetInterruptedAsync(ct);
        // Imported files are in the library now; the inbox keeps them only for the session's summary.
        _thumbnails.DeleteInboxThumbnails(_library, await _store.DeleteImportedAsync(ct));
        IsPaused = await _index.GetStateAsync(PausedKey, ct) == "true";
        _worker = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
    }

    /// <summary>Adds files, and the books found inside folders (recursively). Returns how many were added.</summary>
    public async Task<int> AddAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var open = await _store.GetOpenSourcePathsAsync(ct);
        // A drive root ("G:\") keeps its separator after trimming.
        var libraryRoot = Path.TrimEndingDirectorySeparator(_library.RootPath);
        libraryRoot = Path.EndsInDirectorySeparator(libraryRoot) ? libraryRoot : libraryRoot + Path.DirectorySeparatorChar;
        var toAdd = new List<string>();
        foreach (var file in paths.SelectMany(Expand))
        {
            var full = Path.GetFullPath(file);
            // Files already in the library are never imported again from inside it.
            if (full.StartsWith(libraryRoot, StringComparison.OrdinalIgnoreCase) || !open.Add(full))
            {
                continue;
            }

            toAdd.Add(full);
        }

        if (toAdd.Count > 0)
        {
            await _store.AddAsync(toAdd, ct);
            _logger.LogInformation("Inbox: {Count} files added", toAdd.Count);
            OnChanged();
            _wake.Release();
        }

        return toAdd.Count;
    }

    public async Task<IReadOnlyList<InboxEntry>> GetEntriesAsync(CancellationToken ct = default)
    {
        var items = await _store.GetAllAsync(ct);
        var preference = _settings.Current.FormatPreference;
        var entries = new List<InboxEntry>(items.Count);
        var earlier = new List<InboxItem>();
        foreach (var item in items)
        {
            entries.Add(Describe(item, earlier, preference));
            if (item.Analysis is not null && item.State is not (InboxItemState.Skipped or InboxItemState.Failed))
            {
                earlier.Add(item);
            }
        }

        return entries;
    }

    private static InboxEntry Describe(InboxItem item, List<InboxItem> earlier, IReadOnlyList<BookFormat> preference)
    {
        if (item.Analysis is not { } a)
        {
            return new InboxEntry(item, null, null, ImportDecision.Import, []);
        }

        DuplicateKind? kind = null;
        string? title = null;
        ImportDecision suggested;
        IReadOnlyList<ImportDecision> allowed;

        if (a.Match is { SameFile: true } sameFile)
        {
            (kind, title, suggested, allowed) = (DuplicateKind.SameFileInLibrary, sameFile.BookTitle, ImportDecision.Skip, [ImportDecision.Skip]);
        }
        else if (earlier.FirstOrDefault(e => e.Analysis!.Sha256 == a.Sha256) is { } twin)
        {
            (kind, title, suggested, allowed) = (DuplicateKind.SameFileInList, twin.Analysis!.Title, ImportDecision.Skip, [ImportDecision.Skip]);
        }
        else if (a.Match is { } sameBook)
        {
            kind = DuplicateKind.SameBookInLibrary;
            title = sameBook.BookTitle;
            suggested = BookIdentity.SuggestForSameBook(a.Format, a.Metadata.LooksConverted, sameBook, preference);
            allowed = [ImportDecision.Replace, ImportDecision.KeepBoth, ImportDecision.Skip];
        }
        else if (earlier.FirstOrDefault(e => BookIdentity.IsSameBook(e.Analysis!, a)) is { } sibling)
        {
            (kind, title, suggested, allowed) = (DuplicateKind.SameBookInList, sibling.Analysis!.Title, ImportDecision.KeepBoth, [ImportDecision.KeepBoth, ImportDecision.Skip]);
        }
        else
        {
            (suggested, allowed) = (ImportDecision.Import, [ImportDecision.Import, ImportDecision.Skip]);
        }

        var decision = item.Decision is { } chosen && allowed.Contains(chosen) ? chosen : suggested;
        return new InboxEntry(item, kind, title, decision, allowed);
    }

    public async Task<ImportSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        var items = await _store.GetAllAsync(ct);
        int Count(params InboxItemState[] states) => items.Count(i => states.Contains(i.State));
        return new ImportSummary(
            Count(InboxItemState.Pending, InboxItemState.Analyzing),
            Count(InboxItemState.Ready),
            Count(InboxItemState.Queued, InboxItemState.Importing),
            Count(InboxItemState.Imported),
            Count(InboxItemState.Skipped),
            Count(InboxItemState.Failed));
    }

    public async Task SetDecisionAsync(long itemId, ImportDecision decision, CancellationToken ct = default)
    {
        await _store.SetDecisionAsync(itemId, decision, ct);
        OnChanged();
    }

    /// <summary>
    /// The user's confirmation: every analysed item goes into the import line with its decision
    /// (items to skip are marked skipped right away). Returns how many will be imported.
    /// </summary>
    public async Task<int> ConfirmAsync(CancellationToken ct = default)
    {
        var queued = 0;
        foreach (var entry in await GetEntriesAsync(ct))
        {
            if (entry.Item.State != InboxItemState.Ready)
            {
                continue;
            }

            await _store.SetDecisionAsync(entry.Item.Id, entry.Decision, ct);
            if (entry.Decision == ImportDecision.Skip)
            {
                var reason = entry.Duplicate switch
                {
                    DuplicateKind.SameFileInLibrary => InboxMessage.AlreadyInLibrary,
                    DuplicateKind.SameFileInList => InboxMessage.DuplicateInList,
                    _ => InboxMessage.SkippedByUser,
                };
                await _store.SetStateAsync(entry.Item.Id, InboxItemState.Skipped, ct, message: reason);
            }
            else
            {
                await _store.SetStateAsync(entry.Item.Id, InboxItemState.Queued, ct);
                queued++;
            }
        }

        _logger.LogInformation("Inbox: import confirmed for {Count} files", queued);
        OnChanged();
        _wake.Release();
        return queued;
    }

    /// <summary>"Ignorar todos": analysed items that were not confirmed are skipped.</summary>
    public async Task SkipAllReadyAsync(CancellationToken ct = default)
    {
        foreach (var item in (await _store.GetAllAsync(ct)).Where(i => i.State == InboxItemState.Ready))
        {
            await _store.SetStateAsync(item.Id, InboxItemState.Skipped, ct, message: InboxMessage.SkippedByUser);
        }

        OnChanged();
    }

    /// <summary>Removes imported, skipped and failed items from the list.</summary>
    public async Task ClearFinishedAsync(CancellationToken ct = default)
    {
        var removed = await _store.DeleteFinishedAsync(ct);
        _thumbnails.DeleteInboxThumbnails(_library, removed);
        OnChanged();
    }

    public async Task PauseAsync(CancellationToken ct = default)
    {
        IsPaused = true;
        await _index.SetStateAsync(PausedKey, "true", ct);
        // The item in progress goes back in line and resumes from scratch later.
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

    /// <summary>Waits until nothing is left to analyse or import (for tests and shutdown).</summary>
    public async Task WaitUntilIdleAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var summary = await GetSummaryAsync(ct);
            if (!summary.HasWork || IsPaused)
            {
                return;
            }

            await Task.Delay(25, ct);
        }
    }

    private async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var item = IsPaused
                    ? null
                    : await _store.NextAsync(InboxItemState.Pending, stop) ?? await _store.NextAsync(InboxItemState.Queued, stop);
                if (item is null || IsPaused)
                {
                    await _wake.WaitAsync(stop);
                    continue;
                }

                using var current = CancellationTokenSource.CreateLinkedTokenSource(stop);
                _current = current;
                if (IsPaused)
                {
                    // Paused while the item was being fetched; from here on a pause cancels it instead.
                    _current = null;
                    continue;
                }

                try
                {
                    if (item.State == InboxItemState.Pending)
                    {
                        await AnalyzeAsync(item, current.Token);
                    }
                    else
                    {
                        await ImportAsync(item, current.Token);
                    }
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                    // Paused: put the item back where it was.
                    await _store.SetStateAsync(item.Id, item.State, stop);
                }
                finally
                {
                    _current = null;
                    SetProgress(null);
                    OnChanged();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never let the worker die: log, wait a bit and go on with the next item.
                _logger.LogError(ex, "Inbox worker error");
                await Task.Delay(TimeSpan.FromSeconds(1), stop).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    private async Task AnalyzeAsync(InboxItem item, CancellationToken ct)
    {
        await _store.SetStateAsync(item.Id, InboxItemState.Analyzing, ct);
        OnChanged();
        var name = Path.GetFileName(item.SourcePath);
        try
        {
            var outcome = await _analyzer.AnalyzeAsync(_library, _index, item.Id, item.SourcePath,
                Progress(item.Id, InboxItemState.Analyzing, name), ct);
            if (outcome.Analysis is { } analysis)
            {
                await _store.SetStateAsync(item.Id, InboxItemState.Ready, ct, analysis);
            }
            else
            {
                await _store.SetStateAsync(item.Id, InboxItemState.Failed, ct, message: outcome.Failure, detail: outcome.Detail);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not analyse {Path}", item.SourcePath);
            await _store.SetStateAsync(item.Id, InboxItemState.Failed, ct, message: InboxMessage.IoError, detail: ex.Message);
        }
    }

    private async Task ImportAsync(InboxItem item, CancellationToken ct)
    {
        if (item.Analysis is not { } analysis)
        {
            await _store.SetStateAsync(item.Id, InboxItemState.Pending, ct);
            return;
        }

        await _store.SetStateAsync(item.Id, InboxItemState.Importing, ct);
        OnChanged();
        try
        {
            var outcome = await _importer.ImportAsync(_library, _index, item.SourcePath, analysis,
                item.Decision ?? ImportDecision.Import, _settings.Current.FormatPreference,
                Progress(item.Id, InboxItemState.Importing, analysis.FileName), ct);
            await _store.SetStateAsync(item.Id, outcome.State, ct, bookId: outcome.BookId, message: outcome.Message, detail: outcome.Detail);
            if (outcome.State == InboxItemState.Imported && outcome.BookId is { } bookId)
            {
                BookImported?.Invoke(this, bookId);
            }
        }
        catch (ImportException ex)
        {
            _logger.LogWarning("Import of {Path} failed: {Reason} {Detail}", item.SourcePath, ex.Reason, ex.Detail);
            await _store.SetStateAsync(item.Id, InboxItemState.Failed, ct, message: ex.Reason, detail: ex.Detail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Import of {Path} failed", item.SourcePath);
            await _store.SetStateAsync(item.Id, InboxItemState.Failed, ct, message: InboxMessage.IoError, detail: ex.Message);
        }
    }

    private IProgress<double> Progress(long itemId, InboxItemState phase, string fileName)
    {
        var last = -1.0;
        return new SyncProgress(fraction =>
        {
            // About a hundred updates per file at most.
            if (fraction - last >= 0.01 || fraction >= 1)
            {
                last = fraction;
                SetProgress(new ImportProgress(itemId, phase, fileName, fraction));
            }
        });
    }

    private void SetProgress(ImportProgress? progress)
    {
        Current = progress;
        ProgressChanged?.Invoke(this, progress);
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static IEnumerable<string> Expand(string path)
    {
        if (File.Exists(path))
        {
            return [path];
        }

        if (!Directory.Exists(path))
        {
            return [];
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        // Inside folders only likely books are picked (covers, .nfo, .txt… are left out); the
        // format is still confirmed by content. A file dropped on its own is always analysed.
        return Directory.EnumerateFiles(path, "*", options)
            .Where(f => !Path.GetFileName(f).StartsWith('.') && BookExtensions.Contains(Path.GetExtension(f)))
            .Order(StringComparer.OrdinalIgnoreCase);
    }

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

    /// <summary>Unlike <see cref="Progress{T}"/>, reports on the calling thread (no posting to a context).</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
