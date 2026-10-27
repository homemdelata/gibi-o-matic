using GibiOMatic.App.Resources;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Core.Settings;
using GibiOMatic.Destinations.Sync;
using GibiOMatic.Library;
using GibiOMatic.Library.Editing;
using GibiOMatic.Library.Identification;
using GibiOMatic.Library.Import;
using GibiOMatic.Library.Index;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.App.Services;

/// <summary>
/// The library currently open in the app, with its inbox queue and the watcher of the inbox
/// folder. Opening or creating one remembers it in the settings, so the next start opens it directly.
/// </summary>
public sealed class LibrarySession : IAsyncDisposable, IDisposable
{
    private readonly LibraryService _libraries;
    private readonly ISettingsService _settings;
    private readonly ImportQueueFactory _queues;
    private readonly IdentificationQueueFactory _identifications;
    private readonly DestinationServiceFactory _destinations;
    private readonly BookEditor _editor;
    private readonly InboxWatchOptions _watchOptions;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;

    public LibrarySession(LibraryService libraries, ISettingsService settings, ImportQueueFactory queues,
        IdentificationQueueFactory identifications, DestinationServiceFactory destinations, BookEditor editor,
        InboxWatchOptions watchOptions, ILoggerFactory loggers)
    {
        _libraries = libraries;
        _settings = settings;
        _queues = queues;
        _identifications = identifications;
        _destinations = destinations;
        _editor = editor;
        _watchOptions = watchOptions;
        _loggers = loggers;
        _logger = loggers.CreateLogger<LibrarySession>();
        settings.Saved += OnSettingsSaved;
    }

    public LibraryInfo? Current { get; private set; }

    /// <summary>The inbox of the current library, running in the background.</summary>
    public ImportQueue? Queue { get; private set; }

    /// <summary>Online identification of the current library's books, running in the background.</summary>
    public IdentificationQueue? Identification { get; private set; }

    /// <summary>Komga, Kindle…: copies generated in the background.</summary>
    public DestinationService? Destinations { get; private set; }

    /// <summary>Watches the inbox folder of the settings and feeds the queue (F5).</summary>
    public InboxWatcher? Watcher { get; private set; }

    /// <summary>Raised on a background thread when the watcher starts, stops or loses its folder.</summary>
    public event EventHandler? WatcherChanged;

    /// <summary>Raised on a background thread when a batch from the watched folder is done.</summary>
    public event EventHandler<ArrivalSummary>? ArrivalsCompleted;

    public LibraryIndex? Index => Current is null ? null : _libraries.GetIndex(Current);

    /// <summary>Raised after a different library becomes the current one.</summary>
    public event EventHandler<LibraryInfo>? Changed;

    /// <summary>Books of the current library whose main original is not on disk (deleted or moved outside the app).</summary>
    public IReadOnlySet<string> MissingFiles { get; private set; } = new HashSet<string>();

    /// <summary>Raised, possibly on a background thread, when <see cref="MissingFiles"/> changes.</summary>
    public event EventHandler? MissingFilesChanged;

    /// <summary>Raised, possibly on a background thread, after a book was taken out of the current library.</summary>
    public event EventHandler<string>? BookRemoved;

    // The disk work runs off the UI thread: a library on Google Drive can take seconds to answer.
    public Task<LibraryInfo> CreateAsync(string folder, CancellationToken ct = default) =>
        ActivateAsync(Task.Run(() => _libraries.CreateAsync(folder, ct), ct), ct);

    /// <summary>Opens a library; a cancelled <paramref name="ct"/> stops it from becoming current even if the disk answers later.</summary>
    public Task<LibraryInfo> OpenAsync(string folder, CancellationToken ct = default) =>
        ActivateAsync(Task.Run(() => _libraries.OpenAsync(folder, ct), ct), ct);

