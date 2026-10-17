using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace GibiOMatic.App.Services;

/// <summary>Opens a web page in the user's browser.</summary>
public interface IUrlOpener
{
    Task OpenAsync(Uri url);
}

public sealed class AvaloniaUrlOpener : IUrlOpener
{
    public async Task OpenAsync(Uri url)
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window is not null && TopLevel.GetTopLevel(window) is { } top)
        {
            await top.Launcher.LaunchUriAsync(url);
        }
    }
}
