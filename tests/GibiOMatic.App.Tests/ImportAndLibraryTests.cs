using System.Globalization;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
using GibiOMatic.App.ViewModels;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Quality;
using GibiOMatic.Core.Settings;
using GibiOMatic.Library.Import;

namespace GibiOMatic.App.Tests;

public sealed class ImportAndLibraryTests : IDisposable
{
    private readonly AppTestContext _app = new();

    public void Dispose() => _app.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Downloads(string name) => _app.Folder(Path.Combine("Downloads", name));

    private LibrarySession Session => _app.Get<LibrarySession>();

    private async Task OpenLibraryAsync() => await Session.CreateAsync(_app.Folder("Arquivo"), Ct);

    private static string Info(string series, string number, string? writer = "Brian K. Vaughan", int? year = 2012) =>
        SyntheticBooks.ComicInfo(series: series, number: number, year: year, month: 10, writer: writer, penciller: "Fiona Staples",
            publisher: "Image Comics", format: "TPB");

    /// <summary>Adds files through the inbox page, waits for the analysis, imports and waits again.</summary>
    private async Task<InboxPageViewModel> ImportThroughInboxAsync(params string[] files)
    {
        var inbox = _app.Get<InboxPageViewModel>();
        _app.FilePicker.WillReturn(files);
        await inbox.AddFilesCommand.ExecuteAsync(null);
        await Session.Queue!.WaitUntilIdleAsync(Ct);
        await inbox.RefreshAsync(Ct);
        await inbox.ImportCommand.ExecuteAsync(null);
        await Session.Queue.WaitUntilIdleAsync(Ct);
        await inbox.RefreshAsync(Ct);
        return inbox;
    }

    [Fact]
    public async Task Files_go_through_the_inbox_and_appear_in_the_library_with_their_quality()
    {
        await OpenLibraryAsync();
        var inbox = _app.Get<InboxPageViewModel>();
        _app.FilePicker.WillReturn(
            SyntheticBooks.Cbz(Downloads("saga.cbz"), comicInfoXml: Info("Saga", "1")),
            SyntheticBooks.Pdf(Downloads("houseofslaughter_vol1.pdf")));

        await inbox.AddFilesCommand.ExecuteAsync(null);
        await Session.Queue!.WaitUntilIdleAsync(Ct);
        await inbox.RefreshAsync(Ct);

        Assert.Equal(2, inbox.Items.Count);
        Assert.All(inbox.Items, i => Assert.Equal(InboxItemState.Ready, i.Entry.Item.State));
        Assert.Equal("Saga, Vol. 1", inbox.Items[0].Title);
        Assert.True(inbox.CanImport);
        Assert.Contains("2", inbox.ConfirmSummary);

        await inbox.ImportCommand.ExecuteAsync(null);
        await Session.Queue.WaitUntilIdleAsync(Ct);
        await inbox.RefreshAsync(Ct);

        // Imported files leave the list; the summary still tells how it went.
        Assert.Empty(inbox.Items);
        Assert.True(inbox.IsEmpty);
        Assert.Contains("2", inbox.DoneSummary);
        Assert.False(inbox.CanImport);

        var library = _app.Get<LibraryPageViewModel>();
        await library.LoadAsync(Ct);

        Assert.Equal(2, library.BookCount);
        Assert.Equal(1, library.NeedsReviewCount);
        var details = library.Selected!;
        Assert.NotEmpty(details.Checks);
        Assert.Single(details.Files);
    }

