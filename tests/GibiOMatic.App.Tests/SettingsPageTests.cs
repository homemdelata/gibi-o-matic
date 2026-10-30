using GibiOMatic.App.Services;
using GibiOMatic.App.ViewModels;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Ids;
using GibiOMatic.Core.Settings;
using GibiOMatic.Library;

namespace GibiOMatic.App.Tests;

public sealed class SettingsPageTests : IDisposable
{
    private readonly AppTestContext _app = new();

    public void Dispose() => _app.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> OpenLibraryAsync()
    {
        var folder = _app.Folder("Library");
        await _app.Get<LibrarySession>().CreateAsync(folder, Ct);
        return folder;
    }

    [Fact]
    public async Task Save_persists_inbox_and_language_and_flags_the_restart()
    {
        await _app.EnterUiThreadAsync();
        await OpenLibraryAsync();
        var page = _app.Get<SettingsPageViewModel>();
        Assert.False(page.RestartRequired);

        _app.Picker.WillReturn(_app.Folder("Downloads"));
        await page.ChooseInboxCommand.ExecuteAsync(null);
        page.SelectedLanguage = page.Languages.Single(l => l.Code == "en");
        await page.SaveCommand.ExecuteAsync(null);

        Assert.True(page.RestartRequired);
        Assert.False(page.StatusIsError);
        var saved = await new JsonSettingsService(_app.Paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonSettingsService>.Instance).LoadAsync(Ct);
        Assert.Equal("en", saved.Language);
        Assert.Equal(_app.Folder("Downloads"), saved.InboxPath);
        Assert.Equal(_app.Folder("Library"), saved.LibraryPath);
    }

    [Fact]
    public async Task Restore_defaults_keeps_the_library()
    {
        await _app.EnterUiThreadAsync();
        await OpenLibraryAsync();
        var page = _app.Get<SettingsPageViewModel>();
        page.InboxPath = "somewhere";
        page.SelectedLanguage = page.Languages.Single(l => l.Code == "en");

        page.RestoreDefaultsCommand.Execute(null);
        await page.SaveCommand.ExecuteAsync(null);

        Assert.Null(page.InboxPath);
        Assert.Equal("pt-BR", page.SelectedLanguage.Code);
        Assert.Equal(_app.Folder("Library"), _app.Get<ISettingsService>().Current.LibraryPath);
    }

    [Fact]
    public async Task Rebuild_index_reports_how_many_books_were_found()
    {
        await _app.EnterUiThreadAsync();
        var root = await OpenLibraryAsync();
        var bookFolder = Path.Combine(root, LibraryLayout.BooksFolderName, "_No publisher", "Plutona [abcdef]");
        Directory.CreateDirectory(bookFolder);
        await MetadataFile.WriteAsync(Path.Combine(bookFolder, LibraryLayout.MetadataFileName),
            new BookMetadata { Id = Ulid.NewUlid(), Title = "Plutona" }, Ct);
        var page = _app.Get<SettingsPageViewModel>();

        await page.RebuildIndexCommand.ExecuteAsync(null);

        Assert.False(page.StatusIsError);
        Assert.Contains("1", page.StatusMessage);
    }

    [Fact]
    public async Task Opening_another_library_switches_the_session()
    {
        await _app.EnterUiThreadAsync();
        await OpenLibraryAsync();
        var other = _app.Folder("Outra");
        await _app.Get<GibiOMatic.Core.Libraries.ILibraryService>().CreateAsync(other, Ct);
        var page = _app.Get<SettingsPageViewModel>();
        _app.Picker.WillReturn(other);

        await page.OpenOtherLibraryCommand.ExecuteAsync(null);

        Assert.Equal(other, _app.Get<LibrarySession>().Current?.RootPath);
        Assert.Equal(other, _app.Get<ISettingsService>().Current.LibraryPath);
    }

    [Fact]
    public async Task The_preferred_format_order_can_be_changed_and_restored()
    {
        await _app.EnterUiThreadAsync();
        await OpenLibraryAsync();
        var page = _app.Get<SettingsPageViewModel>();
        Assert.Equal([BookFormat.Cbz, BookFormat.Pdf, BookFormat.Epub, BookFormat.Cbr], page.Formats.Select(f => f.Format));

        page.MoveFormatUpCommand.Execute(page.Formats[1]);
        page.MoveFormatDownCommand.Execute(page.Formats[3]); // last one: nothing happens
        await page.SaveCommand.ExecuteAsync(null);

        Assert.Equal([BookFormat.Pdf, BookFormat.Cbz, BookFormat.Epub, BookFormat.Cbr], _app.Get<ISettingsService>().Current.FormatPreference);
        Assert.Equal(["1", "2", "3", "4"], page.Formats.Select(f => f.Rank));

        page.RestoreDefaultsCommand.Execute(null);
        Assert.Equal(AppSettings.DefaultFormatPreference, page.Formats.Select(f => f.Format));
    }
}
