using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
using GibiOMatic.Destinations.Kindle;

namespace GibiOMatic.App.ViewModels;

public enum NavPage
{
    Library,
    Inbox,
    Review,
    Editor,
    Destinations,
    Settings,
}

public sealed partial class NavItemViewModel(NavPage page, string label, PageViewModel content) : ViewModelBase
{
    public NavPage Page { get; } = page;
    public string Label { get; } = label;
    public PageViewModel Content { get; } = content;

    /// <summary>Optional counter shown at the right of the item.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlainCount), nameof(HasBadge))]
    public partial string? Count { get; set; }

    /// <summary>Show the counter as a pill (things waiting for the user).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlainCount), nameof(HasBadge))]
    public partial bool IsBadge { get; set; }

    /// <summary>Orange pill (Revisão) instead of the yellow one (Entrada).</summary>
    public bool IsWarnBadge { get; init; }

    public bool HasPlainCount => !string.IsNullOrEmpty(Count) && !IsBadge;

    public bool HasBadge => !string.IsNullOrEmpty(Count) && IsBadge;
}

/// <summary>Main layout once a library is open: sidebar navigation plus the current page.</summary>
public sealed partial class ShellViewModel : ViewModelBase, IDisposable
{
    private readonly LibrarySession _session;
    private readonly Debouncer _counts;
    private readonly KindleWatcher _kindle;
    private readonly DestinationsPageViewModel _destinationsPage;
    private bool _kindleDismissed;
    private string? _kindleToOpen;

