using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
using GibiOMatic.Library;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.App.ViewModels;

/// <summary>First run: create a new library or open an existing one.</summary>
public sealed partial class WelcomeViewModel(
    IFolderPicker folderPicker,
    LibrarySession session,
    ILogger<WelcomeViewModel> logger) : ViewModelBase
{
    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    /// <summary>A folder with files in it, waiting for the user to confirm creating the library there.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingCreate))]
    public partial string? PendingCreateFolder { get; private set; }

    public bool IsConfirmingCreate => PendingCreateFolder is not null;

    [RelayCommand]
    private async Task CreateLibraryAsync(CancellationToken ct)
    {
        var folder = await folderPicker.PickFolderAsync(Strings.Picker_CreateTitle, ct);
        if (folder is null)
        {
            return;
        }

        // An existing library is reported by CreateAsync; other files only deserve a warning.
        if (!File.Exists(LibraryLayout.ManifestPath(folder)) && LibraryService.HasVisibleContent(folder))
        {
            ErrorMessage = null;
            PendingCreateFolder = folder;
            return;
        }

        await RunAsync(folder, session.CreateAsync, ct);
    }

    [RelayCommand]
    private async Task ConfirmCreateAsync(CancellationToken ct)
    {
        if (PendingCreateFolder is { } folder)
        {
            PendingCreateFolder = null;
            await RunAsync(folder, session.CreateAsync, ct);
        }
    }

    [RelayCommand]
    private void CancelCreate() => PendingCreateFolder = null;

    [RelayCommand]
    private async Task OpenLibraryAsync(CancellationToken ct)
    {
        var folder = await folderPicker.PickFolderAsync(Strings.Picker_OpenTitle, ct);
        if (folder is not null)
        {
            PendingCreateFolder = null;
            await RunAsync(folder, session.OpenAsync, ct);
        }
    }

    private async Task RunAsync(string folder, Func<string, CancellationToken, Task> action, CancellationToken ct)
    {
        ErrorMessage = null;
        try
        {
            await action(folder, ct);
        }
        catch (Exception ex) when (LibrarySession.IsExpectedFailure(ex))
        {
            logger.LogWarning(ex, "Could not use {Folder} as a library", folder);
            ErrorMessage = LibrarySession.DescribeError(ex);
        }
    }
}
