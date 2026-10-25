using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
using GibiOMatic.Destinations;
using GibiOMatic.Destinations.Sync;

namespace GibiOMatic.App.ViewModels;

/// <summary>A destination's line in the sidebar status card: "Komga de casa · 15 de 30".</summary>
public sealed partial class SidebarDestinationViewModel(string id, string name, Action<string> open) : ViewModelBase
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    [ObservableProperty]
    public partial string? StatusText { get; set; }

    [ObservableProperty]
    public partial StatusKind Kind { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial double ProgressValue { get; set; }

    [RelayCommand]
    private void Open() => open(Id);
}

/// <summary>
/// The destinations in the sidebar status card (DESIGN, Main): a short status per destination and,
/// while one is being updated, how far it got.
/// </summary>
public sealed partial class DestinationsStatusViewModel : ViewModelBase, IDisposable
{
    private readonly LibrarySession _session;
    private readonly IUiThread _ui;
    private readonly Action<string> _open;
    private readonly Debouncer _refresh;

    public DestinationsStatusViewModel(LibrarySession session, IUiThread ui, Action<string> open)
    {
        _session = session;
        _ui = ui;
        _open = open;
        _refresh = new Debouncer(ui, TimeSpan.FromMilliseconds(500), RefreshAsync);
        if (session.Destinations is { } service)
        {
            service.Changed += OnChanged;
            service.ProgressChanged += OnProgress;
        }
    }

    public ObservableCollection<SidebarDestinationViewModel> Items { get; } = [];

    public async Task RefreshAsync()
    {
        if (_session.Destinations is not { } service)
        {
            return;
        }

        // Every destination shows up right away; each status arrives on its own, so a network
        // drive that does not answer (up to a minute) holds only its own line.
        var byId = Items.ToDictionary(i => i.Id);
        Items.Clear();
        var loads = new List<Task>();
        foreach (var destination in service.Destinations)
        {
            if (!byId.TryGetValue(destination.Config.Id, out var item) || item.Name != destination.Config.Name)
            {
                item = new SidebarDestinationViewModel(destination.Config.Id, destination.Config.Name, _open)
                {
                    StatusText = Strings.Destination_Checking,
                    Kind = StatusKind.Neutral,
                };
            }

            Items.Add(item);
            loads.Add(LoadAsync(service, destination, item));
        }

        await Task.WhenAll(loads);
    }

    private static async Task LoadAsync(DestinationService service, IDestination destination, SidebarDestinationViewModel item)
    {
        var summary = await service.SummaryAsync(destination);
        Show(item, summary, service.LastResult(destination.Config.Id));
        if (service.CurrentOf(item.Id) is { } progress)
        {
            Show(item, progress);
        }
    }

    private static void Show(SidebarDestinationViewModel item, DestinationSummary s, SyncResult? last)
    {
        item.IsRunning = false;
        if (s.Config.Type == GibiOMatic.Destinations.Kindle.KindleDestinationType.TypeId)
        {
            (item.StatusText, item.Kind) = s.Status.Health switch
            {
                DestinationHealth.NotConfigured => (Strings.Sidebar_NoKcc, StatusKind.Warn),
                _ when last?.Failures.Count > 0 => (string.Format(CultureInfo.CurrentCulture, Strings.Sidebar_Errors, last.Failures.Count), StatusKind.Bad),
                _ when s.Pending > 0 => (string.Format(CultureInfo.CurrentCulture, Strings.Sidebar_ToPrepare, s.Pending), StatusKind.Warn),
                _ => (string.Format(CultureInfo.CurrentCulture, Strings.Sidebar_InList, s.Included), StatusKind.Ok),
            };
            return;
        }

        (item.StatusText, item.Kind) = s.Status.Health switch
        {
            DestinationHealth.NotConfigured => (Strings.Sidebar_NoFolder, StatusKind.Warn),
            DestinationHealth.Unavailable => (Strings.Sidebar_Unavailable, StatusKind.Neutral),
            _ when last?.Failures.Count > 0 => (string.Format(CultureInfo.CurrentCulture, Strings.Sidebar_Errors, last.Failures.Count), StatusKind.Bad),
            _ when !s.Confirmed => (Strings.Sidebar_FirstSync, StatusKind.Warn),
            _ when s.Pending > 0 => (string.Format(CultureInfo.CurrentCulture, Strings.Sidebar_Pending, s.Pending), StatusKind.Warn),
            _ => (Strings.Sidebar_UpToDate, StatusKind.Ok),
        };
    }

    private static void Show(SidebarDestinationViewModel item, SyncProgress progress)
    {
        item.IsRunning = true;
        item.Kind = StatusKind.Busy;
        item.ProgressValue = progress.Total == 0 ? 0 : Math.Min(1, (progress.Done + progress.FileFraction) / progress.Total);
        item.StatusText = progress.Done >= progress.Total
            ? Strings.Sidebar_Finishing
            : progress.Queued > 0
                ? string.Format(CultureInfo.CurrentCulture, Strings.Sidebar_ProgressQueued, progress.Done + 1, progress.Total, progress.Queued)
                : string.Format(CultureInfo.CurrentCulture, Strings.Sidebar_Progress, progress.Done + 1, progress.Total);
    }

    private void OnChanged(object? sender, EventArgs e) => _refresh.Trigger();

    private void OnProgress(object? sender, SyncProgress progress) => _ui.Post(() =>
    {
        if (progress.IsFinished)
        {
            _refresh.Trigger();
        }
        else if (Items.FirstOrDefault(i => i.Id == progress.DestinationId) is { } item)
        {
            Show(item, progress);
        }
    });

    public void Dispose()
    {
        if (_session.Destinations is { } service)
        {
            service.Changed -= OnChanged;
            service.ProgressChanged -= OnProgress;
        }

        _refresh.Dispose();
    }
}