    private async Task<LibraryInfo> ActivateAsync(Task<LibraryInfo> opening, CancellationToken ct)
    {
        var library = await opening;
        ct.ThrowIfCancellationRequested();
        if (_settings.Current.LibraryPath != library.RootPath)
        {
            await _settings.SaveAsync(_settings.Current with { LibraryPath = library.RootPath }, ct);
        }

        await StopQueuesAsync();

        var queue = _queues.Create(library);
        var identification = _identifications.Create(library);
        // Every imported book is identified afterwards (decision of 2026-10-17).
        queue.BookImported += (_, bookId) => _ = identification.EnqueueAsync([bookId], force: false);
        var destinationService = _destinations.Create(library);
        // Every saved book (approval, edit, new cover) and every import reaches the automatic destinations.
        queue.BookImported += (_, bookId) => destinationService.OnBookChanged(bookId);
        _onBookSaved = (_, e) =>
        {
            if (e.LibraryId == library.Id)
            {
                destinationService.OnBookChanged(e.BookId);
            }
        };
        _editor.BookSaved += _onBookSaved;
        _onBookRemoved = (_, e) =>
        {
            if (e.LibraryId == library.Id)
            {
                _ = ForgetRemovedAsync(destinationService, e.BookId);
            }
        };
        _editor.BookRemoved += _onBookRemoved;
        await queue.StartAsync(ct);
        await identification.StartAsync(ct);
        destinationService.Start();
        var arrivals = new ArrivalTracker(queue, identification, _libraries.GetIndex(library), _watchOptions, _loggers.CreateLogger<ArrivalTracker>());
        arrivals.Completed += (_, summary) => ArrivalsCompleted?.Invoke(this, summary);
        var watcher = new InboxWatcher(queue, _watchOptions, _loggers.CreateLogger<InboxWatcher>());
        watcher.StatusChanged += (_, _) => WatcherChanged?.Invoke(this, EventArgs.Empty);
        Queue = queue;
        Identification = identification;
        Destinations = destinationService;
        _arrivals = arrivals;
        Watcher = watcher;
        Current = library;
        MissingFiles = new HashSet<string>();
        var inbox = _settings.Current.InboxPath;
        await Task.Run(() => watcher.Watch(inbox), CancellationToken.None);
        Changed?.Invoke(this, library);
        _ = CheckFilesAsync();
        return library;
    }

    /// <summary>
    /// Looks for books whose files disappeared outside the app. A book folder that is gone was
    /// deleted or moved by hand: the index is rebuilt from the metadata.json files, which drops
    /// the first and finds the second where it went.
    /// </summary>
    public async Task CheckFilesAsync(CancellationToken ct = default)
    {
        if (Current is not { } library)
        {
            return;
        }

        try
        {
            var index = _libraries.GetIndex(library);
            var result = await Task.Run(async () => BookFilesCheck.Check(library.RootPath, await index.GetBooksAsync(ct), ct), ct);
            if (result is null)
            {
                return;
            }

            // A folder may have moved (approval, edit) between reading the index and looking at the disk.
            var gone = new List<string>();
            foreach (var id in result.MissingFolder)
            {
                if (await index.GetBookAsync(id, ct) is { } fresh && !Directory.Exists(Path.Combine(library.RootPath, fresh.Folder)))
                {
                    gone.Add(id);
                }
            }

            if (gone.Count > 0)
            {
                _logger.LogInformation("{Count} book folders are gone; rebuilding the index", gone.Count);
                await _libraries.RebuildIndexAsync(library, ct);
                var books = await index.GetBooksAsync(ct);
                var ids = books.Select(b => b.Metadata.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var id in gone.Where(id => !ids.Contains(id)))
                {
                    await ForgetRemovedAsync(Destinations, id);
                }

                result = await Task.Run(() => BookFilesCheck.Check(library.RootPath, books, ct), ct) ?? result;
            }

            if (Current == library)
            {
                SetMissing(result.MissingOriginal);
            }
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            _logger.LogWarning(ex, "Could not check the files of library {LibraryId}", library.Id);
        }
    }

    /// <summary>Checks one book again (when it is shown); true when its main original is missing.</summary>
    public bool CheckFile(CatalogBook book)
    {
        var missing = Current is { } library && !BookFilesCheck.HasOriginal(library.RootPath, book);
        if (missing != MissingFiles.Contains(book.Metadata.Id))
        {
            var ids = MissingFiles.ToHashSet(StringComparer.Ordinal);
            _ = missing ? ids.Add(book.Metadata.Id) : ids.Remove(book.Metadata.Id);
            SetMissing(ids);
        }

        return missing;
    }

