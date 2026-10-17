using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace GibiOMatic.App.Services;

public interface IFolderPicker
{
    /// <summary>Asks the user for a folder; returns <c>null</c> if cancelled.</summary>
    Task<string?> PickFolderAsync(string title, CancellationToken ct = default);
}

public sealed class AvaloniaFolderPicker : IFolderPicker
{
    public async Task<string?> PickFolderAsync(string title, CancellationToken ct = default)
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow
                     ?? throw new InvalidOperationException("No main window to show the folder picker on.");

        var folders = await TopLevel.GetTopLevel(window)!.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        ct.ThrowIfCancellationRequested();

        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }
}
