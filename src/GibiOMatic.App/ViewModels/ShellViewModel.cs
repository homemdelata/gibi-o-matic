using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;

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

    public ShellViewModel(LibrarySession session, LibraryPageViewModel libraryPage, InboxPageViewModel inboxPage,
        ReviewPageViewModel reviewPage, EditorPageViewModel editorPage, DestinationsPageViewModel destinationsPage, SettingsPageViewModel settingsPage, IUiThread ui)
    {
        _session = session;
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
        await DestinationsStatus.RefreshAsync();
        await CurrentNavigation;
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

    partial void OnSelectedNavItemChanged(NavItemViewModel? value)
    {
        if (value is not null)
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

        _counts.Dispose();
        DestinationsStatus.Dispose();
        foreach (var item in NavItems)
        {
            item.Content.Dispose();
        }
    }
}
