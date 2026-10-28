using GibiOMatic.App.Services;
using GibiOMatic.App.ViewModels;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Settings;
using GibiOMatic.Library.Editing;

namespace GibiOMatic.App.Tests;

public sealed class DestinationsPageTests : IDisposable
{
    private readonly AppTestContext _app = new();

    public void Dispose() => _app.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private LibrarySession Session => _app.Get<LibrarySession>();

    private string KomgaFolder => _app.Folder("Komga");

    private async Task ImportSagaAsync()
    {
        await Session.CreateAsync(_app.Folder("Library"), Ct);
        string Info(string number, int year) => SyntheticBooks.ComicInfo(series: "Saga", number: number, year: year, writer: "Brian K. Vaughan",
            publisher: "Image Comics", format: "TPB");
        await Session.Queue!.AddAsync(
        [
            SyntheticBooks.Cbz(_app.Folder("Downloads/saga1.cbz"), pages: 2, comicInfoXml: Info("1", 2012)),
            SyntheticBooks.Cbz(_app.Folder("Downloads/saga2.cbz"), pages: 3, comicInfoXml: Info("2", 2013)),
        ], Ct);
        await Session.Queue.WaitUntilIdleAsync(Ct);
        await Session.Queue.ConfirmAsync(Ct);
        await Session.Queue.WaitUntilIdleAsync(Ct);
        await Session.Identification!.WaitUntilIdleAsync(Ct);
        await Session.Destinations!.WaitUntilIdleAsync(Ct);
    }

    /// <summary>Goes through the three wizard steps and creates a Komga destination.</summary>
    private async Task<DestinationsPageViewModel> CreateKomgaAsync(bool includeUnreviewed = true, bool chosenBooks = false)
    {
        Directory.CreateDirectory(KomgaFolder);
        var page = _app.Get<DestinationsPageViewModel>();
        await page.OnNavigatedToAsync(Ct);
        await page.AddDestinationCommand.ExecuteAsync(null);
        var wizard = page.Wizard!;

        wizard.NextCommand.Execute(null);
        wizard.NextCommand.Execute(null);
        Assert.True(wizard.IsStep2);
        Assert.NotNull(wizard.Error);

        _app.Picker.WillReturn(KomgaFolder);
        await wizard.Form.ChooseFolderCommand.ExecuteAsync(null);
        Assert.Equal("Saga/Saga v01 (2012).cbz", wizard.Form.Preview);
        wizard.NextCommand.Execute(null);
        Assert.True(wizard.IsStep3);

        wizard.Form.IncludeUnreviewed = includeUnreviewed;
        wizard.Form.IsChosenBooks = chosenBooks;
        await wizard.CreateCommand.ExecuteAsync(null);
        return page;
    }

    [Fact]
    public async Task A_new_destination_shows_its_plan_and_writes_only_after_confirmation()
    {
        await ImportSagaAsync();
        var page = await CreateKomgaAsync();

        Assert.True(page.IsDetail);
        var detail = page.Detail!;
        Assert.True(detail.HasPlan);
        Assert.True(detail.CanSync);
        Assert.Equal(2, detail.PlanItems.Count);
        Assert.Empty(Directory.GetFiles(KomgaFolder, "*", SearchOption.AllDirectories));

        detail.SyncCommand.Execute(null);
        await Session.Destinations!.WaitUntilIdleAsync(Ct);
        await page.RefreshAsync(Ct);

        Assert.True(File.Exists(Path.Combine(KomgaFolder, "Saga", "Saga v01 (2012).cbz")));
        Assert.True(File.Exists(Path.Combine(KomgaFolder, "Saga", "Saga v02 (2013).cbz")));
        Assert.Equal(StatusKind.Ok, page.Cards.Single().StatusKind);
        Assert.NotNull(detail.ResultText);
        Assert.False(detail.ResultIsError);
        Assert.False(detail.IsRunning);
        Assert.False(page.Cards.Single().IsRunning);
        Assert.Equal(2, detail.History.Count(h => h.Text == Resources.Strings.History_Created));
        Assert.DoesNotContain(detail.History, h => h.IsError);

        var library = _app.Get<LibraryPageViewModel>();
        await library.LoadAsync(Ct);
        library.VisibleCards.OfType<SeriesCardViewModel>().Single().OpenCommand.Execute(null);
        library.VisibleCards.OfType<BookCardViewModel>().First().SelectCommand.Execute(null);
        await WaitForAsync(() => library.Selected!.Copies.Count > 0);
        Assert.Equal(StatusKind.Ok, library.Selected!.Copies.Single().Kind);
    }