    [Fact]
    public async Task Filters_search_and_series_view()
    {
        await OpenLibraryAsync();
        await ImportThroughInboxAsync(
            SyntheticBooks.Cbz(Downloads("saga1.cbz"), comicInfoXml: Info("Saga", "1")),
            SyntheticBooks.Cbz(Downloads("saga2.cbz"), comicInfoXml: Info("Saga", "2")),
            SyntheticBooks.Pdf(Downloads("plutona.pdf"), new SyntheticBooks.PdfOptions { Title = "Plutona" }));
        var page = _app.Get<LibraryPageViewModel>();
        await page.LoadAsync(Ct);

        // Séries view (default): the two Saga volumes become one card, Plutona stays on its own.
        // A series with one book is shown as that book.
        Assert.Equal(LibraryViewMode.Series, page.ViewMode);
        Assert.Equal(2, page.VisibleCards.Count);
        var saga = Assert.Single(page.VisibleCards.OfType<SeriesCardViewModel>());
        Assert.Equal(2, saga.Books.Count);
        // A stack: volume 1 in front, volume 2 behind.
        Assert.Equal("Saga, Vol. 1", saga.Cover.Title);
        Assert.Equal(["Saga, Vol. 2"], saga.BehindCovers.Select(b => b.Title));
        Assert.Null(saga.Behind2);
        // The CI runs in English: the expected text comes from the resources, not a fixed pt-BR string.
        Assert.StartsWith($"{Strings.Library_SeriesKind} · {string.Format(CultureInfo.CurrentCulture, Strings.Library_SeriesBooksMany, 2)}", saga.MetaLine, StringComparison.Ordinal);

        saga.OpenCommand.Execute(null);
        Assert.True(page.IsInSeries);
        Assert.Equal(["Saga, Vol. 1", "Saga, Vol. 2"], page.VisibleCards.OfType<BookCardViewModel>().Select(b => b.Title));
        page.BackToSeriesCommand.Execute(null);

        page.SetViewModeCommand.Execute(LibraryViewMode.Books);
        Assert.Equal(3, page.VisibleCards.Count);

        page.SearchText = "pluto";
        Assert.Equal("Plutona", Assert.Single(page.VisibleCards.OfType<BookCardViewModel>()).Title);
        page.SearchText = "vaughan";
        Assert.Equal(2, page.VisibleCards.Count);
        page.SearchText = null;

        Assert.Equal(1, page.Filters.Single(f => f.Filter == LibraryFilter.NoSeries).Count);
        page.Filters.Single(f => f.Filter == LibraryFilter.UnknownAuthor).PickCommand.Execute(null);
        Assert.Equal("Plutona", Assert.Single(page.VisibleCards.OfType<BookCardViewModel>()).Title);
        Assert.True(page.Filters.Single(f => f.Filter == LibraryFilter.UnknownAuthor).IsSelected);

        // A series of one book shows as the book, not as a stack.
        page.Filters[0].PickCommand.Execute(null);
        var plutona = page.VisibleCards.OfType<BookCardViewModel>().Single(b => b.Title == "Plutona");
        await _app.Get<GibiOMatic.Library.Editing.BookEditor>().UpdateAsync(Session.Current!, plutona.Id, m => m with { Series = new("Plutona", "1") }, Ct);
        await page.LoadAsync(Ct);
        page.SetViewModeCommand.Execute(LibraryViewMode.Series);
        Assert.Contains(page.VisibleCards, c => c is BookCardViewModel { Title: "Plutona" });
        Assert.Single(page.VisibleCards.OfType<SeriesCardViewModel>());
    }

