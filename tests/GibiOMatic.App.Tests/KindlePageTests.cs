using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
using GibiOMatic.App.ViewModels;
using GibiOMatic.Core.Settings;
using GibiOMatic.Destinations.Kindle;
using GibiOMatic.Destinations.Sync;

namespace GibiOMatic.App.Tests;

public sealed class KindlePageTests : IDisposable
{
    private readonly AppTestContext _app = new();

    public void Dispose() => _app.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private LibrarySession Session => _app.Get<LibrarySession>();

    private DestinationService Service => Session.Destinations!;

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
        await Service.WaitUntilIdleAsync(Ct);
    }

    /// <summary>The wizard with the Kindle type: one page of settings, then its detail opens.</summary>
    private async Task<DestinationsPageViewModel> CreateKindleAsync()
    {
        var page = _app.Get<DestinationsPageViewModel>();
        await page.OnNavigatedToAsync(Ct);
        await page.AddDestinationCommand.ExecuteAsync(null);
        var wizard = page.Wizard!;
        wizard.Types.Single(t => t.Id == KindleDestinationType.TypeId).PickCommand.Execute(null);
        Assert.Equal(2, wizard.StepCount);

        wizard.NextCommand.Execute(null);
        Assert.True(wizard.IsKindleStep2);
        Assert.True(wizard.IsLastStep);
        Assert.Equal("Kindle Colorsoft", wizard.KindleForm.SelectedModel.Name);
        Assert.True(wizard.KindleForm.Color);
        Assert.True(wizard.KindleForm.HasKcc);
        await wizard.CreateCommand.ExecuteAsync(null);

        Assert.True(page.IsKindleDetail);
        return page;
    }

    /// <summary>Right-click on a book in the Library → "Add to the Kindle list".</summary>
    private async Task<LibraryPageViewModel> AddFromLibraryAsync(int index)
    {
        var library = _app.Get<LibraryPageViewModel>();
        await library.LoadAsync(Ct);
        library.SetViewModeCommand.Execute(LibraryViewMode.Books);
        var card = library.VisibleCards.Cast<BookCardViewModel>().ElementAt(index);
        Assert.True(card.HasMenu);
        Assert.Equal(Strings.Menu_AddKindle, card.KindleMenuText);
        await card.ToggleKindleCommand.ExecuteAsync(null);
        Assert.True(card.IsInKindleList);
        Assert.Equal(Strings.Menu_RemoveKindle, card.KindleMenuText);
        return library;
    }

    [Fact]
    public async Task A_kindle_is_created_in_the_wizard_and_prepares_the_books_chosen_in_its_list()
    {
        await _app.EnterUiThreadAsync();
        _app.InstallFakeKcc();
        await ImportSagaAsync();
        var page = await CreateKindleAsync();
        var config = Assert.Single(_app.Get<ISettingsService>().Current.Destinations);
        Assert.Equal((KindleDestinationType.TypeId, DestinationScope.ChosenBooks, "KCS"), (config.Type, config.Which, config.KindleProfile));
        Assert.StartsWith(_app.Paths.KindleDirectory, config.Folder);
        var detail = page.KindleDetail!;
        Assert.Equal(Strings.Kindle_EmptyList, detail.EmptyText);

        // A book chosen in the Library is prepared.
        await AddFromLibraryAsync(0);
        await WaitForAsync(async () =>
        {
            await Service.WaitUntilIdleAsync(Ct);
            await detail.RefreshAsync(Ct);
            return detail.ReadyCount == 1;
        });
        Assert.Equal(1, _app.Kcc.Runs);
        Assert.Equal(1, detail.ChosenCount);
        Assert.False(detail.CanSend);
        Assert.False(detail.IsConnected);

        // A black-and-white model gets grey pages.
        detail.Form.SelectedModel = KindleModels.Find("KPW6");
        Assert.False(detail.Form.Color);

        // Unticking it here takes it out of the list.
        await detail.RefreshAsync(Ct);
        Assert.Single(detail.Rows).IsChosen = false;
        await WaitForAsync(async () =>
        {
            await detail.RefreshAsync(Ct);
            return detail.ChosenCount == 0;
        });
        Assert.Empty(_app.Get<ISettingsService>().Current.Destinations.Single().ChosenBookIds);
    }

    [Fact]
    public async Task The_library_puts_a_book_in_the_list_and_the_banner_offers_to_send_it_when_the_kindle_is_connected()
    {
        await _app.EnterUiThreadAsync();
        _app.InstallFakeKcc();
        await ImportSagaAsync();
        await CreateKindleAsync();
        var main = _app.Get<MainWindowViewModel>();
        await main.InitializeAsync(Ct);
        var shell = Assert.IsType<ShellViewModel>(main.Content);
        await shell.CurrentNavigation;
        var library = (LibraryPageViewModel)shell.NavItems[0].Content;
        var book = library.Selected!;

        Assert.Equal(Strings.Menu_AddKindle, library.KindleButtonText);
        await library.ToggleKindleCommand.ExecuteAsync(null);
        Assert.Equal(Strings.Menu_RemoveKindle, library.KindleButtonText);
        Assert.True(book.Card.IsInKindleList);
        await Service.WaitUntilIdleAsync(Ct);
        Assert.False(shell.ShowKindleBanner);

        // Plugged in: the banner counts what is ready; nothing is copied before the confirmation.
        _app.Kindle.Connected = true;
        _app.Get<KindleWatcher>().Poll();
        await WaitForAsync(() => Task.FromResult(shell.ShowKindleBanner));
        Assert.Contains("1", shell.KindleBannerText);
        Assert.Empty(_app.Kindle.Files);

        await shell.SendToKindleCommand.ExecuteAsync(null);
        var destinations = (DestinationsPageViewModel)shell.NavItems[4].Content;
        var detail = destinations.KindleDetail!;
        Assert.True(detail.HasPlan);
        var row = Assert.Single(detail.PlanRows);
        Assert.Equal("Saga v01 (2012).mobi", row.DeviceFile);
        Assert.Empty(_app.Kindle.Files);

        await detail.ConfirmSendCommand.ExecuteAsync(null);
        Assert.Equal(book.Title, _app.Kindle.Files["Saga v01 (2012).mobi"]);
        Assert.False(detail.SendResultIsError);
        Assert.Equal(1, detail.SentCount);
        await WaitForAsync(async () =>
        {
            await shell.UpdateKindleBannerAsync();
            return !shell.ShowKindleBanner;
        });

        // Unplugged: nothing more to offer, and the next connection asks again.
        _app.Kindle.Connected = false;
        _app.Get<KindleWatcher>().Poll();
        await WaitForAsync(() => Task.FromResult(!detail.IsConnected));
    }

    [Fact]
    public async Task The_manager_lists_the_device_and_deletes_only_what_is_ticked_after_confirming()
    {
        await _app.EnterUiThreadAsync();
        _app.InstallFakeKcc();
        await ImportSagaAsync();
        var page = await CreateKindleAsync();
        var detail = page.KindleDetail!;
        await AddFromLibraryAsync(0);
        await WaitForAsync(async () =>
        {
            await Service.WaitUntilIdleAsync(Ct);
            await detail.RefreshAsync(Ct);
            return detail.ReadyCount == 1;
        });
        _app.Kindle.Files["Dicionário.azw3"] = "the user's own";
        _app.Kindle.Connected = true;
        _app.Get<KindleWatcher>().Poll();
        await detail.ShowSendPlanCommand.ExecuteAsync(null);
        await detail.ConfirmSendCommand.ExecuteAsync(null);

        await detail.SetTabCommand.ExecuteAsync(KindleTab.Device);

        Assert.Equal(["Dicionário.azw3", "Saga v01 (2012).mobi"], detail.DeviceFiles.Select(f => f.Name));
        Assert.Equal([false, true], detail.DeviceFiles.Select(f => f.SentByApp));

        // Nothing ticked: nothing to delete. Ticked: asks first.
        detail.AskDeleteCommand.Execute(null);
        Assert.False(detail.ConfirmingDelete);
        detail.DeviceFiles[1].IsChecked = true;
        detail.AskDeleteCommand.Execute(null);
        Assert.True(detail.ConfirmingDelete);
        Assert.Equal(2, _app.Kindle.Files.Count);
        await detail.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.Equal(["Dicionário.azw3"], _app.Kindle.Files.Keys);
        Assert.Equal(["Dicionário.azw3"], detail.DeviceFiles.Select(f => f.Name));
        Assert.Empty(_app.Get<ISettingsService>().Current.Destinations.Single().ChosenBookIds);
        await detail.LoadHistoryAsync();
        Assert.Contains(detail.History, h => h.Text == Strings.History_RemovedFromDevice);
    }

    [Fact]
    public async Task The_right_click_menu_marks_read_clears_progress_and_opens_the_editor()
    {
        await _app.EnterUiThreadAsync();
        await ImportSagaAsync();
        var library = _app.Get<LibraryPageViewModel>();
        await library.LoadAsync(Ct);
        library.SetViewModeCommand.Execute(LibraryViewMode.Books);
        var card = library.VisibleCards.Cast<BookCardViewModel>().First();
        NavigationRequest? request = null;
        library.NavigationRequested += (_, r) => request = r;

        Assert.Equal(Strings.Reading_MarkRead, card.ReadMenuText);
        await card.ToggleReadCommand.ExecuteAsync(null);
        Assert.True(card.IsRead);
        Assert.Equal(Strings.Reading_MarkUnread, card.ReadMenuText);
        await card.ToggleReadCommand.ExecuteAsync(null);
        Assert.False(card.IsRead);

        card.EditCommand.Execute(null);
        Assert.Equal((NavPage.Editor, card.Id), (request?.Page, request?.BookIds?.Single()));
        card.ReviewCommand.Execute(null);
        Assert.Equal(NavPage.Review, request?.Page);
    }

    [Fact]
    public async Task Without_a_kindle_destination_the_library_button_goes_to_destinos()
    {
        await _app.EnterUiThreadAsync();
        await ImportSagaAsync();
        var library = _app.Get<LibraryPageViewModel>();
        await library.LoadAsync(Ct);
        NavigationRequest? request = null;
        library.NavigationRequested += (_, r) => request = r;

        await library.ToggleKindleCommand.ExecuteAsync(null);

        Assert.Equal(NavPage.Destinations, request?.Page);
    }

    [Fact]
    public async Task Without_the_kcc_the_wizard_offers_to_get_it_and_the_kindle_says_what_is_missing()
    {
        await _app.EnterUiThreadAsync();
        await ImportSagaAsync();
        var page = _app.Get<DestinationsPageViewModel>();
        await page.OnNavigatedToAsync(Ct);
        await page.AddDestinationCommand.ExecuteAsync(null);
        var wizard = page.Wizard!;
        wizard.Types.Single(t => t.Id == KindleDestinationType.TypeId).PickCommand.Execute(null);
        wizard.NextCommand.Execute(null);

        Assert.False(wizard.KindleForm.HasKcc);
        Assert.Equal(Strings.Kindle_KccMissing, wizard.KindleForm.KccStatus);

        // Choosing the tool by hand.
        var tool = _app.Folder("kcc/kcc-c2e.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(tool)!);
        await File.WriteAllTextAsync(tool, "x", Ct);
        _app.FilePicker.NextExecutable = tool;
        await wizard.KindleForm.ChooseKccCommand.ExecuteAsync(null);
        Assert.True(wizard.KindleForm.HasKcc);
        await wizard.CreateCommand.ExecuteAsync(null);
        Assert.Equal(tool, _app.Get<ISettingsService>().Current.Destinations.Single().KccPath);

        var card = Assert.Single(page.Cards);
        Assert.True(card.IsKindle);
        Assert.Equal(Strings.Kindle_SendShort, card.ActionText);
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25, Ct);
        }

        Assert.True(await condition());
    }
}