    [Fact]
    public async Task The_cards_show_up_before_their_status_is_checked()
    {
        await ImportSagaAsync();
        await CreateKomgaAsync();
        var page = _app.Get<DestinationsPageViewModel>();
        page.ShowList();
        var shell = _app.Get<ShellViewModel>();

        // A network drive can take a minute to answer: the lists do not wait for it.
        var refreshing = page.RefreshAsync(Ct);
        var sidebar = shell.DestinationsStatus.RefreshAsync();
        Assert.Single(page.Cards);
        Assert.Single(shell.DestinationsStatus.Items);
        await Task.WhenAll(refreshing, sidebar);

        Assert.Equal(StatusKind.Warn, page.Cards.Single().StatusKind);
        Assert.Equal(StatusKind.Warn, shell.DestinationsStatus.Items.Single().Kind);
    }

    [Fact]
    public void A_card_says_it_is_checking_until_its_summary_arrives()
    {
        var card = new DestinationCardViewModel(new DestinationConfig { Id = "01ARYZ6S41TSV4RRFFQ69G5KOM", Type = "komga", Name = "Komga de casa" }, (_, _) => { });

        Assert.Null(card.Summary);
        Assert.Equal((Resources.Strings.Destination_Checking, StatusKind.Neutral), (card.StatusText, card.StatusKind));
    }

    [Fact]
    public async Task The_sidebar_shows_each_destination_status()
    {
        await ImportSagaAsync();
        var shell = _app.Get<ShellViewModel>();
        var page = await CreateKomgaAsync();
        await shell.DestinationsStatus.RefreshAsync();
        var line = Assert.Single(shell.DestinationsStatus.Items);
        Assert.Equal((Resources.Strings.Wizard_DefaultKomgaName, StatusKind.Warn), (line.Name, line.Kind));

        page.Detail!.SyncCommand.Execute(null);
        await Session.Destinations!.WaitUntilIdleAsync(Ct);
        await shell.DestinationsStatus.RefreshAsync();

        line = Assert.Single(shell.DestinationsStatus.Items);
        Assert.Equal((Resources.Strings.Sidebar_UpToDate, StatusKind.Ok, false), (line.StatusText, line.Kind, line.IsRunning));
    }

    [Fact]
    public async Task Coming_back_to_destinations_shows_the_list_and_the_sidebar_opens_a_destination()
    {
        await ImportSagaAsync();
        var main = _app.Get<MainWindowViewModel>();
        await main.InitializeAsync(Ct);
        var shell = Assert.IsType<ShellViewModel>(main.Content);
        await shell.CurrentNavigation;
        var page = (DestinationsPageViewModel)shell.NavItems.Single(i => i.Page == NavPage.Destinations).Content;
        shell.NavigateTo(NavPage.Destinations);
        await shell.CurrentNavigation;
        Directory.CreateDirectory(KomgaFolder);
        await page.AddDestinationCommand.ExecuteAsync(null);
        page.Wizard!.NextCommand.Execute(null);
        _app.Picker.WillReturn(KomgaFolder);
        await page.Wizard.Form.ChooseFolderCommand.ExecuteAsync(null);
        page.Wizard.NextCommand.Execute(null);
        await page.Wizard.CreateCommand.ExecuteAsync(null);
        Assert.True(page.IsDetail);

        shell.NavigateTo(NavPage.Library);
        await shell.CurrentNavigation;
        shell.NavigateTo(NavPage.Destinations);
        await shell.CurrentNavigation;
        Assert.True(page.IsList);

        shell.NavigateTo(NavPage.Library);
        await shell.CurrentNavigation;
        await shell.DestinationsStatus.RefreshAsync();
        shell.DestinationsStatus.Items.Single().OpenCommand.Execute(null);
        await WaitForAsync(() => page.IsDetail);
        Assert.Same(page, shell.CurrentPage);
    }