    [Fact]
    public async Task The_universes_view_groups_series_of_the_same_universe()
    {
        await OpenLibraryAsync();
        static string Grouped(string series, string number, string? group) =>
            SyntheticBooks.ComicInfo(series: series, number: number, writer: "Kyle Higgins", publisher: "Image Comics", seriesGroup: group);
        await ImportThroughInboxAsync(
            SyntheticBooks.Cbz(Downloads("rb1.cbz"), pages: 3, comicInfoXml: Grouped("Radiant Black", "1", "Massive-Verse")),
            SyntheticBooks.Cbz(Downloads("rb2.cbz"), pages: 4, comicInfoXml: Grouped("Radiant Black", "2", "Massive-Verse")),
            SyntheticBooks.Cbz(Downloads("rr1.cbz"), pages: 5, comicInfoXml: Grouped("Radiant Red", "1", "Massive-Verse, Supermassive")),
            SyntheticBooks.Cbz(Downloads("saga1.cbz"), pages: 6, comicInfoXml: Grouped("Saga", "1", null)));
        var page = _app.Get<LibraryPageViewModel>();
        await page.LoadAsync(Ct);

        page.SetViewModeCommand.Execute(LibraryViewMode.Universes);
        Assert.False(page.ShowNoUniverses);
        var universes = page.VisibleCards.Cast<UniverseCardViewModel>().ToList();
        Assert.Equal(["Massive-Verse", "Supermassive"], universes.Select(u => u.Name));
        Assert.Equal(3, universes[0].Books.Count);
        Assert.Contains("2", universes[0].MetaLine);

        // Inside a universe: its series, then a series' books, and back the same way.
        universes[0].OpenCommand.Execute(null);
        Assert.True(page.IsDrilledIn);
        Assert.Equal("Massive-Verse", page.DrillTitle);
        Assert.Equal(Strings.Library_BackToUniverses, page.BackText);
        var radiantBlack = page.VisibleCards.OfType<SeriesCardViewModel>().Single();
        Assert.Equal("Radiant Red", page.VisibleCards.OfType<BookCardViewModel>().Single().Book.Metadata.Series!.Name);
        radiantBlack.OpenCommand.Execute(null);
        Assert.Equal("Radiant Black", page.DrillTitle);
        Assert.Equal("Massive-Verse", page.BackText);
        Assert.Equal(2, page.VisibleCards.Count);
        page.BackToSeriesCommand.Execute(null);
        Assert.Equal("Massive-Verse", page.DrillTitle);
        page.BackToSeriesCommand.Execute(null);
        Assert.False(page.IsDrilledIn);
        Assert.Equal(2, page.VisibleCards.Count);

        // Searching finds books by universe; the panel shows them.
        page.SetViewModeCommand.Execute(LibraryViewMode.Books);
        page.SearchText = "supermassive";
        var red = Assert.Single(page.VisibleCards.OfType<BookCardViewModel>());
        red.SelectCommand.Execute(null);
        Assert.Equal(["Massive-Verse", "Supermassive"], page.Selected!.Universes);
    }

    [Fact]
    public async Task The_universes_view_explains_how_to_make_one_when_there_is_none()
    {
        await OpenLibraryAsync();
        await ImportThroughInboxAsync(SyntheticBooks.Cbz(Downloads("saga1.cbz"), comicInfoXml: Info("Saga", "1")));
        var page = _app.Get<LibraryPageViewModel>();
        await page.LoadAsync(Ct);

        page.SetViewModeCommand.Execute(LibraryViewMode.Universes);

        Assert.Empty(page.VisibleCards);
        Assert.True(page.ShowNoUniverses);
    }

    [Fact]
    public async Task The_grid_fits_as_many_columns_as_the_width_allows()
    {
        await OpenLibraryAsync();
        await ImportThroughInboxAsync(Enumerable.Range(1, 7)
            .Select(i => SyntheticBooks.Cbz(Downloads($"b{i}.cbz"), comicInfoXml: Info($"Series {i}", "1"))).ToArray());
        var page = _app.Get<LibraryPageViewModel>();
        await page.LoadAsync(Ct);

        page.SetViewportWidth(3 * LibraryPageViewModel.MinCardWidth + (2 * LibraryPageViewModel.CardGap) + 10);

        Assert.Equal(3, page.Columns);
        Assert.Equal([3, 3, 1], page.Rows.Select(r => r.Cards.Count));
    }

