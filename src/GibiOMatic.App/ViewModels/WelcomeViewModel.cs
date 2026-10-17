using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
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

    [RelayCommand]
    private Task CreateLibraryAsync(CancellationToken ct) =>
        PickAndRunAsync(Strings.Picker_CreateTitle, session.CreateAsync, ct);

    [RelayCommand]
    private Task OpenLibraryAsync(CancellationToken ct) =>
        PickAndRunAsync(Strings.Picker_OpenTitle, session.OpenAsync, ct);

    private async Task PickAndRunAsync(string pickerTitle, Func<string, CancellationToken, Task> action, CancellationToken ct)
    {
        var folder = await folderPicker.PickFolderAsync(pickerTitle, ct);
        if (folder is null)
        {
            return;
        }

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
