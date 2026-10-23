namespace GibiOMatic.App.Services.Tray;

/// <summary>An entry of the tray menu; <see cref="IsDefault"/> is shown in bold where the system supports it.</summary>
public sealed record TrayMenuItem(string Text, Action OnClick, bool IsDefault = false, bool IsEnabled = true);

/// <summary>
/// The app's icon in the system tray (SPEC §6, item 7): status in the tooltip, a menu and system
/// notifications. All members are called on the UI thread; events are raised on it too.
/// </summary>
public interface ITrayIcon : IDisposable
{
    /// <summary>False where the system has no tray; the app then closes with its window.</summary>
    bool IsSupported { get; }

    /// <summary>Shows the icon (once) or updates it. A null item in the menu is a separator.</summary>
    void Show(string tooltip, IReadOnlyList<TrayMenuItem?> menu);

    void Notify(string title, string text);

    /// <summary>Click on the icon.</summary>
    event EventHandler? Activated;

    /// <summary>Click on the last notification (not every system reports it).</summary>
    event EventHandler? NotificationClicked;
}
