using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.App.Services.Tray;

/// <summary>
/// The tray icon on Linux, with Avalonia's tray icon (StatusNotifierItem). Notifications go through
/// <c>notify-send</c> when it is installed; their clicks are not reported.
/// </summary>
public sealed class AvaloniaTrayIcon(ILogger<AvaloniaTrayIcon> logger) : ITrayIcon
{
    private TrayIcon? _icon;
    private bool _notifyMissing;

    public bool IsSupported => Application.Current is not null;

    public event EventHandler? Activated;

    public event EventHandler? NotificationClicked
    {
        add { }
        remove { }
    }

    public void Show(string tooltip, IReadOnlyList<TrayMenuItem?> menu)
    {
        if (_icon is null)
        {
            _icon = new TrayIcon { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://GibiOMatic/Assets/gibiomatic.ico"))) };
            _icon.Clicked += (_, _) => Activated?.Invoke(this, EventArgs.Empty);
            TrayIcon.SetIcons(Application.Current!, [_icon]);
        }

        _icon.ToolTipText = tooltip;
        var nativeMenu = new NativeMenu();
        foreach (var item in menu)
        {
            if (item is null)
            {
                nativeMenu.Items.Add(new NativeMenuItemSeparator());
                continue;
            }

            var entry = new NativeMenuItem(item.Text) { IsEnabled = item.IsEnabled };
            entry.Click += (_, _) => item.OnClick();
            nativeMenu.Items.Add(entry);
        }

        _icon.Menu = nativeMenu;
        _icon.IsVisible = true;
    }

    public void Notify(string title, string text)
    {
        if (_notifyMissing)
        {
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("notify-send")
            {
                ArgumentList = { "--app-name=Gibi-o-matic", title, text },
                UseShellExecute = false,
            });
        }
        catch (Win32Exception ex)
        {
            _notifyMissing = true;
            logger.LogInformation(ex, "notify-send is not available; notifications are off");
        }
    }

    public void Dispose()
    {
        if (_icon is not null)
        {
            _icon.IsVisible = false;
            _icon.Dispose();
            _icon = null;
        }
    }
}
