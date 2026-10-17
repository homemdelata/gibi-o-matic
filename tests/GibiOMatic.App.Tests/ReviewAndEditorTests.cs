using GibiOMatic.App.Services;
using GibiOMatic.App.ViewModels;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Secrets;
using GibiOMatic.Core.Settings;
using GibiOMatic.Library.Index;
using GibiOMatic.Metadata;
using GibiOMatic.Metadata.Identification;

namespace GibiOMatic.App.Tests;

public sealed class ReviewAndEditorTests : IDisposable
{
    private readonly AppTestContext _app = new();

    public void Dispose() => _app.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private LibrarySession Session => _app.Get<LibrarySession>();

    private string Downloads(string name) => _app.Folder(Path.Combine("Downloads", name));

    /// <summary>Opens a library and imports synthetic PDFs; identification runs with the fake identifier.</summary>
    private async Task<IReadOnlyList<CatalogBook>> ImportAsync(params string[] fileNames)
    {
        await Session.CreateAsync(_app.Folder("Arquivo"), Ct);
        // Different page counts, so no two files are the same file.
        await Session.Queue!.AddAsync(fileNames.Select((n, i) => SyntheticBooks.Pdf(Downloads(n), new SyntheticBooks.PdfOptions { Pages = i + 1 })), Ct);
        await Session.Queue.WaitUntilIdleAsync(Ct);
        await Session.Queue.ConfirmAsync(Ct);
        await Session.Queue.WaitUntilIdleAsync(Ct);

        // Imported books enter the identification queue through an event; wait until every one was handled.
        var books = await Session.Index!.GetBooksAsync(Ct);
        for (var waited = 0; waited < 10_000; waited += 25)
        {
            var counts = await Session.Identification!.GetCountsAsync(Ct);
            if (counts.Queued == 0 && counts.ToReview + counts.AutoApproved + counts.Approved >= books.Count)
            {
                break;
            }

            await Task.Delay(25, Ct);
        }

        return await Session.Index!.GetBooksAsync(Ct);
    }

    private async Task<BookMetadata> BookAsync(string id) => (await Session.Index!.GetBookAsync(id, Ct))!.Metadata;

    [Fact]
    public async Task The_review_shows_candidates_and_approves_the_checked_fields()
    {
        _app.Identifier.Respond = (_, _) => [FakeIdentifier.Candidate("Saga, Vol. 1", "Saga", "1", 0.72, MatchReason.Title, MatchReason.Number)];
        var book = (await ImportAsync("saga_vol1.pdf")).Single();
        var review = _app.Get<ReviewPageViewModel>();

        await review.OnNavigatedToAsync(Ct);

        var item = Assert.Single(review.Items);
        Assert.Equal(book.Metadata.Id, review.Selected?.BookId);
        var candidate = Assert.Single(review.Candidates);
        Assert.True(candidate.IsSelected);
        Assert.Contains("72", review.ConfidenceText);
        var publisher = review.Rows.Single(r => r.Change.Field == ProposalField.Publisher);
        publisher.IsApplied = false;
        Assert.Contains($"{review.Rows.Count(r => r.CanApply) - 1}", review.AppliedSummary);

        await review.ApproveCommand.ExecuteAsync(null);

        var saved = await BookAsync(book.Metadata.Id);
        Assert.Equal("Saga, Vol. 1", saved.Title);
        Assert.Null(saved.Publisher);
        Assert.Equal(["Brian K. Vaughan", "Fiona Staples"], saved.Credits.Select(c => c.Name));
        Assert.Equal(ReviewStatus.Approved, saved.Review.Status);
        Assert.Empty(review.Items);
        Assert.True(review.IsEmpty);
    }

    [Fact]
    public async Task Skip_moves_on_and_search_again_uses_the_typed_words()
    {
        _app.Identifier.Respond = (_, text) => text is "Saga"
            ? [FakeIdentifier.Candidate("Saga, Vol. 1", "Saga", "1", 0.6)]
            : [];
        await ImportAsync("a.pdf", "b.pdf");
        var review = _app.Get<ReviewPageViewModel>();
        await review.OnNavigatedToAsync(Ct);
        Assert.Equal(2, review.Items.Count);
        Assert.Empty(review.Candidates);
        var first = review.Selected!.BookId;

        await review.SkipCommand.ExecuteAsync(null);
        Assert.NotEqual(first, review.Selected!.BookId);
        Assert.Equal(first, review.Items[^1].BookId);

        review.SearchText = "Saga";
        await review.SearchAgainCommand.ExecuteAsync(null);
        Assert.Single(review.Candidates);
    }

