using GibiOMatic.App.Resources;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Core.Settings;

namespace GibiOMatic.App.Services;

/// <summary>
/// The library currently open in the app. Opening or creating one remembers it in the settings,
/// so the next start opens it directly.
/// </summary>
public sealed class LibrarySession(ILibraryService libraries, ISettingsService settings)
{
    public LibraryInfo? Current { get; private set; }

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

        Current = library;
        Changed?.Invoke(this, library);
        return library;
    }

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