    private void SetMissing(IReadOnlySet<string> ids)
    {
        MissingFiles = ids;
        MissingFilesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A book left the library: it leaves the destinations' lists too, and their copies are deleted
    /// (never the one on a Kindle, decision of 2026-10-18).
    /// </summary>
    private async Task ForgetRemovedAsync(DestinationService? destinations, string bookId)
    {
        try
        {
            var settings = _settings.Current;
            if (settings.Destinations.Any(d => d.ChosenBookIds.Contains(bookId)))
            {
                await _settings.SaveAsync(settings with
                {
                    Destinations = settings.Destinations
                        .Select(d => d.ChosenBookIds.Contains(bookId) ? d with { ChosenBookIds = d.ChosenBookIds.Where(id => id != bookId).ToList() } : d)
                        .ToList(),
                });
            }
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            _logger.LogWarning(ex, "Could not take {BookId} out of the destinations' lists", bookId);
        }

        destinations?.OnBookChanged(bookId);
        if (MissingFiles.Contains(bookId))
        {
            SetMissing(MissingFiles.Where(id => id != bookId).ToHashSet(StringComparer.Ordinal));
        }

        BookRemoved?.Invoke(this, bookId);
    }

    private ArrivalTracker? _arrivals;

    private void OnSettingsSaved(object? sender, AppSettings saved)
    {
        if (Watcher is { } watcher && !PathsEqual(watcher.Folder, saved.InboxPath))
        {
            watcher.Watch(saved.InboxPath);
        }
    }

    private static bool PathsEqual(string? a, string? b) =>
        string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)
            ? string.IsNullOrWhiteSpace(a) == string.IsNullOrWhiteSpace(b)
            : string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Stops the background import; an item in progress resumes on the next start.</summary>
    public ValueTask DisposeAsync() => new(StopQueuesAsync());

    private EventHandler<BookSavedEventArgs>? _onBookSaved;
    private EventHandler<BookSavedEventArgs>? _onBookRemoved;

    private async Task StopQueuesAsync()
    {
        // The watcher feeds the queue: it stops first.
        Watcher?.Dispose();
        Watcher = null;
        _arrivals?.Dispose();
        _arrivals = null;

        if (_onBookSaved is not null)
        {
            _editor.BookSaved -= _onBookSaved;
            _onBookSaved = null;
        }

        if (_onBookRemoved is not null)
        {
            _editor.BookRemoved -= _onBookRemoved;
            _onBookRemoved = null;
        }

        if (Destinations is not null)
        {
            await Destinations.DisposeAsync().ConfigureAwait(false);
            Destinations = null;
        }

        if (Queue is not null)
        {
            await Queue.DisposeAsync().ConfigureAwait(false);
            Queue = null;
        }

        if (Identification is not null)
        {
            await Identification.DisposeAsync().ConfigureAwait(false);
            Identification = null;
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>User-facing message for a failure to create or open a library.</summary>
    public static string DescribeError(Exception ex) => ex switch
    {
        LibraryException { Kind: LibraryErrorKind.AlreadyALibrary } => Strings.Error_AlreadyALibrary,
        LibraryException { Kind: LibraryErrorKind.NotALibrary } => Strings.Error_NotALibrary,
        LibraryException { Kind: LibraryErrorKind.InvalidManifest } => Strings.Error_InvalidManifest,
        LibraryException { Kind: LibraryErrorKind.UnsupportedFormatVersion } => Strings.Error_UnsupportedFormat,
        _ => string.Format(Strings.Error_FileAccess, ex.Message),
    };

    /// <summary>Failures that mean "this folder can't be used", as opposed to bugs.</summary>
    public static bool IsExpectedFailure(Exception ex) =>
        ex is LibraryException or IOException or UnauthorizedAccessException or System.Data.Common.DbException;
}
