using GibiOMatic.App.Resources;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Core.Settings;
using GibiOMatic.Library;
using GibiOMatic.Library.Identification;
using GibiOMatic.Library.Import;
using GibiOMatic.Library.Index;

namespace GibiOMatic.App.Services;

/// <summary>
/// The library currently open in the app, with its inbox queue. Opening or creating one
/// remembers it in the settings, so the next start opens it directly.
/// </summary>
public sealed class LibrarySession(
    LibraryService libraries,
    ISettingsService settings,
    ImportQueueFactory queues,
    IdentificationQueueFactory identifications) : IAsyncDisposable, IDisposable
{
    public LibraryInfo? Current { get; private set; }

    /// <summary>The inbox of the current library, running in the background.</summary>
    public ImportQueue? Queue { get; private set; }

    /// <summary>Online identification of the current library's books, running in the background.</summary>
    public IdentificationQueue? Identification { get; private set; }

    public LibraryIndex? Index => Current is null ? null : libraries.GetIndex(Current);

    /// <summary>Raised after a different library becomes the current one.</summary>
    public event EventHandler<LibraryInfo>? Changed;

    public Task<LibraryInfo> CreateAsync(string folder, CancellationToken ct = default) =>
        ActivateAsync(libraries.CreateAsync(folder, ct), ct);

    public Task<LibraryInfo> OpenAsync(string folder, CancellationToken ct = default) =>
        ActivateAsync(libraries.OpenAsync(folder, ct), ct);

    private async Task<LibraryInfo> ActivateAsync(Task<LibraryInfo> opening, CancellationToken ct)
    {
        var library = await opening;
        if (settings.Current.LibraryPath != library.RootPath)
        {
            await settings.SaveAsync(settings.Current with { LibraryPath = library.RootPath }, ct);
        }

        await StopQueuesAsync();

        var queue = queues.Create(library);
        var identification = identifications.Create(library);
        // Every imported book is identified afterwards (decision of 2026-10-17).
        queue.BookImported += (_, bookId) => _ = identification.EnqueueAsync([bookId], force: false);
        await queue.StartAsync(ct);
        await identification.StartAsync(ct);
        Queue = queue;
        Identification = identification;
        Current = library;
        Changed?.Invoke(this, library);
        return library;
    }

    /// <summary>Stops the background import; an item in progress resumes on the next start.</summary>
    public ValueTask DisposeAsync() => new(StopQueuesAsync());

    private async Task StopQueuesAsync()
    {
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
