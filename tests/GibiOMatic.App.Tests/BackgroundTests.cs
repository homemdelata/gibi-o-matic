using System.Globalization;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
using GibiOMatic.App.Services.Tray;
using GibiOMatic.App.ViewModels;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Settings;
using GibiOMatic.Library.Import;
using GibiOMatic.Metadata;
using Microsoft.Extensions.Logging.Abstractions;

namespace GibiOMatic.App.Tests;

/// <summary>F5: the watched folder end to end, the tray, closing to the tray and starting with the system.</summary>
public sealed class BackgroundTests : IDisposable
{
    private readonly AppTestContext _app = new();

    public void Dispose() => _app.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string SagaInfo => SyntheticBooks.ComicInfo(series: "Saga", number: "1", year: 2012, writer: "Brian K. Vaughan",
        penciller: "Fiona Staples", publisher: "Image Comics", format: "TPB");

    private string Inbox(params string[] parts) => _app.Folder(Path.Combine(["Inbox", .. parts]));

    private async Task OpenLibraryAsync(string? inbox = null)
    {
        if (inbox is not null)
        {
            Directory.CreateDirectory(inbox);
            var settings = _app.Get<ISettingsService>();
            await settings.SaveAsync(settings.Current with { InboxPath = inbox }, Ct);
        }

        await _app.Get<LibrarySession>().CreateAsync(_app.Folder("Library"), Ct);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 600 && !condition(); i++)
        {
            await Task.Delay(25, Ct);
        }