    [Fact]
    public async Task Searching_again_shows_that_it_is_searching_and_then_what_was_not_found()
    {
        _app.Identifier.Respond = (_, text) => text is null ? [FakeIdentifier.Candidate("Saga, Vol. 1", "Saga", "1", 0.6)] : [];
        await ImportAsync("a.pdf");
        var review = _app.Get<ReviewPageViewModel>();
        await review.OnNavigatedToAsync(Ct);
        Assert.True(review.ShowCandidates);

        _app.Identifier.Gate = new TaskCompletionSource();
        review.SearchText = "Bee and PuppyCat";
        var search = review.SearchAgainCommand.ExecuteAsync(null);

        Assert.True(review.IsBusy);
        Assert.Contains("Bee and PuppyCat", review.BusyText);
        Assert.Empty(review.Candidates);
        Assert.False(review.ShowCandidates);
        Assert.False(review.ShowNoCandidateHint);
        Assert.False(review.SearchAgainCommand.CanExecute(null));

        _app.Identifier.Gate.SetResult();
        await search;

        Assert.False(review.IsBusy);
        Assert.True(review.ShowNoCandidateHint);
        Assert.Contains("Bee and PuppyCat", review.NoCandidateText);
    }

    [Fact]
    public async Task The_right_comicvine_page_can_be_pasted_and_the_file_pages_are_shown()
    {
        await ImportAsync("houseofslaughter_vol1.pdf");
        var review = _app.Get<ReviewPageViewModel>();
        await review.OnNavigatedToAsync(Ct);
        await Task.Delay(200, Ct);
        Assert.Equal("Arquivo: 1 páginas · PDF", review.FileInfoText);

        review.SearchText = "House of Slaughter";
        await review.OpenComicVineCommand.ExecuteAsync(null);
        Assert.Contains("q=House%20of%20Slaughter", Assert.Single(_app.Urls.Opened).AbsoluteUri);

        review.ComicVineLink = "https://example.com/wrong";
        await review.UseComicVineLinkCommand.ExecuteAsync(null);
        Assert.NotNull(review.LinkError);

        review.ComicVineLink = "https://comicvine.gamespot.com/house-of-slaughter-1/4000-123/";
        await review.UseComicVineLinkCommand.ExecuteAsync(null);

        Assert.Null(review.LinkError);
        var candidate = Assert.Single(review.Candidates);
        Assert.Equal("House of Slaughter, Vol. 1", candidate.Title);
        Assert.True(review.ConfidenceText?.Contains("100") == true, $"Confidence text: '{review.ConfidenceText}'");
    }

    [Fact]
    public async Task Safe_matches_are_approved_automatically_and_listed_by_the_library_filter()
    {
        _app.Identifier.Respond = (_, _) => [FakeIdentifier.Candidate("Saga, Vol. 1", "Saga", "1", 0.98, MatchReason.Isbn, MatchReason.SecondSource)];
        var book = (await ImportAsync("saga_vol1.pdf")).Single();

        var saved = await BookAsync(book.Metadata.Id);
        Assert.Equal((ReviewStatus.Approved, MatchedBy.Auto), (saved.Review.Status, saved.Review.MatchedBy));

        var library = _app.Get<LibraryPageViewModel>();
        await library.LoadAsync(Ct);
        Assert.Equal(1, library.Filters.Single(f => f.Filter == LibraryFilter.AutoApproved).Count);
    }

    [Fact]
    public async Task The_editor_saves_one_book_renames_its_folder_and_can_undo()
    {
        var book = (await ImportAsync("saga_vol1.pdf")).Single();
        var editor = _app.Get<EditorPageViewModel>();
        editor.SetBooks([book.Metadata.Id]);
        await editor.OnNavigatedToAsync(Ct);
        Assert.True(editor.IsSingle);
        Assert.Equal("saga vol1", editor.BookTitle);

        editor.BookTitle = "Saga, Vol. 1";
        editor.SeriesName = "Saga";
        editor.Number = "1";
        editor.Publisher = "Image Comics";
        editor.DateText = "2012-13";
        editor.AddCreditCommand.Execute(null);
        editor.Credits[^1].Name = "Brian K. Vaughan";
        Assert.Equal("Saga/Saga v01.cbz", editor.KomgaPreview);

        await editor.SaveCommand.ExecuteAsync(null);
        Assert.True(editor.MessageIsError, "invalid month");

        editor.DateText = "2012-10";
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.False(editor.MessageIsError);
        var saved = (await Session.Index!.GetBookAsync(book.Metadata.Id, Ct))!;
        Assert.Equal(("Saga, Vol. 1", "Image Comics"), (saved.Metadata.Title, saved.Metadata.Publisher));
        Assert.Equal(new PartialDate(2012, 10), saved.Metadata.Date);
        Assert.Equal((ReviewStatus.Approved, MatchedBy.Manual), (saved.Metadata.Review.Status, saved.Metadata.Review.MatchedBy));
        Assert.StartsWith("Livros/Image Comics/Saga/Saga, Vol. 1 [", saved.Folder);
        Assert.Equal("Saga/Saga v01 (2012).cbz", editor.KomgaPreview);

        var history = Assert.Single(editor.History);
        await history.RestoreCommand.ExecuteAsync(null);
        Assert.Equal("saga vol1", (await BookAsync(book.Metadata.Id)).Title);
        Assert.Equal("saga vol1", editor.BookTitle);
    }

