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
    public partial string? Count { get; set; }
}

/// <summary>Main layout once a library is open: sidebar navigation plus the current page.</summary>
public sealed partial class ShellViewModel : ViewModelBase
{
    public ShellViewModel(LibrarySession session, LibraryPageViewModel libraryPage, SettingsPageViewModel settingsPage)
    {
        LibraryPath = session.Current?.RootPath ?? string.Empty;

        NavItems =
        [
            new NavItemViewModel(NavPage.Library, Strings.Nav_Library, libraryPage),
            new NavItemViewModel(NavPage.Inbox, Strings.Nav_Inbox, new PlaceholderPageViewModel(Strings.Nav_Inbox, Strings.Inbox_Subtitle)),
            new NavItemViewModel(NavPage.Review, Strings.Nav_Review, new PlaceholderPageViewModel(Strings.Nav_Review, Strings.Review_Subtitle)),
            new NavItemViewModel(NavPage.Editor, Strings.Nav_Editor, new PlaceholderPageViewModel(Strings.Nav_Editor, Strings.Editor_Subtitle)),
            new NavItemViewModel(NavPage.Destinations, Strings.Nav_Destinations, new PlaceholderPageViewModel(Strings.Nav_Destinations, Strings.Destinations_Subtitle)),
            new NavItemViewModel(NavPage.Settings, Strings.Nav_Settings, settingsPage),
        ];

        libraryPage.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LibraryPageViewModel.BookCount))
            {
                NavItems[0].Count = libraryPage.BookCount?.ToString(System.Globalization.CultureInfo.CurrentCulture);
            }
        };
    }

    public IReadOnlyList<NavItemViewModel> NavItems { get; }

    public string LibraryPath { get; }

    [ObservableProperty]
    public partial NavItemViewModel? SelectedNavItem { get; set; }

    [ObservableProperty]
    public partial PageViewModel? CurrentPage { get; private set; }

    /// <summary>The page load started by the last navigation (pages handle their own errors).</summary>
    public Task CurrentNavigation { get; private set; } = Task.CompletedTask;

    public Task InitializeAsync()
    {
        NavigateTo(NavPage.Library);
        return CurrentNavigation;
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
}