        Assert.True(condition());
    }

    private static string Format(string format, int count) => string.Format(CultureInfo.CurrentCulture, format, count);

    private sealed class FakeHost : IAppHost
    {
        public List<NavPage?> Shown { get; } = [];

        public bool Exited { get; private set; }

        public void ShowMainWindow(NavPage? page = null) => Shown.Add(page);

        public void Exit() => Exited = true;
    }

    [Fact]
    public async Task A_download_goes_from_the_watched_folder_to_the_library_and_the_tray_tells_once()
    {
        _app.Identifier.Respond = (book, _) => book.Title.Contains("Saga", StringComparison.Ordinal)
            ? [FakeIdentifier.Candidate("Saga, Vol. 1", "Saga", "1", 0.98, MatchReason.Isbn, MatchReason.SecondSource)]
            : [];
        await OpenLibraryAsync(Inbox());
        var host = new FakeHost();
        using var tray = _app.Get<TrayController>();
        tray.Start(host);
        await tray.RefreshAsync();
        Assert.Contains(Inbox(), _app.Tray.Tooltip);

        SyntheticBooks.Cbz(Inbox("Saga_Vol1.cbz"), comicInfoXml: SagaInfo);
        SyntheticBooks.Pdf(Inbox("houseofslaughter_vol1.pdf"));

        await WaitForAsync(() => _app.Tray.Notifications.Count > 0);
        var (title, text) = Assert.Single(_app.Tray.Notifications);
        Assert.Equal(Format(Strings.Notify_Title, 2), title);
        Assert.Equal($"{Strings.Notify_ApprovedOne} · {Strings.Notify_ToReviewOne}", text);
        // By default the downloads go to the recycle bin once they are in the library.
        Assert.Equal(["houseofslaughter_vol1.pdf", "Saga_Vol1.cbz"], _app.RecycleBin.Recycled.Order(StringComparer.OrdinalIgnoreCase));
        Assert.False(Directory.Exists(Inbox(InboxFolder.ImportedFolderName)));
        var books = await _app.Get<LibrarySession>().Index!.GetBooksAsync(Ct);
        Assert.Equal(2, books.Count);

        // A click on the notification opens the Review, where the second book waits.
        _app.Tray.ClickNotification();
        Assert.Equal(NavPage.Review, Assert.Single(host.Shown));
    }

    [Fact]
    public async Task The_sidebar_shows_the_inbox_and_the_metadata_search_from_every_page()
    {
        var gate = new TaskCompletionSource();
        _app.Identifier.Gate = gate;
        await OpenLibraryAsync(Inbox());
        var main = _app.Get<MainWindowViewModel>();
        await main.InitializeAsync(Ct);
        var shell = Assert.IsType<ShellViewModel>(main.Content);
        var activity = shell.Activity;
        await activity.RefreshAsync();
        Assert.True(activity.ShowInbox);
        Assert.Equal(Strings.Status_InboxWatching, activity.InboxText);
        Assert.False(activity.ShowMetadata);

        SyntheticBooks.Cbz(Inbox("saga.cbz"), comicInfoXml: SagaInfo);
        await WaitForAsync(() => activity.ShowMetadata);
        Assert.Equal(Format(Strings.Status_MetadataSearching, 1), activity.MetadataText);
        Assert.True(activity.MetadataRunning);

        activity.OpenReviewCommand.Execute(null);
        Assert.Equal(NavPage.Review, shell.SelectedNavItem?.Page);
        activity.OpenInboxCommand.Execute(null);
        Assert.Equal(NavPage.Inbox, shell.SelectedNavItem?.Page);

        gate.SetResult();
        await WaitForAsync(() => !activity.ShowMetadata);
    }

    [Fact]
    public async Task A_new_version_shows_a_banner_and_installs_only_when_asked()
    {
        await OpenLibraryAsync();
        var main = _app.Get<MainWindowViewModel>();
        await main.InitializeAsync(Ct);
        var shell = Assert.IsType<ShellViewModel>(main.Content);
        var checker = _app.Get<UpdateChecker>();
        var restarts = 0;
        checker.RestartRequested += (_, _) => restarts++;

        await checker.CheckAsync(Ct);
        Assert.False(shell.Update.ShowBanner);
        Assert.Equal(Strings.Update_UpToDate, shell.Update.StatusText);

        _app.Updater.Offered = "0.7.0";
        await checker.CheckAsync(Ct);
        Assert.True(shell.Update.ShowBanner);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Update_Available, "0.7.0"), shell.Update.BannerText);
        Assert.Null(_app.Updater.Applied);

        // "Not now" hides it until the next check finds it again.
        shell.Update.DismissCommand.Execute(null);
        Assert.False(shell.Update.ShowBanner);
        await checker.CheckAsync(Ct);
        Assert.True(shell.Update.ShowBanner);

        await shell.Update.InstallCommand.ExecuteAsync(null);
        Assert.Equal("0.7.0", _app.Updater.Applied?.Version);
        Assert.Equal(1, restarts);
    }

    [Fact]
    public async Task The_settings_show_the_version_and_an_unreachable_source_is_an_error_not_a_crash()
    {
        await OpenLibraryAsync();
        var settings = _app.Get<SettingsPageViewModel>();
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Update_Version, "0.6.0"), settings.Update.VersionText);

        _app.Updater.Unreachable = true;
        await settings.Update.CheckCommand.ExecuteAsync(null);

        Assert.True(settings.Update.StatusIsError);
        Assert.Equal(Strings.Update_Failed, settings.Update.StatusText);
        Assert.True(settings.Update.CheckCommand.CanExecute(null));
    }

    [Fact]
    public void Without_the_installer_there_is_nothing_to_check()
    {
        _app.Updater.IsSupported = false;
        var update = new UpdateStatusViewModel(_app.Get<UpdateChecker>());
        Assert.False(update.IsSupported);
        Assert.Equal(Strings.Update_Unsupported, update.StatusText);
        Assert.False(update.CheckCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_tray_menu_opens_pauses_the_inbox_and_exits()
    {
        await OpenLibraryAsync(Inbox());
        var host = new FakeHost();
        using var tray = _app.Get<TrayController>();
        tray.Start(host);
        await tray.RefreshAsync();

        _app.Tray.Click();
        _app.Tray.Menu[0]!.OnClick();
        Assert.Equal([null, null], host.Shown);

        _app.Tray.Menu[1]!.OnClick();
        var queue = _app.Get<LibrarySession>().Queue!;
        await WaitForAsync(() => queue.IsPaused);
        await tray.RefreshAsync();
        Assert.Equal(Strings.Tray_ResumeInbox, _app.Tray.Menu[1]!.Text);
        Assert.EndsWith(Strings.Tray_InboxPaused, _app.Tray.Tooltip);

        Assert.Null(_app.Tray.Menu[2]);
        _app.Tray.Menu[3]!.OnClick();
        Assert.True(host.Exited);
    }

    [Fact]
    public async Task Without_an_inbox_folder_the_tray_and_the_inbox_page_say_so()
    {
        await OpenLibraryAsync();
        using var tray = _app.Get<TrayController>();
        tray.Start(new FakeHost());
        await tray.RefreshAsync();
        Assert.EndsWith(Strings.Tray_InboxOff, _app.Tray.Tooltip);

        var page = _app.Get<InboxPageViewModel>();
        Assert.Equal(InboxWatchStatus.Off, page.WatchStatus);
        NavigationRequest? request = null;
        page.NavigationRequested += (_, r) => request = r;
        page.ConfigureInboxCommand.Execute(null);
        Assert.Equal(NavPage.Settings, request?.Page);

        // Choosing the folder in the settings starts watching it.
        Directory.CreateDirectory(Inbox());
        var settings = _app.Get<SettingsPageViewModel>();
        settings.InboxPath = Inbox();
        await settings.SaveCommand.ExecuteAsync(null);
        await WaitForAsync(() => page.WatchStatus == InboxWatchStatus.Watching);
        Assert.Equal(Inbox(), page.WatchFolder);
    }

    [Fact]
    public void The_notification_names_only_what_happened_and_opens_the_right_page()
    {
        Assert.Equal((Strings.Notify_TitleOne, Strings.Notify_ApprovedOne, NavPage.Library),
            ArrivalText.Describe(new ArrivalSummary(1, 1, 0, 0, 0)));
        Assert.Equal((Format(Strings.Notify_Title, 4), $"{Format(Strings.Notify_Approved, 2)} · {Strings.Notify_AlreadyOne} · {Strings.Notify_FailedOne}", NavPage.Inbox),
            ArrivalText.Describe(new ArrivalSummary(4, 2, 0, 1, 1)));
        Assert.Equal(NavPage.Review, ArrivalText.Describe(new ArrivalSummary(3, 0, 3, 0, 0)).Page);
    }

    [Fact]
    public async Task The_first_close_asks_and_the_answer_is_remembered()
    {
        var window = _app.Get<MainWindowViewModel>();
        bool? chosen = null;
        window.CloseChosen += (_, keep) => chosen = keep;

        Assert.Equal(CloseAction.Exit, window.OnCloseRequested(trayAvailable: false));
        Assert.Equal(CloseAction.Ask, window.OnCloseRequested(trayAvailable: true));
        Assert.True(window.ShowClosePrompt);

        // Dismissed: asks again next time.
        window.CancelCloseCommand.Execute(null);
        Assert.False(window.ShowClosePrompt);
        Assert.Equal(CloseAction.Ask, window.OnCloseRequested(trayAvailable: true));

        await window.KeepInTrayCommand.ExecuteAsync(null);
        Assert.True(chosen);
        Assert.True(_app.Get<ISettingsService>().Current.Background.CloseToTray);
        Assert.Equal(CloseAction.Hide, window.OnCloseRequested(trayAvailable: true));
        Assert.False(window.ShowClosePrompt);

        var settings = _app.Get<ISettingsService>();
        await settings.SaveAsync(settings.Current with { Background = settings.Current.Background with { CloseToTray = null } }, Ct);
        window.OnCloseRequested(trayAvailable: true);
        await window.CloseAppCommand.ExecuteAsync(null);
        Assert.False(chosen);
        Assert.Equal(CloseAction.Exit, window.OnCloseRequested(trayAvailable: true));
    }

    [Fact]
    public async Task Settings_turn_the_start_with_the_system_on_in_the_tray_or_with_the_window()
    {
        await OpenLibraryAsync();
        var page = _app.Get<SettingsPageViewModel>();
        Assert.True(page.CloseToTray);
        Assert.False(page.StartWithSystem);
        Assert.True(page.StartInTray);

        page.StartWithSystem = true;
        await page.SaveCommand.ExecuteAsync(null);
        page.StartWithWindow = true;
        await page.SaveCommand.ExecuteAsync(null);
        page.StartWithSystem = false;
        await page.SaveCommand.ExecuteAsync(null);

        Assert.Equal([StartupMode.Tray, StartupMode.Window, StartupMode.Off], _app.Startup.Applied);
        var background = _app.Get<ISettingsService>().Current.Background;
        // The close question was never answered: saving other settings does not answer it.
        Assert.Equal((null, StartupMode.Off), (background.CloseToTray, background.StartWithSystem));

        page.CloseToTray = false;
        await page.SaveCommand.ExecuteAsync(null);
        Assert.False(_app.Get<ISettingsService>().Current.Background.CloseToTray);
        // Off and unchanged: the registry is not touched again.
        Assert.Equal(3, _app.Startup.Applied.Count);
    }

    [Fact]
    public async Task The_inbox_cannot_be_inside_the_library_or_around_it()
    {
        await OpenLibraryAsync();
        var page = _app.Get<SettingsPageViewModel>();

        foreach (var inbox in new[] { _app.Folder("Library"), Path.Combine(_app.Folder("Library"), "Inbox"), _app.Folder("") })
        {
            page.InboxPath = inbox;
            await page.SaveCommand.ExecuteAsync(null);
            Assert.True(page.StatusIsError, inbox);
            Assert.Null(_app.Get<ISettingsService>().Current.InboxPath);
        }
    }

    [Fact]
    public void The_desktop_entry_starts_the_app_in_the_tray()
    {
        var folder = _app.Folder("autostart");
        var startup = new LinuxStartupRegistration(NullLogger<LinuxStartupRegistration>.Instance, folder, "/opt/gibiomatic/GibiOMatic");

        Assert.True(startup.Apply(StartupMode.Tray));
        var entry = File.ReadAllText(Path.Combine(folder, LinuxStartupRegistration.FileName));
        Assert.Contains("Exec=\"/opt/gibiomatic/GibiOMatic\" --tray", entry);

        Assert.True(startup.Apply(StartupMode.Window));
        Assert.Contains("Exec=\"/opt/gibiomatic/GibiOMatic\"\n", File.ReadAllText(Path.Combine(folder, LinuxStartupRegistration.FileName)).ReplaceLineEndings("\n"));

        Assert.True(startup.Apply(StartupMode.Off));
        Assert.False(File.Exists(Path.Combine(folder, LinuxStartupRegistration.FileName)));
    }

    [Fact]
    public void The_run_key_points_at_the_app()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var keyPath = $@"Software\GibiOMatic.Tests\{Guid.NewGuid():N}";
        try
        {
            var startup = new WindowsStartupRegistration(NullLogger<WindowsStartupRegistration>.Instance, keyPath, @"C:\Apps\GibiOMatic.exe");
            Assert.True(startup.Apply(StartupMode.Tray));
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath)!)
            {
                Assert.Equal("\"C:\\Apps\\GibiOMatic.exe\" --tray", key.GetValue(WindowsStartupRegistration.ValueName));
            }

            Assert.True(startup.Apply(StartupMode.Off));
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath)!)
            {
                Assert.Null(key.GetValue(WindowsStartupRegistration.ValueName));
            }
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\GibiOMatic.Tests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Updates_come_from_github_unless_a_local_folder_is_set()
    {
        Assert.IsType<Velopack.Sources.GithubSource>(VelopackUpdater.CreateSource(null));
        Assert.IsType<Velopack.Sources.GithubSource>(VelopackUpdater.CreateSource(" "));
        Assert.IsType<Velopack.Sources.SimpleFileSource>(VelopackUpdater.CreateSource(@"D:\releases"));
    }

    [Fact]
    public async Task A_second_start_hands_its_command_line_to_the_first()
    {
        var name = $"GibiOMatic.Tests.{Guid.NewGuid():N}";
        using var first = SingleInstance.Acquire(name);
        Assert.True(first.IsFirst);
        var received = new TaskCompletionSource<IReadOnlyList<string>>();
        first.Activated += (_, args) => received.TrySetResult(args);
        first.Listen();

        using var second = SingleInstance.Acquire(name);
        Assert.False(second.IsFirst);
        Assert.True(second.SignalFirst(["--tray"], TimeSpan.FromSeconds(5)));

        Assert.Equal(["--tray"], await received.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }
}