    [Fact]
    public async Task Duplicates_offer_a_choice_and_the_choice_is_applied()
    {
        await OpenLibraryAsync();
        await ImportThroughInboxAsync(SyntheticBooks.Pdf(Downloads("saga.pdf"), new SyntheticBooks.PdfOptions { Title = "Saga, Vol. 1" }));
        var inbox = _app.Get<InboxPageViewModel>();
        await inbox.ClearFinishedCommand.ExecuteAsync(null);

        _app.FilePicker.WillReturn(SyntheticBooks.Cbz(Downloads("saga.cbz"), comicInfoXml: Info("Saga", "1")));
        await inbox.AddFilesCommand.ExecuteAsync(null);
        await Session.Queue!.WaitUntilIdleAsync(Ct);
        await inbox.RefreshAsync(Ct);

        var item = Assert.Single(inbox.Items);
        Assert.Equal(StatusKind.Duplicate, item.StatusKind);
        Assert.Equal([ImportDecision.Replace, ImportDecision.KeepBoth, ImportDecision.Skip], item.Decisions.Select(d => d.Decision));
        Assert.True(item.Decisions[0].IsSelected);

        item.Decisions[1].PickCommand.Execute(null);
        await Task.Delay(100, Ct);
        await inbox.RefreshAsync(Ct);
        Assert.True(item.Decisions[1].IsSelected);

        await inbox.ImportCommand.ExecuteAsync(null);
        await Session.Queue.WaitUntilIdleAsync(Ct);

        var book = Assert.Single(await Session.Index!.GetBooksAsync(Ct));
        Assert.Equal(2, book.Metadata.Files.Count);
        Assert.Equal(BookFormat.Cbz, book.MainFile!.Format);
    }

    [Fact]
    public async Task Pause_and_resume_from_the_inbox_page()
    {
        await OpenLibraryAsync();
        var inbox = _app.Get<InboxPageViewModel>();

        await inbox.TogglePauseCommand.ExecuteAsync(null);
        Assert.True(inbox.IsPaused);
        Assert.Equal(Resources.Strings.Inbox_Resume, inbox.PauseLabel);

        _app.FilePicker.WillReturn(SyntheticBooks.Pdf(Downloads("a.pdf")));
        await inbox.AddFilesCommand.ExecuteAsync(null);
        await Task.Delay(100, Ct);
        await inbox.RefreshAsync(Ct);
        Assert.Equal(InboxItemState.Pending, Assert.Single(inbox.Items).Entry.Item.State);
        Assert.NotNull(inbox.ProgressText);

        await inbox.TogglePauseCommand.ExecuteAsync(null);
        await Session.Queue!.WaitUntilIdleAsync(Ct);
        await inbox.RefreshAsync(Ct);
        Assert.Equal(InboxItemState.Ready, Assert.Single(inbox.Items).Entry.Item.State);
    }

    [Fact]
    public async Task The_shell_counts_files_waiting_in_the_inbox()
    {
        var main = _app.Get<MainWindowViewModel>();
        await _app.Get<ISettingsService>().LoadAsync(Ct);
        await main.InitializeAsync(Ct);
        _app.Picker.WillReturn(_app.Folder("Arquivo"));
        await ((WelcomeViewModel)main.Content!).CreateLibraryCommand.ExecuteAsync(null);
        var shell = Assert.IsType<ShellViewModel>(main.Content);
        await shell.CurrentNavigation;

        await Session.Queue!.AddAsync([SyntheticBooks.Pdf(Downloads("a.pdf")), SyntheticBooks.Pdf(Downloads("b.pdf"))], Ct);
        await Session.Queue.WaitUntilIdleAsync(Ct);
        await Task.Delay(600, Ct); // the counter is debounced

        var inboxItem = shell.NavItems.Single(i => i.Page == NavPage.Inbox);
        Assert.Equal("2", inboxItem.Count);
        Assert.True(inboxItem.HasBadge);
    }

    [Fact]
    public void Quality_texts_exist_for_every_issue()
    {
        foreach (var issue in Enum.GetValues<QualityIssue>())
        {
            var text = QualityText.Describe(new QualityProblem(QualityField.Title, 1, issue, "x|y", 2));
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.NotEqual(issue.ToString(), text);
        }
    }
}
