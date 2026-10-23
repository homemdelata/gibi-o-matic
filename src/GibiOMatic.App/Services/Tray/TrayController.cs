using System.Globalization;
using GibiOMatic.App.Resources;
using GibiOMatic.App.ViewModels;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Library.Import;

namespace GibiOMatic.App.Services.Tray;

/// <summary>What the tray needs from the app: its window and quitting.</summary>
public interface IAppHost
{
    /// <summary>Shows the main window (creating it the first time), optionally on a page.</summary>
    void ShowMainWindow(NavPage? page = null);

    void Exit();
}

/// <summary>
/// Keeps the tray icon up to date with the inbox (tooltip and menu) and shows one notification per
/// batch of files from the watched folder (decision of 2026-10-23).
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly ITrayIcon _tray;
    private readonly LibrarySession _session;
    private readonly IUiThread _ui;
    private readonly Debouncer _refresh;
    private IAppHost? _host;
    private ImportQueue? _queue;
    private NavPage _notificationPage = NavPage.Library;

    public TrayController(ITrayIcon tray, LibrarySession session, IUiThread ui)
    {
        _tray = tray;
        _session = session;
        _ui = ui;
        _refresh = new Debouncer(ui, TimeSpan.FromMilliseconds(500), RefreshAsync);
    }

    public bool IsSupported => _tray.IsSupported;

    /// <summary>The tooltip as last shown (for tests).</summary>
    public string Tooltip { get; private set; } = string.Empty;

    /// <summary>The menu as last shown (for tests).</summary>
    public IReadOnlyList<TrayMenuItem?> Menu { get; private set; } = [];

    public void Start(IAppHost host)
    {
        if (!_tray.IsSupported)
        {
            return;
        }

        _host = host;
        _tray.Activated += OnActivated;
        _tray.NotificationClicked += OnNotificationClicked;
        _session.Changed += OnLibraryChanged;
        _session.WatcherChanged += OnStateChanged;
        _session.ArrivalsCompleted += OnArrivalsCompleted;
        Subscribe(_session.Queue);
        _ = RefreshAsync();
    }

    private void OnActivated(object? sender, EventArgs e) => _host?.ShowMainWindow();

    private void OnNotificationClicked(object? sender, EventArgs e) => _host?.ShowMainWindow(_notificationPage);

    private void OnLibraryChanged(object? sender, LibraryInfo e)
    {
        Subscribe(_session.Queue);
        _refresh.Trigger();
    }

    private void OnStateChanged(object? sender, EventArgs e) => _refresh.Trigger();

    private void Subscribe(ImportQueue? queue)
    {
        if (_queue is not null)
        {
            _queue.Changed -= OnStateChanged;
        }

        _queue = queue;
        if (queue is not null)
        {
            queue.Changed += OnStateChanged;
        }
    }

    private void OnArrivalsCompleted(object? sender, ArrivalSummary summary) => _ui.Post(() =>
    {
        var (title, text, page) = ArrivalText.Describe(summary);
        _notificationPage = page;
        _tray.Notify(title, text);
    });

    /// <summary>Rebuilds the tooltip and the menu (on the UI thread).</summary>
    public async Task RefreshAsync()
    {
        var queue = _session.Queue;
        var watcher = _session.Watcher;
        var working = 0;
        if (queue is not null)
        {
            var summary = await queue.GetSummaryAsync();
            working = summary.Waiting + summary.Queued;
        }

        var status = (queue, watcher) switch
        {
            (null, _) or (_, null) => null,
            ({ IsPaused: true }, _) => Strings.Tray_InboxPaused,
            _ when working > 0 => string.Format(CultureInfo.CurrentCulture, Strings.Tray_InboxWorking, working),
            (_, { Status: InboxWatchStatus.Watching }) => string.Format(CultureInfo.CurrentCulture, Strings.Tray_InboxWatching, watcher.Folder),
            (_, { Status: InboxWatchStatus.Missing }) => string.Format(CultureInfo.CurrentCulture, Strings.Tray_InboxMissing, watcher.Folder),
            _ => Strings.Tray_InboxOff,
        };
        Tooltip = status is null ? "Gibi-o-matic" : $"Gibi-o-matic\n{status}";
        Menu =
        [
            new TrayMenuItem(Strings.Tray_Open, () => _host?.ShowMainWindow(), IsDefault: true),
            new TrayMenuItem(queue?.IsPaused == true ? Strings.Tray_ResumeInbox : Strings.Tray_PauseInbox, () => _ = TogglePauseAsync(),
                IsEnabled: queue is not null),
            null,
            new TrayMenuItem(Strings.Tray_Exit, () => _host?.Exit()),
        ];
        _tray.Show(Tooltip, Menu);
    }

    private async Task TogglePauseAsync()
    {
        if (_session.Queue is not { } queue)
        {
            return;
        }

        if (queue.IsPaused)
        {
            await queue.ResumeAsync();
        }
        else
        {
            await queue.PauseAsync();
        }
    }

    public void Dispose()
    {
        _refresh.Dispose();
        Subscribe(null);
        _session.Changed -= OnLibraryChanged;
        _session.WatcherChanged -= OnStateChanged;
        _session.ArrivalsCompleted -= OnArrivalsCompleted;
        _tray.Activated -= OnActivated;
        _tray.NotificationClicked -= OnNotificationClicked;
        _tray.Dispose();
    }
}

/// <summary>The notification of a batch from the watched folder.</summary>
public static class ArrivalText
{
    /// <returns>Title, text and the page that a click on it opens.</returns>
    public static (string Title, string Text, NavPage Page) Describe(ArrivalSummary summary)
    {
        var title = string.Format(CultureInfo.CurrentCulture, summary.Files == 1 ? Strings.Notify_TitleOne : Strings.Notify_Title, summary.Files);
        var parts = new List<string>();
        void Add(int count, string one, string many)
        {
            if (count > 0)
            {
                parts.Add(string.Format(CultureInfo.CurrentCulture, count == 1 ? one : many, count));
            }
        }

        Add(summary.Approved, Strings.Notify_ApprovedOne, Strings.Notify_Approved);
        Add(summary.ToReview, Strings.Notify_ToReviewOne, Strings.Notify_ToReview);
        Add(summary.AlreadyInLibrary, Strings.Notify_AlreadyOne, Strings.Notify_Already);
        Add(summary.Failed, Strings.Notify_FailedOne, Strings.Notify_Failed);
        var page = summary.ToReview > 0 ? NavPage.Review : summary.Failed > 0 ? NavPage.Inbox : NavPage.Library;
        return (title, string.Join(" · ", parts), page);
    }
}
