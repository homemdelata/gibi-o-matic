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
        var main = await StartAsync(_app.CreateServices());

        var welcome = Assert.IsType<WelcomeViewModel>(main.Content);
        Assert.Null(welcome.ErrorMessage);
    }

    [Fact]
    public async Task Creating_a_library_opens_the_shell_on_the_empty_library_and_is_remembered()
    {
        var main = await StartAsync(_app.CreateServices());
        var folder = _app.Folder("Arquivo");
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
        var main = await StartAsync(_app.CreateServices());
        _app.Picker.WillReturn(null);

        await ((WelcomeViewModel)main.Content!).OpenLibraryCommand.ExecuteAsync(null);

        Assert.IsType<WelcomeViewModel>(main.Content);
        Assert.False(File.Exists(_app.Paths.SettingsFile));
    }

    [Fact]
    public async Task Opening_a_folder_that_is_not_a_library_shows_an_error()
    {
        var main = await StartAsync(_app.CreateServices());
        var folder = _app.Folder("Downloads");
        Directory.CreateDirectory(folder);
        _app.Picker.WillReturn(folder);

        var welcome = (WelcomeViewModel)main.Content!;
        await welcome.OpenLibraryCommand.ExecuteAsync(null);

        Assert.Same(welcome, main.Content);
        Assert.Contains("gibiomatic-library.json", welcome.ErrorMessage);
    }

    [Fact]
    public async Task A_remembered_library_that_disappeared_falls_back_to_welcome_with_an_explanation()
    {
        var missing = _app.Folder("Sumiu");
        await new JsonSettingsService(_app.Paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonSettingsService>.Instance)
            .SaveAsync(new AppSettings { LibraryPath = missing }, Ct);

        var main = await StartAsync(_app.CreateServices());

        var welcome = Assert.IsType<WelcomeViewModel>(main.Content);
        Assert.Contains(missing, welcome.ErrorMessage);
    }
}
