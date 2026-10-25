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
        var inbox = _settings.Current.InboxPath;
        await Task.Run(() => watcher.Watch(inbox), CancellationToken.None);
        Changed?.Invoke(this, library);
        return library;
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
