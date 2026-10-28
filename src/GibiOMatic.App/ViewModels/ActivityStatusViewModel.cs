using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
using GibiOMatic.Library.Import;

namespace GibiOMatic.App.ViewModels;

/// <summary>
/// The background work in the sidebar status card, seen from every page: the inbox ("Inbox · 3
/// queued", with the progress of the file at hand) and the metadata search ("Metadata search:
/// 37 queued"). A click opens the page where it can be paused.
/// </summary>
public sealed partial class ActivityStatusViewModel : ViewModelBase, IDisposable
{
    private readonly LibrarySession _session;
    private readonly IUiThread _ui;
    private readonly Action<NavPage> _open;
    private readonly Debouncer _refresh;

    public ActivityStatusViewModel(LibrarySession session, IUiThread ui, Action<NavPage> open)
    {
        _session = session;
        _ui = ui;
        _open = open;
        _refresh = new Debouncer(ui, TimeSpan.FromMilliseconds(300), RefreshAsync);
        if (session.Queue is { } queue)
        {
            queue.Changed += OnChanged;
            queue.ProgressChanged += OnProgress;
        }

        if (session.Identification is { } identification)
        {
            identification.Changed += OnChanged;
        }

        session.WatcherChanged += OnChanged;
    }

    // ---- Inbox ----

    [ObservableProperty]
    public partial bool ShowInbox { get; private set; }

    [ObservableProperty]
    public partial string? InboxText { get; private set; }

    [ObservableProperty]
    public partial string? InboxDetail { get; private set; }

    [ObservableProperty]
    public partial StatusKind InboxKind { get; private set; }

    [ObservableProperty]
    public partial bool InboxRunning { get; private set; }

    [ObservableProperty]
    public partial double InboxProgress { get; private set; }

    // ---- Metadata ----

    [ObservableProperty]
    public partial bool ShowMetadata { get; private set; }

    [ObservableProperty]
    public partial string? MetadataText { get; private set; }

    [ObservableProperty]
    public partial string? MetadataDetail { get; private set; }

    [ObservableProperty]
    public partial StatusKind MetadataKind { get; private set; }

    [ObservableProperty]
    public partial bool MetadataRunning { get; private set; }

    public async Task RefreshAsync()
    {
        if (_session.Queue is { } queue)
        {
            var summary = await queue.GetSummaryAsync();
            var working = summary.Waiting + summary.Queued;
            var watcher = _session.Watcher;
            // Shown while there is work, and always while a folder is watched or missing.
            ShowInbox = working > 0 || watcher?.Status is InboxWatchStatus.Watching or InboxWatchStatus.Missing;
            InboxRunning = working > 0 && !queue.IsPaused;
            (InboxText, InboxKind) = (working, queue.IsPaused, watcher?.Status) switch
            {
                ( > 0, true, _) => (string.Format(CultureInfo.CurrentCulture, Strings.Status_Paused, working), StatusKind.Warn),
                ( > 0, _, _) => (string.Format(CultureInfo.CurrentCulture, Strings.Status_InboxQueued, working), StatusKind.Busy),
                (_, _, InboxWatchStatus.Missing) => (Strings.Status_InboxMissing, StatusKind.Warn),
                _ => (Strings.Status_InboxWatching, StatusKind.Ok),
            };
            if (!InboxRunning || queue.Current is null)
            {
                InboxProgress = 0;
                InboxDetail = watcher?.Folder;
            }
        }

        if (_session.Identification is { } identification)
        {
            var queued = (await identification.GetCountsAsync()).Queued;
            ShowMetadata = queued > 0;
            MetadataRunning = queued > 0 && !identification.IsPaused;
            (MetadataText, MetadataKind) = identification.IsPaused
                ? (string.Format(CultureInfo.CurrentCulture, Strings.Status_Paused, queued), StatusKind.Warn)
                : (string.Format(CultureInfo.CurrentCulture, Strings.Status_MetadataSearching, queued), StatusKind.Busy);
            MetadataDetail = identification.IsPaused
                ? string.Format(CultureInfo.CurrentCulture, Strings.Identify_Paused, queued)
                : identification.CurrentTitle is { } title
                    ? string.Format(CultureInfo.CurrentCulture, Strings.Identify_Running, title, queued)
                    : string.Format(CultureInfo.CurrentCulture, Strings.Identify_Waiting, queued);
        }
    }

    [RelayCommand]
    private void OpenInbox() => _open(NavPage.Inbox);

    [RelayCommand]
    private void OpenReview() => _open(NavPage.Review);

    private void OnChanged(object? sender, EventArgs e) => _refresh.Trigger();

    private void OnProgress(object? sender, ImportProgress? progress) => _ui.Post(() =>
    {
        if (progress is null)
        {
            return;
        }

        InboxProgress = progress.Fraction;
        var phase = progress.Phase == InboxItemState.Analyzing ? Strings.Inbox_ProgressAnalyzing : Strings.Inbox_ProgressImporting;
        InboxDetail = string.Format(CultureInfo.CurrentCulture, phase, progress.FileName, progress.Fraction);
    });

    public void Dispose()
    {
        if (_session.Queue is { } queue)
        {
            queue.Changed -= OnChanged;
            queue.ProgressChanged -= OnProgress;
        }

        if (_session.Identification is { } identification)
        {
            identification.Changed -= OnChanged;
        }

        _session.WatcherChanged -= OnChanged;
        _refresh.Dispose();
    }
}
