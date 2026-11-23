using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;

namespace GibiOMatic.App.Services;

/// <summary>A short notice at the bottom of the window ("Saved."), which goes away by itself.</summary>
public sealed partial class Toast(string text, bool isError, Action<Toast> close)
{
    public string Text { get; } = text;

    public bool IsError { get; } = isError;

    [RelayCommand]
    private void Close() => close(this);
}

/// <summary>
/// The notices the shell shows over every page: a page can say what it did and then leave (the
/// Editor saves and goes back to the Library). Only the latest few stay; each closes after a while.
/// </summary>
public sealed class Toasts(IUiThread ui)
{
    public const int MaxShown = 3;

    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(4);

    public ObservableCollection<Toast> Items { get; } = [];

    public Toast Show(string text, bool isError = false)
    {
        var toast = new Toast(text, isError, Close);
        Items.Add(toast);
        while (Items.Count > MaxShown)
        {
            Items.RemoveAt(0);
        }

        _ = CloseLaterAsync(toast);
        return toast;
    }

    public void Close(Toast toast) => Items.Remove(toast);

    private async Task CloseLaterAsync(Toast toast)
    {
        await Task.Delay(Duration).ConfigureAwait(false);
        ui.Post(() => Close(toast));
    }
}
