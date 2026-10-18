using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using GibiOMatic.App.Resources;

namespace GibiOMatic.App.Services;

public interface IFilePicker
{
    /// <summary>Asks the user for book files; returns an empty list if cancelled.</summary>
    Task<IReadOnlyList<string>> PickBooksAsync(string title, CancellationToken ct = default);

    /// <summary>Asks the user for one image (a new cover); <c>null</c> if cancelled.</summary>
    Task<string?> PickImageAsync(string title, CancellationToken ct = default);

    /// <summary>Asks the user for a program (the KCC); <c>null</c> if cancelled.</summary>
    Task<string?> PickExecutableAsync(string title, CancellationToken ct = default);
}

public sealed class AvaloniaFilePicker : IFilePicker
{
    public async Task<IReadOnlyList<string>> PickBooksAsync(string title, CancellationToken ct = default)
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow
                     ?? throw new InvalidOperationException("No main window to show the file picker on.");

        var files = await TopLevel.GetTopLevel(window)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.Picker_BookFiles) { Patterns = ["*.pdf", "*.cbz", "*.cbr", "*.epub"] },
                new FilePickerFileType(Strings.Picker_AllFiles) { Patterns = ["*"] },
            ],
        });
        ct.ThrowIfCancellationRequested();

        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickImageAsync(string title, CancellationToken ct = default)
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow
                     ?? throw new InvalidOperationException("No main window to show the file picker on.");

        var files = await TopLevel.GetTopLevel(window)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });
        ct.ThrowIfCancellationRequested();

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> PickExecutableAsync(string title, CancellationToken ct = default)
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow
                     ?? throw new InvalidOperationException("No main window to show the file picker on.");

        var files = await TopLevel.GetTopLevel(window)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = OperatingSystem.IsWindows()
                ? [new FilePickerFileType(Strings.Picker_Programs) { Patterns = ["*.exe"] }, new FilePickerFileType(Strings.Picker_AllFiles) { Patterns = ["*"] }]
                : [new FilePickerFileType(Strings.Picker_AllFiles) { Patterns = ["*"] }],
        });
        ct.ThrowIfCancellationRequested();

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}
