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

    /// <summary>Show the counter as a yellow pill (things waiting for the user).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlainCount), nameof(HasBadge))]
    public partial bool IsBadge { get; set; }

    public bool HasPlainCount => !string.IsNullOrEmpty(Count) && !IsBadge;

    public bool HasBadge => !string.IsNullOrEmpty(Count) && IsBadge;
}

/// <summary>Main layout once a library is open: sidebar navigation plus the current page.</summary>
public sealed partial class ShellViewModel : ViewModelBase, IDisposable
{
    private readonly LibrarySession _session;
    private readonly Debouncer _inboxCount;

    public ShellViewModel(LibrarySession session, LibraryPageViewModel libraryPage, InboxPageViewModel inboxPage,
        SettingsPageViewModel settingsPage, IUiThread ui)
    {
        _session = session;
        LibraryPath = session.Current?.RootPath ?? string.Empty;

        NavItems =
        [
            new NavItemViewModel(NavPage.Library, Strings.Nav_Library, libraryPage),
            new NavItemViewModel(NavPage.Inbox, Strings.Nav_Inbox, inboxPage),
            new NavItemViewModel(NavPage.Review, Strings.Nav_Review, new PlaceholderPageViewModel(Strings.Nav_Review, Strings.Review_Subtitle)),
            new NavItemViewModel(NavPage.Editor, Strings.Nav_Editor, new PlaceholderPageViewModel(Strings.Nav_Editor, Strings.Editor_Subtitle)),
            new NavItemViewModel(NavPage.Destinations, Strings.Nav_Destinations, new PlaceholderPageViewModel(Strings.Nav_Destinations, Strings.Destinations_Subtitle)),
            new NavItemViewModel(NavPage.Settings, Strings.Nav_Settings, settingsPage),
        ];
        NavItems[1].IsBadge = true;

        libraryPage.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LibraryPageViewModel.BookCount))
            {
                NavItems[0].Count = libraryPage.BookCount?.ToString(CultureInfo.CurrentCulture);
            }
        };
        libraryPage.NavigationRequested += (_, page) => NavigateTo(page);

        _inboxCount = new Debouncer(ui, TimeSpan.FromMilliseconds(300), UpdateInboxCountAsync);
        if (session.Queue is { } queue)
        {
            queue.Changed += OnQueueChanged;
        }
    }

    public IReadOnlyList<NavItemViewModel> NavItems { get; }

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
        await UpdateInboxCountAsync();
        await CurrentNavigation;
    }

    public void NavigateTo(NavPage page) => SelectedNavItem = NavItems.First(i => i.Page == page);

    partial void OnSelectedNavItemChanged(NavItemViewModel? value)
    {
        if (value is not null)
        {
            CurrentPage = value.Content;
            CurrentNavigation = value.Content.OnNavigatedToAsync();
        }
    }

    /// <summary>Files waiting in the inbox (to analyse, to confirm or to import).</summary>
    private async Task UpdateInboxCountAsync()
    {
        if (_session.Queue is not { } queue)
        {
            return;
        }

        var summary = await queue.GetSummaryAsync();
        var waiting = summary.Waiting + summary.Ready + summary.Queued;
        NavItems[1].Count = waiting > 0 ? waiting.ToString(CultureInfo.CurrentCulture) : null;
    }

    private void OnQueueChanged(object? sender, EventArgs e) => _inboxCount.Trigger();

    public void Dispose()
    {
        if (_session.Queue is { } queue)
        {
            queue.Changed -= OnQueueChanged;
        }

        _inboxCount.Dispose();
        foreach (var item in NavItems)
        {
            item.Content.Dispose();
        }
    }
}
