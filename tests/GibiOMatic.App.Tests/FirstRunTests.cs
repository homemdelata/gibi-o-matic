using GibiOMatic.App.ViewModels;
using GibiOMatic.Core.Settings;
using GibiOMatic.Library;
using Microsoft.Extensions.DependencyInjection;

namespace GibiOMatic.App.Tests;

public sealed class FirstRunTests : IDisposable
{
    private readonly AppTestContext _app = new();

    public void Dispose() => _app.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<MainWindowViewModel> StartAsync(IServiceProvider services)
    {
        await services.GetRequiredService<ISettingsService>().LoadAsync(Ct);
        var main = services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync(Ct);
        return main;
    }

    [Fact]
    public async Task Without_a_remembered_library_the_welcome_screen_is_shown()
    {
        await _app.EnterUiThreadAsync();
        var main = await StartAsync(_app.CreateServices());

        var welcome = Assert.IsType<WelcomeViewModel>(main.Content);
        Assert.Null(welcome.ErrorMessage);
    }

    [Fact]
    public async Task Creating_a_library_opens_the_shell_on_the_empty_library_and_is_remembered()
    {
        await _app.EnterUiThreadAsync();
        var main = await StartAsync(_app.CreateServices());
        var folder = _app.Folder("Library");
        _app.Picker.WillReturn(folder);

        await ((WelcomeViewModel)main.Content!).CreateLibraryCommand.ExecuteAsync(null);

        var shell = Assert.IsType<ShellViewModel>(main.Content);
        await shell.CurrentNavigation;
        Assert.Equal(NavPage.Library, shell.SelectedNavItem?.Page);
        var library = Assert.IsType<LibraryPageViewModel>(shell.CurrentPage);
        Assert.True(library.IsEmpty);
        Assert.Equal("0", shell.NavItems[0].Count);
        Assert.True(File.Exists(Path.Combine(folder, LibraryLayout.ManifestFileName)));

        // Next run on the same machine goes straight to the library.
        using var secondRun = _app.CreateServices();
        var reopened = await StartAsync(secondRun);
        var reopenedShell = Assert.IsType<ShellViewModel>(reopened.Content);
        Assert.Equal(folder, reopenedShell.LibraryPath);
    }

    [Fact]
    public async Task Cancelling_the_folder_picker_changes_nothing()
    {
        await _app.EnterUiThreadAsync();
        var main = await StartAsync(_app.CreateServices());
        _app.Picker.WillReturn(null);

        await ((WelcomeViewModel)main.Content!).OpenLibraryCommand.ExecuteAsync(null);

        Assert.IsType<WelcomeViewModel>(main.Content);
        Assert.False(File.Exists(_app.Paths.SettingsFile));
    }

    [Fact]
    public async Task Opening_a_folder_that_is_not_a_library_shows_an_error()
    {
        await _app.EnterUiThreadAsync();
        var main = await StartAsync(_app.CreateServices());
        var folder = _app.Folder("Downloads");
        Directory.CreateDirectory(folder);
        _app.Picker.WillReturn(folder);

        var welcome = (WelcomeViewModel)main.Content!;
        await welcome.OpenLibraryCommand.ExecuteAsync(null);

        Assert.Same(welcome, main.Content);
        Assert.Contains("gibiomatic-library.json", welcome.ErrorMessage);
    }

    private async Task RememberAsync(string folder) =>
        await new JsonSettingsService(_app.Paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonSettingsService>.Instance)
            .SaveAsync(new AppSettings { LibraryPath = folder }, Ct);

    [Fact]
    public async Task A_remembered_library_that_disappeared_shows_the_error_and_waits_for_a_retry()
    {
        await _app.EnterUiThreadAsync();
        var missing = _app.Folder("Sumiu");
        await RememberAsync(missing);

        using var services = _app.CreateServices();
        var main = await StartAsync(services);

        var opening = Assert.IsType<LibraryOpeningViewModel>(main.Content);
        Assert.Contains(missing, opening.ErrorMessage);
        Assert.False(opening.IsSlow);

        // The drive is back: "Try again" opens it.
        await services.GetRequiredService<LibraryService>().CreateAsync(missing, Ct);
        await opening.RetryCommand.ExecuteAsync(null);

        var shell = Assert.IsType<ShellViewModel>(main.Content);
        Assert.Equal(missing, shell.LibraryPath);
    }

    [Fact]
    public async Task Choosing_another_library_from_the_error_shows_the_welcome_screen()
    {
        await _app.EnterUiThreadAsync();
        await RememberAsync(_app.Folder("Sumiu"));
        var main = await StartAsync(_app.CreateServices());

        ((LibraryOpeningViewModel)main.Content!).ChooseAnotherCommand.Execute(null);

        Assert.IsType<WelcomeViewModel>(main.Content);
    }

    [Fact]
    public async Task A_slow_library_offers_to_keep_waiting_or_choose_another()
    {
        await _app.EnterUiThreadAsync();
        var folder = _app.Folder("Lenta");
        using var services = _app.CreateServices();
        await services.GetRequiredService<LibraryService>().CreateAsync(folder, Ct);
        await RememberAsync(folder);
        await services.GetRequiredService<ISettingsService>().LoadAsync(Ct);
        var main = services.GetRequiredService<MainWindowViewModel>();
        main.SlowOpeningAfter = TimeSpan.Zero;

        // The library is abandoned the moment the slow warning shows up.
        LibraryOpeningViewModel? warned = null;
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.Content) && main.Content is LibraryOpeningViewModel opening && warned is null)
            {
                warned = opening;
            }
        };
        var starting = main.InitializeAsync(Ct);
        Assert.NotNull(warned);
        Assert.True(warned.IsSlow);
        Assert.False(warned.IsOpening);
        warned.ChooseAnotherCommand.Execute(null);
        await starting;

        // A late answer from the disk does not replace the welcome screen.
        Assert.IsType<WelcomeViewModel>(main.Content);
        Assert.Null(services.GetRequiredService<GibiOMatic.App.Services.LibrarySession>().Current);
    }

    [Fact]
    public void Keep_waiting_hides_the_slow_warning()
    {
        var opening = new LibraryOpeningViewModel(@"G:\Biblioteca", () => Task.CompletedTask, () => { }) { IsSlow = true };

        opening.KeepWaitingCommand.Execute(null);

        Assert.False(opening.IsSlow);
        Assert.True(opening.IsOpening);
    }

    [Fact]
    public async Task Creating_a_library_in_a_folder_with_files_asks_first()
    {
        await _app.EnterUiThreadAsync();
        var main = await StartAsync(_app.CreateServices());
        var folder = _app.Folder("Fotos");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "foto.jpg"), "x", Ct);
        var welcome = (WelcomeViewModel)main.Content!;

        _app.Picker.WillReturn(folder);
        await welcome.CreateLibraryCommand.ExecuteAsync(null);
        Assert.True(welcome.IsConfirmingCreate);
        Assert.False(File.Exists(Path.Combine(folder, LibraryLayout.ManifestFileName)));

        welcome.CancelCreateCommand.Execute(null);
        Assert.False(welcome.IsConfirmingCreate);

        _app.Picker.WillReturn(folder);
        await welcome.CreateLibraryCommand.ExecuteAsync(null);
        await welcome.ConfirmCreateCommand.ExecuteAsync(null);

        Assert.IsType<ShellViewModel>(main.Content);
        Assert.True(File.Exists(Path.Combine(folder, "foto.jpg")), "existing files are not touched");
    }
}
