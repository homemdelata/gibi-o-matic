using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
using GibiOMatic.Library.Editing;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.App.ViewModels;

/// <summary>
/// "Remove from library" asks first, then sends the book folder to the library's internal trash
/// or deletes it for good (decision of 2026-10-27). Shared by the Library, the Review and the Editor.
/// </summary>
public sealed partial class RemoveBookPromptViewModel(LibrarySession session, BookEditor editor, ILogger logger) : ViewModelBase
{
    private string? _bookId;

    /// <summary>Called with the book's id once it is out of the library.</summary>
    public Action<string>? Removed { get; set; }

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    /// <summary>"Remove “Saga, Vol. 1” from the library?"</summary>
    [ObservableProperty]
    public partial string? Question { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(DeleteCommand))]
    public partial bool IsBusy { get; private set; }

    private bool IsIdle => !IsBusy;

    public void Ask(string bookId, string title)
    {
        _bookId = bookId;
        Question = string.Format(CultureInfo.CurrentCulture, Strings.Remove_Question, title);
        Error = null;
        IsOpen = true;
    }

    /// <summary>The folder goes to <c>.gibiomatic/trash</c>.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task RemoveAsync(CancellationToken ct) => RunAsync(BookRemoval.ToTrash, ct);

    /// <summary>The folder and its history are deleted.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task DeleteAsync(CancellationToken ct) => RunAsync(BookRemoval.DeleteFiles, ct);

    [RelayCommand]
    private void Cancel()
    {
        IsOpen = false;
        _bookId = null;
    }

    private async Task RunAsync(BookRemoval removal, CancellationToken ct)
    {
        if (_bookId is not { } id || session.Current is not { } library)
        {
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            // The library may be on Google Drive: the disk work stays off the UI thread.
            await Task.Run(() => editor.RemoveAsync(library, id, removal, ct), ct);
            IsOpen = false;
            _bookId = null;
            Removed?.Invoke(id);
        }
        catch (Exception ex) when (LibrarySession.IsExpectedFailure(ex) || ex is InvalidOperationException)
        {
            logger.LogError(ex, "Could not remove book {BookId}", id);
            Error = string.Format(CultureInfo.CurrentCulture, Strings.Remove_Error, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