    [Fact]
    public async Task The_batch_editor_changes_only_filled_fields_and_numbers_in_sequence()
    {
        var books = await ImportAsync("deadly_a.pdf", "deadly_b.pdf", "deadly_c.pdf");
        var editor = _app.Get<EditorPageViewModel>();
        editor.SetBooks(books.Select(b => b.Metadata.Id).ToList());
        await editor.OnNavigatedToAsync(Ct);
        Assert.True(editor.IsBatch);

        editor.SeriesName = "Deadly Class";
        editor.Number = "1";
        editor.NumberInSequence = true;
        editor.Publisher = "Image Comics";
        await editor.SaveCommand.ExecuteAsync(null);

        var saved = (await Session.Index!.GetBooksAsync(Ct)).Select(b => b.Metadata).OrderBy(m => m.Series!.Number).ToList();
        Assert.Equal(["1", "2", "3"], saved.Select(m => m.Series!.Number));
        Assert.All(saved, m => Assert.Equal(("Deadly Class", "Image Comics"), (m.Series!.Name, m.Publisher)));
        Assert.Equal(["deadly a", "deadly b", "deadly c"], saved.Select(m => m.Title));
        Assert.All(saved, m => Assert.Equal(ReviewStatus.Pending, m.Review.Status));
    }

    [Fact]
    public async Task Library_selection_opens_the_batch_editor_through_the_shell()
    {
        await ImportAsync("a.pdf", "b.pdf");
        var main = _app.Get<MainWindowViewModel>();
        await main.InitializeAsync(Ct);
        var shell = Assert.IsType<ShellViewModel>(main.Content);
        await shell.CurrentNavigation;
        var library = (LibraryPageViewModel)shell.NavItems[0].Content;

        library.ToggleSelectingCommand.Execute(null);
        library.CheckAllVisibleCommand.Execute(null);
        Assert.Equal(2, library.CheckedCount);
        library.EditCheckedCommand.Execute(null);
        await shell.CurrentNavigation;

        var editor = Assert.IsType<EditorPageViewModel>(shell.CurrentPage);
        Assert.True(editor.IsBatch);
        Assert.Equal("2", shell.NavItems.Single(i => i.Page == NavPage.Review).Count);
    }

    [Fact]
    public async Task Fetch_metadata_asks_before_queueing_the_books()
    {
        await ImportAsync("a.pdf");
        var library = _app.Get<LibraryPageViewModel>();
        await library.LoadAsync(Ct);
        var calls = 0;
        _app.Identifier.Respond = (_, _) =>
        {
            calls++;
            return [];
        };

        library.FetchMetadataCommand.Execute(null);
        Assert.NotNull(library.FetchConfirmation);
        await Task.Delay(100, Ct);
        Assert.Equal(0, calls);

        await library.ConfirmFetchCommand.ExecuteAsync(null);
        await Session.Identification!.WaitUntilIdleAsync(Ct);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Metadata_settings_are_saved_and_the_key_is_kept_protected()
    {
        await Session.CreateAsync(_app.Folder("Arquivo"), Ct);
        var page = _app.Get<SettingsPageViewModel>();
        await page.OnNavigatedToAsync(Ct);
        Assert.Equal(["openlibrary", "comicvine", "googlebooks"], page.Sources.Select(s => s.Id));

        page.MoveSourceUpCommand.Execute(page.Sources[1]);
        page.Sources.Single(s => s.Id == "googlebooks").Enabled = true;
        page.AlwaysReview = true;
        await page.SaveCommand.ExecuteAsync(null);

        var saved = _app.Get<ISettingsService>().Current.Metadata;
        Assert.Equal([new SourceSetting("comicvine", true), new SourceSetting("openlibrary", true), new SourceSetting("googlebooks", true)], saved.Sources);
        Assert.True(saved.AlwaysReview);

        if (!_app.Get<ISecretStore>().IsSupported)
        {
            return;
        }

        page.ComicVineKeyInput = "a-test-key";
        // The test key is not real: storing works, the online test reports a rejection or no answer.
        await page.SaveKeyCommand.ExecuteAsync(null);
        Assert.Null(page.ComicVineKeyInput);
        Assert.Equal("a-test-key", await _app.Get<ISecretStore>().GetAsync(SecretNames.ComicVineApiKey, Ct));
        Assert.DoesNotContain("a-test-key", await File.ReadAllTextAsync(_app.Paths.SettingsFile, Ct));

        await page.RemoveKeyCommand.ExecuteAsync(null);
        Assert.Null(await _app.Get<ISecretStore>().GetAsync(SecretNames.ComicVineApiKey, Ct));
        Assert.Equal(Resources.Strings.Key_Missing, page.KeyStatus);
    }
}
