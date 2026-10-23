using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Platform;

namespace GibiOMatic.App.Services.Tray;

/// <summary>
/// The tray icon on Windows, straight on Shell_NotifyIcon: unlike Avalonia's tray icon it can show
/// a notification (a balloon, which Windows 10 and 11 show as a toast and keep in the notification
/// center) and tell when it is clicked. Its hidden window gets messages from the UI thread's loop.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class Win32TrayIcon : ITrayIcon
{
    private const uint IconId = 1;
    private const uint CallbackMessage = 0x8000 + 1; // WM_APP + 1
    private const string WindowClass = "GibiOMatic.Tray";

    private static Win32TrayIcon? _active;

    private readonly uint _taskbarCreated;
    private nint _window;
    private nint _smallIcon;
    private nint _largeIcon;
    private bool _added;
    private string _tooltip = string.Empty;
    private IReadOnlyList<TrayMenuItem?> _menu = [];

    public Win32TrayIcon()
    {
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    }

    public bool IsSupported => true;

    public event EventHandler? Activated;

    public event EventHandler? NotificationClicked;

    public void Show(string tooltip, IReadOnlyList<TrayMenuItem?> menu)
    {
        _tooltip = tooltip;
        _menu = menu;
        if (_window == 0)
        {
            CreateWindow();
        }

        var data = NewData(NifTip | NifShowTip);
        SetText(data.szTip, 128, _tooltip);
        Shell_NotifyIconW(NimModify, &data);
        if (!_added)
        {
            Add();
        }
    }

    public void Notify(string title, string text)
    {
        if (!_added)
        {
            return;
        }

        var data = NewData(NifInfo);
        SetText(data.szInfoTitle, 64, title);
        SetText(data.szInfo, 256, text);
        data.dwInfoFlags = NiifUser | NiifLargeIcon;
        data.hBalloonIcon = _largeIcon;
        Shell_NotifyIconW(NimModify, &data);
    }

    private void CreateWindow()
    {
        var module = GetModuleHandleW(null);
        fixed (char* className = WindowClass)
        {
            var windowClass = new WndClassEx
            {
                cbSize = (uint)sizeof(WndClassEx),
                lpfnWndProc = &WndProc,
                hInstance = module,
                lpszClassName = className,
            };
            RegisterClassExW(&windowClass);
            // A top-level window that is never shown (a message-only one would miss "TaskbarCreated").
            _window = CreateWindowExW(0, className, className, WsPopup, 0, 0, 0, 0, 0, 0, module, 0);
        }

        _active = this;
        (_smallIcon, _largeIcon) = (LoadAppIcon(GetSystemMetrics(SmCxSmIcon)), LoadAppIcon(GetSystemMetrics(SmCxIcon)));
    }

    private void Add()
    {
        var data = NewData(NifMessage | NifIcon | NifTip | NifShowTip);
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _smallIcon;
        SetText(data.szTip, 128, _tooltip);
        _added = Shell_NotifyIconW(NimAdd, &data);
        if (!_added)
        {
            // Explorer may not be up yet at logon; "TaskbarCreated" brings it back.
            Serilog.Log.Warning("Could not add the tray icon");
        }
        else
        {
            // Version 4: the menu key, keyboard selection and the click on a notification are reported.
            data.uVersion = NotifyIconVersion4;
            Shell_NotifyIconW(NimSetVersion, &data);
        }
    }

    private NotifyIconData NewData(uint flags) => new()
    {
        cbSize = (uint)sizeof(NotifyIconData),
        hWnd = _window,
        uID = IconId,
        uFlags = flags,
    };

    [UnmanagedCallersOnly]
    private static nint WndProc(nint window, uint message, nint wParam, nint lParam)
    {
        if (_active is { } tray && window == tray._window)
        {
            try
            {
                if (message == CallbackMessage)
                {
                    tray.OnTrayMessage((uint)(lParam & 0xFFFF), (short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
                    return 0;
                }

                if (message == tray._taskbarCreated)
                {
                    // Explorer restarted: the icon has to be added again.
                    tray._added = false;
                    tray.Add();
                    return 0;
                }
            }
            catch (Exception ex)
            {
                // An exception must never cross into native code.
                Serilog.Log.Error(ex, "Tray icon message failed");
            }
        }

        return DefWindowProcW(window, message, wParam, lParam);
    }

    private void OnTrayMessage(uint trayEvent, int x, int y)
    {
        switch (trayEvent)
        {
            case NinSelect or NinKeySelect:
                Activated?.Invoke(this, EventArgs.Empty);
                break;
            case WmContextMenu:
                ShowMenu(x, y);
                break;
            case NinBalloonUserClick:
                NotificationClicked?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void ShowMenu(int x, int y)
    {
        var menu = CreatePopupMenu();
        try
        {
            for (var i = 0; i < _menu.Count; i++)
            {
                if (_menu[i] is not { } item)
                {
                    AppendMenuW(menu, MfSeparator, 0, null);
                    continue;
                }

                fixed (char* text = item.Text)
                {
                    AppendMenuW(menu, MfString | (item.IsEnabled ? 0 : MfGrayed), (nuint)(i + 1), text);
                }

                if (item.IsDefault)
                {
                    SetMenuDefaultItem(menu, (uint)(i + 1), 0);
                }
            }

            // Without this the menu does not close when the user clicks elsewhere.
            SetForegroundWindow(_window);
            var chosen = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton | TpmNoNotify, x, y, _window, 0);
            PostMessageW(_window, 0, 0, 0);
            if (chosen > 0 && chosen <= _menu.Count && _menu[chosen - 1] is { IsEnabled: true } picked)
            {
                picked.OnClick();
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary>The image of the app's .ico closest to <paramref name="size"/> (PNG inside the ICO is fine).</summary>
    private static nint LoadAppIcon(int size)
    {
        using var stream = AssetLoader.Open(new Uri("avares://GibiOMatic/Assets/gibiomatic.ico"));
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var ico = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);

        // ICONDIR (6 bytes) then one 16-byte ICONDIRENTRY per image: width, height (0 = 256)…, size, offset.
        var count = BitConverter.ToUInt16(ico[4..]);
        var (bestOffset, bestLength, bestWidth) = (0, 0, 0);
        for (var i = 0; i < count; i++)
        {
            var entry = ico.Slice(6 + 16 * i, 16);
            var width = entry[0] == 0 ? 256 : entry[0];
            var better = bestWidth == 0
                         || (width >= size && (bestWidth < size || width < bestWidth))
                         || (width < size && bestWidth < size && width > bestWidth);
            if (better)
            {
                (bestOffset, bestLength, bestWidth) = (BitConverter.ToInt32(entry[12..]), BitConverter.ToInt32(entry[8..]), width);
            }
        }

        fixed (byte* image = ico.Slice(bestOffset, bestLength))
        {
            return CreateIconFromResourceEx(image, (uint)bestLength, 1, 0x00030000, size, size, 0);
        }
    }

    private static void SetText(char* field, int capacity, string text)
    {
        var length = Math.Min(text.Length, capacity - 1);
        text.AsSpan(0, length).CopyTo(new Span<char>(field, capacity));
        field[length] = '\0';
    }

    /// <summary>Lets the instance already running bring its window to the front (called by a second start).</summary>
    public static void AllowOtherProcessToTakeFocus() => AllowSetForegroundWindow(-1);

    public void Dispose()
    {
        if (_added)
        {
            var data = NewData(0);
            Shell_NotifyIconW(NimDelete, &data);
            _added = false;
        }

        if (_window != 0)
        {
            DestroyWindow(_window);
            _window = 0;
            fixed (char* className = WindowClass)
            {
                UnregisterClassW(className, GetModuleHandleW(null));
            }
        }

        foreach (var icon in new[] { _smallIcon, _largeIcon })
        {
            if (icon != 0)
            {
                DestroyIcon(icon);
            }
        }

        (_smallIcon, _largeIcon) = (0, 0);
        if (_active == this)
        {
            _active = null;
        }
    }

    // ---- Win32 ----

    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10, NifShowTip = 0x80;
    private const uint NiifUser = 0x4, NiifLargeIcon = 0x20;
    private const uint NotifyIconVersion4 = 4;
    private const uint NinSelect = 0x400, NinKeySelect = 0x401, NinBalloonUserClick = 0x405, WmContextMenu = 0x7B;
    private const uint MfString = 0x0, MfGrayed = 0x1, MfSeparator = 0x800;
    private const uint TpmRightButton = 0x2, TpmNoNotify = 0x80, TpmReturnCmd = 0x100;
    private const uint WsPopup = 0x80000000;
    private const int SmCxIcon = 11, SmCxSmIcon = 49;

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public nint hIconSm;
    }

    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Shell_NotifyIconW(uint message, NotifyIconData* data);

    [LibraryImport("user32.dll")]
    private static partial ushort RegisterClassExW(WndClassEx* windowClass);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterClassW(char* className, nint instance);

    [LibraryImport("user32.dll")]
    private static partial nint CreateWindowExW(uint exStyle, char* className, char* windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetModuleHandleW(char* name);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    private static partial nint CreateIconFromResourceEx(byte* data, uint size, int isIcon, uint version, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(nint menu, uint flags, nuint id, char* text);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetMenuDefaultItem(nint menu, uint item, uint byPosition);

    [LibraryImport("user32.dll")]
    private static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