    [Fact]
    public async Task A_copy_that_fails_shows_up_in_the_history_and_on_the_card()
    {
        await ImportSagaAsync();
        var page = await CreateKomgaAsync();
        var book = (await Session.Index!.GetBooksAsync(Ct)).Single(b => b.Metadata.Series?.Number == "2");
        File.Delete(Path.Combine(Session.Current!.RootPath, book.Folder, book.MainFile!.Name));

        page.Detail!.SyncCommand.Execute(null);
        await Session.Destinations!.WaitUntilIdleAsync(Ct);
        await page.RefreshAsync(Ct);

        var failure = Assert.Single(page.Detail.History, h => h.IsError && h.Detail is not null && h.Text.Contains("Saga v02", StringComparison.Ordinal));
        Assert.NotNull(failure.Detail);
        Assert.True(page.Detail.ResultIsError);
        Assert.Equal(StatusKind.Bad, page.Cards.Single().StatusKind);
        Assert.Single(Directory.GetFiles(KomgaFolder, "*.cbz", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Unreviewed_books_stay_out_unless_the_option_is_on()
    {
        await ImportSagaAsync();
        var page = await CreateKomgaAsync(includeUnreviewed: false);

        Assert.False(page.Detail!.CanSync);
        Assert.Empty(page.Detail.PlanItems);
    }

    [Fact]
    public async Task After_the_first_sync_edits_update_the_copy_on_their_own()
    {
        await ImportSagaAsync();
        var page = await CreateKomgaAsync();
        page.Detail!.SyncCommand.Execute(null);
        await Session.Destinations!.WaitUntilIdleAsync(Ct);

        var book = (await Session.Index!.GetBooksAsync(Ct)).Single(b => b.Metadata.Series?.Number == "1");
        await _app.Get<BookEditor>().UpdateAsync(Session.Current!, book.Metadata.Id, m => m with { Date = new PartialDate(2011) }, Ct);
        await WaitForAsync(() => File.Exists(Path.Combine(KomgaFolder, "Saga", "Saga v01 (2011).cbz")));
        await Session.Destinations.WaitUntilIdleAsync(Ct);

        Assert.False(File.Exists(Path.Combine(KomgaFolder, "Saga", "Saga v01 (2012).cbz")));
    }

    [Fact]
    public async Task A_change_the_destination_missed_is_applied_when_the_library_opens_again()
    {
        await ImportSagaAsync();
        var page = await CreateKomgaAsync();
        page.Detail!.SyncCommand.Execute(null);
        await Session.Destinations!.WaitUntilIdleAsync(Ct);

        // The drive is away while the book is edited: the copy cannot follow yet.
        var away = KomgaFolder + "-away";
        Directory.Move(KomgaFolder, away);
        var book = (await Session.Index!.GetBooksAsync(Ct)).Single(b => b.Metadata.Series?.Number == "1");
        await _app.Get<BookEditor>().UpdateAsync(Session.Current!, book.Metadata.Id, m => m with { Date = new PartialDate(2011) }, Ct);
        await Session.Destinations.WaitUntilIdleAsync(Ct);

        Directory.Move(away, KomgaFolder);
        await Session.OpenAsync(Session.Current!.RootPath, Ct);
        await Session.Destinations!.WaitUntilIdleAsync(Ct);

        Assert.True(File.Exists(Path.Combine(KomgaFolder, "Saga", "Saga v01 (2011).cbz")));
        Assert.False(File.Exists(Path.Combine(KomgaFolder, "Saga", "Saga v01 (2012).cbz")));
    }

    [Fact]
    public async Task Chosen_books_are_added_from_the_library_selection()
    {
        await ImportSagaAsync();
        var page = await CreateKomgaAsync(chosenBooks: true);
        Assert.False(page.Detail!.CanSync);

        var library = _app.Get<LibraryPageViewModel>();
        await library.LoadAsync(Ct);
        library.ToggleSelectingCommand.Execute(null);
        library.CheckAllVisibleCommand.Execute(null);
        var choice = Assert.Single(library.ChosenDestinations);
        await choice.IncludeCommand.ExecuteAsync(null);

        Assert.Equal(2, _app.Get<ISettingsService>().Current.Destinations.Single().ChosenBookIds.Count);
        await page.Detail.ShowPlanCommand.ExecuteAsync(null);
        Assert.Equal(2, page.Detail.PlanItems.Count);
    }

    [Fact]
    public async Task Removing_a_destination_keeps_its_files()
    {
        await ImportSagaAsync();
        var shell = _app.Get<ShellViewModel>();
        var page = await CreateKomgaAsync();
        await WaitForAsync(() => shell.NavItems.Single(i => i.Page == NavPage.Destinations).Count == "1");
        page.Detail!.SyncCommand.Execute(null);
        await Session.Destinations!.WaitUntilIdleAsync(Ct);

        page.Detail.AskRemoveCommand.Execute(null);
        await page.Detail.ConfirmRemoveCommand.ExecuteAsync(null);
        await page.RefreshAsync(Ct);

        Assert.True(page.IsList);
        Assert.Empty(page.Cards);
        Assert.Empty(_app.Get<ISettingsService>().Current.Destinations);
        Assert.Equal(2, Directory.GetFiles(KomgaFolder, "*.cbz", SearchOption.AllDirectories).Length);
        await WaitForAsync(() => shell.NavItems.Single(i => i.Page == NavPage.Destinations).Count is null);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(25, Ct);
        }

        Assert.True(condition());
    }
}