    public ShellViewModel(LibrarySession session, LibraryPageViewModel libraryPage, InboxPageViewModel inboxPage,
        ReviewPageViewModel reviewPage, EditorPageViewModel editorPage, DestinationsPageViewModel destinationsPage, SettingsPageViewModel settingsPage, IUiThread ui,
        KindleWatcher kindle)
    {
        _session = session;
        _kindle = kindle;
        _destinationsPage = destinationsPage;
        kindle.Changed += OnKindleChanged;
        destinationsPage.KindleSent += OnKindleChanged;
        LibraryPath = session.Current?.RootPath ?? string.Empty;

        NavItems =
        [
            new NavItemViewModel(NavPage.Library, Strings.Nav_Library, libraryPage),
            new NavItemViewModel(NavPage.Inbox, Strings.Nav_Inbox, inboxPage) { IsBadge = true },
            new NavItemViewModel(NavPage.Review, Strings.Nav_Review, reviewPage) { IsBadge = true, IsWarnBadge = true },
            new NavItemViewModel(NavPage.Editor, Strings.Nav_Editor, editorPage),
            new NavItemViewModel(NavPage.Destinations, Strings.Nav_Destinations, destinationsPage),
            new NavItemViewModel(NavPage.Settings, Strings.Nav_Settings, settingsPage),
        ];

        libraryPage.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LibraryPageViewModel.BookCount))
            {
                NavItems[0].Count = libraryPage.BookCount?.ToString(CultureInfo.CurrentCulture);
            }
        };

        foreach (var item in NavItems)
        {
            item.Content.NavigationRequested += (_, request) => Navigate(request);
        }

        _counts = new Debouncer(ui, TimeSpan.FromMilliseconds(300), UpdateCountsAsync);
        Activity = new ActivityStatusViewModel(session, ui, NavigateTo);
        DestinationsStatus = new DestinationsStatusViewModel(session, ui, id =>
        {
            _ = destinationsPage.OpenAsync(id, showPlan: false);
            NavigateTo(NavPage.Destinations);
        });
        if (session.Queue is { } queue)
        {
            queue.Changed += OnQueueChanged;
        }

        if (session.Identification is { } identification)
        {
            identification.Changed += OnQueueChanged;
        }

        if (session.Destinations is { } destinations)
        {
            destinations.Changed += OnQueueChanged;
        }
    }

    public IReadOnlyList<NavItemViewModel> NavItems { get; }

    /// <summary>Inbox and metadata search in the sidebar status card.</summary>
    public ActivityStatusViewModel Activity { get; }

    /// <summary>The destinations' lines in the sidebar status card.</summary>
    public DestinationsStatusViewModel DestinationsStatus { get; }

    public string LibraryPath { get; }

    [ObservableProperty]
    public partial NavItemViewModel? SelectedNavItem { get; set; }

    [ObservableProperty]
    public partial PageViewModel? CurrentPage { get; private set; }

    /// <summary>The page load started by the last navigation (pages handle their own errors).</summary>
    public Task CurrentNavigation { get; private set; } = Task.CompletedTask;

    public async Task InitializeAsync()
    {
        NavigateTo(NavPage.Library);
        await UpdateCountsAsync();
        await Activity.RefreshAsync();
        await DestinationsStatus.RefreshAsync();
        _kindle.Start();
        await UpdateKindleBannerAsync();
        await CurrentNavigation;
    }

    // ---- Kindle connected ----

    /// <summary>"Kindle conectado: 3 livros prontos para enviar." Nothing is sent from here: it opens the confirmation.</summary>
    [ObservableProperty]
    public partial string? KindleBannerText { get; private set; }

    [ObservableProperty]
    public partial bool ShowKindleBanner { get; private set; }

    private void OnKindleChanged(object? sender, EventArgs e) => _ = UpdateKindleBannerAsync();

    /// <summary>Counts the prepared books of the Kindle destinations while a Kindle is connected.</summary>
    public async Task UpdateKindleBannerAsync()
    {
        if (_kindle.Device is null || _session.Destinations is not { } service)
        {
            // Unplugged: the next connection asks again.
            _kindleDismissed = false;
            ShowKindleBanner = false;
            return;
        }

        var ready = 0;
        _kindleToOpen = null;
        try
        {
            foreach (var destination in service.Destinations.OfType<KindleDestination>())
            {
                var count = (await service.Kindle.StatusAsync(destination, CancellationToken.None)).Values
                    .Count(s => s.State is KindleBookState.Ready or KindleBookState.ReadyToReplace);
                if (count > 0)
                {
                    _kindleToOpen ??= destination.Config.Id;
                    ready += count;
                }
            }
        }
        catch (Exception ex) when (LibrarySession.IsExpectedFailure(ex))
        {
            ready = 0;
        }

        KindleBannerText = string.Format(CultureInfo.CurrentCulture, ready == 1 ? Strings.Shell_KindleReadyOne : Strings.Shell_KindleReady, ready);
        ShowKindleBanner = ready > 0 && !_kindleDismissed;
    }

    [RelayCommand]
    private async Task SendToKindleAsync()
    {
        ShowKindleBanner = false;
        if (_kindleToOpen is { } id)
        {
            NavigateTo(NavPage.Destinations);
            await CurrentNavigation;
            await _destinationsPage.OpenAsync(id, showPlan: true);
        }
    }

    [RelayCommand]
    private void DismissKindle()
    {
        _kindleDismissed = true;
        ShowKindleBanner = false;
    }

    public void NavigateTo(NavPage page) => SelectedNavItem = NavItems.First(i => i.Page == page);

    /// <summary>Shows a page with the books chosen on another one (Biblioteca → Editor, Revisão → Editor…).</summary>
    public void Navigate(NavigationRequest request)
    {
        var item = NavItems.First(i => i.Page == request.Page);
        if (request.BookIds is { } ids)
        {
            item.Content.SetBooks(ids);
        }

        if (SelectedNavItem == item)
        {
            CurrentNavigation = item.Content.OnNavigatedToAsync();
        }
        else
        {
            SelectedNavItem = item;
        }
    }

    partial void OnSelectedNavItemChanged(NavItemViewModel? oldValue, NavItemViewModel? newValue)
    {
        oldValue?.Content.OnNavigatedFrom();
        if (newValue is { } value)
        {
            CurrentPage = value.Content;
            CurrentNavigation = value.Content.OnNavigatedToAsync();
        }
    }

    /// <summary>Files waiting in the inbox and books waiting in the Review.</summary>
    private async Task UpdateCountsAsync()
    {
        if (_session.Queue is { } queue)
        {
            var summary = await queue.GetSummaryAsync();
            var waiting = summary.Waiting + summary.Ready + summary.Queued;
            NavItems[1].Count = waiting > 0 ? waiting.ToString(CultureInfo.CurrentCulture) : null;
        }

        if (_session.Identification is { } identification)
        {
            var toReview = (await identification.GetCountsAsync()).ToReview;
            NavItems[2].Count = toReview > 0 ? toReview.ToString(CultureInfo.CurrentCulture) : null;
        }

        if (_session.Destinations is { } destinations)
        {
            var count = destinations.Destinations.Count;
            NavItems[4].Count = count > 0 ? count.ToString(CultureInfo.CurrentCulture) : null;
        }

        // A book finished preparing while the Kindle is connected.
        if (_kindle.Device is not null)
        {
            await UpdateKindleBannerAsync();
        }
    }

    private void OnQueueChanged(object? sender, EventArgs e) => _counts.Trigger();

    public void Dispose()
    {
        if (_session.Queue is { } queue)
        {
            queue.Changed -= OnQueueChanged;
        }

        if (_session.Identification is { } identification)
        {
            identification.Changed -= OnQueueChanged;
        }

        if (_session.Destinations is { } destinations)
        {
            destinations.Changed -= OnQueueChanged;
        }

        _kindle.Changed -= OnKindleChanged;
        _destinationsPage.KindleSent -= OnKindleChanged;
        _counts.Dispose();
        Activity.Dispose();
        DestinationsStatus.Dispose();
        foreach (var item in NavItems)
        {
            item.Content.Dispose();
        }
    }
}
