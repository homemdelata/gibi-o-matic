using GibiOMatic.Conversion;
using GibiOMatic.Core;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Core.Settings;
using GibiOMatic.Library.Editing;
using GibiOMatic.Library.Identification;
using GibiOMatic.Library.Import;
using GibiOMatic.Library.Index;
using GibiOMatic.Metadata;
using GibiOMatic.Metadata.Http;
using GibiOMatic.Metadata.Identification;
using Microsoft.Extensions.Logging.Abstractions;

namespace GibiOMatic.Library.Tests;

/// <summary>Answers with a scripted result for each book title.</summary>
internal sealed class FakeIdentifier : IBookIdentifier
{
    public Func<BookMetadata, string?, IdentificationResult> Respond { get; set; } =
        (book, _) => new IdentificationResult(BookQuery.From(book), [], []);

    public List<string> Asked { get; } = [];

    public Task<IdentificationResult> IdentifyAsync(BookMetadata book, MetadataSettings settings, string? searchText, CancellationToken ct)
    {
        Asked.Add(searchText ?? book.Title);
        return Task.FromResult(Respond(book, searchText));
    }

    public Task<MetadataCandidate> CompleteAsync(MetadataCandidate candidate, CancellationToken ct) =>
        Task.FromResult(candidate with { Record = candidate.Record with { IsDetailed = true, Summary = "detailed" } });

    public Task<IReadOnlyList<MetadataCandidate>> FromComicVineLinkAsync(BookMetadata book, string link, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MetadataCandidate>>(link.Contains("4000-") ? [Saga(1.0, MatchReason.ChosenByUser)] : []);

    public static MetadataCandidate Saga(double confidence, params MatchReason[] reasons) => new(new MetadataRecord
    {
        Source = SourceIds.ComicVine,
        Title = "Saga, Vol. 1",
        SeriesName = "Saga",
        Number = "1",
        Kind = BookKind.Tpb,
        Date = new PartialDate(2012, 10, 10),
        Publisher = "Image Comics",
        Credits = [new Credit(CreditRole.Writer, "Brian K. Vaughan"), new Credit(CreditRole.Artist, "Fiona Staples")],
        ComicVineId = "4000-1",
        Isbn = "9781607066019",
        CoverFullUrl = "https://comicvine.gamespot.com/a/uploads/original/saga.jpg",
    }, confidence, reasons);
}

public sealed class EditingAndIdentificationTests : IAsyncLifetime
{
    private readonly TempDirectory _temp = new();
    private readonly AppPaths _paths;
    private readonly LibraryService _libraries;
    private readonly JsonSettingsService _settings;
    private readonly BookEditor _editor;
    private readonly FakeIdentifier _identifier = new();
    private readonly CoverImageCache _covers;
    private readonly ImportQueueFactory _imports;
    private LibraryInfo _library = null!;
    private IdentificationQueue _queue = null!;

    public EditingAndIdentificationTests()
    {
        _paths = new AppPaths(_temp.Combine("config"), _temp.Combine("data"));
        _libraries = new LibraryService(_paths, NullLogger<LibraryService>.Instance);
        _settings = new JsonSettingsService(_paths, NullLogger<JsonSettingsService>.Instance);
        var reader = new BookFileReader(NullLogger<BookFileReader>.Instance);
        var thumbnails = new ThumbnailCache(_paths, NullLogger<ThumbnailCache>.Instance);
        _editor = new BookEditor(_libraries, reader, NullLogger<BookEditor>.Instance);
        _covers = new CoverImageCache(new HttpClient(new LimeCovers()), _temp.Combine("covers"), NullLogger<CoverImageCache>.Instance);
        _imports = new ImportQueueFactory(_libraries, new ImportAnalyzer(reader, thumbnails),
            new BookImporter(reader, NullLogger<BookImporter>.Instance), thumbnails, _settings, new FakeRecycleBin(_temp.Combine("Lixeira")), NullLogger<ImportQueue>.Instance);
    }

    public async ValueTask InitializeAsync() => _library = await _libraries.CreateAsync(_temp.Combine("Library"), Ct);

    public async ValueTask DisposeAsync()
    {
        if (_queue is not null)
        {
            await _queue.DisposeAsync();
        }

        _temp.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private LibraryIndex Index => _libraries.GetIndex(_library);

    /// <summary>Imports a synthetic PDF called <paramref name="fileName"/> and returns its book.</summary>
    /// <param name="pages">Another page count makes another file (the same file twice is a duplicate).</param>
    private async Task<CatalogBook> ImportAsync(string fileName, int pages = 2)
    {
        await using var imports = _imports.Create(_library);
        await imports.StartAsync(Ct);
        await imports.AddAsync([SyntheticBooks.Pdf(_temp.Combine("Downloads", fileName), new SyntheticBooks.PdfOptions { Pages = pages })], Ct);
        await imports.WaitUntilIdleAsync(Ct);
        await imports.ConfirmAsync(Ct);
        await imports.WaitUntilIdleAsync(Ct);
        return (await Index.GetBooksAsync(Ct)).Single(b => b.Metadata.Files[0].DownloadedAs == fileName);
    }

    private async Task<IdentificationQueue> StartQueueAsync()
    {
        _queue = new IdentificationQueueFactory(_libraries, _identifier, _editor, _covers, _settings, NullLogger<IdentificationQueue>.Instance).Create(_library);
        await _queue.StartAsync(Ct);
        await _queue.WaitUntilIdleAsync(Ct);
        return _queue;
    }

    [Fact]
    public async Task Saving_keeps_history_renames_folder_and_file_and_can_be_undone()
    {
        var book = await ImportAsync("saga_vol1.pdf");
        Assert.StartsWith("Books/_To review/saga vol1 [", book.Folder);

        // Not approved yet: the file follows the title, but the book waits in "_To review".
        var edited = await _editor.UpdateAsync(_library, book.Metadata.Id, m => m with
        {
            Title = "Saga, Vol. 1",
            Publisher = "Image Comics",
            Series = new SeriesInfo("Saga", "1"),
        }, Ct);
        Assert.StartsWith("Books/_To review/Saga, Vol. 1 [", edited.Folder);
        Assert.Equal("Saga, Vol. 1.pdf", edited.Metadata.Files[0].Name);

        var saved = await _editor.UpdateAsync(_library, book.Metadata.Id,
            m => m with { Review = new ReviewInfo { Status = ReviewStatus.Approved, MatchedBy = MatchedBy.Manual } }, Ct);

        Assert.StartsWith("Books/Image Comics/Saga/Saga, Vol. 1 [", saved.Folder);
        Assert.True(File.Exists(Path.Combine(_library.RootPath, saved.Folder, "Saga, Vol. 1.pdf")));
        Assert.False(Directory.Exists(Path.Combine(_library.RootPath, "Books", LibraryLayout.ToReviewFolderName)));
        Assert.Equal(saved.Folder, (await Index.GetBookAsync(book.Metadata.Id, Ct))!.Folder);

        var history = await _editor.GetHistoryAsync(_library, book.Metadata.Id, Ct);
        Assert.Equal(2, history.Count);
        Assert.Equal("saga vol1", history[^1].Metadata.Title);

        var restored = await _editor.RestoreAsync(_library, book.Metadata.Id, history[^1], Ct);

        Assert.Equal("saga vol1", restored.Metadata.Title);
        Assert.Null(restored.Metadata.Publisher);
        Assert.StartsWith("Books/_To review/saga vol1 [", restored.Folder);
        Assert.Equal("saga vol1.pdf", restored.Metadata.Files[0].Name);
        Assert.Equal(3, (await _editor.GetHistoryAsync(_library, book.Metadata.Id, Ct)).Count);

        // Approved without a publisher: "_No publisher".
        var noPublisher = await _editor.UpdateAsync(_library, book.Metadata.Id,
            m => m with { Review = new ReviewInfo { Status = ReviewStatus.Approved, MatchedBy = MatchedBy.Manual } }, Ct);
        Assert.StartsWith("Books/_No publisher/saga vol1 [", noPublisher.Folder);
    }

    [Fact]
    public async Task Saving_without_changes_writes_nothing()
    {
        var book = await ImportAsync("a.pdf");

        await _editor.UpdateAsync(_library, book.Metadata.Id, m => m, Ct);

        Assert.Empty(await _editor.GetHistoryAsync(_library, book.Metadata.Id, Ct));
    }

    [Fact]
    public async Task The_cover_can_be_replaced_and_taken_again_from_page_one()
    {
        var book = await ImportAsync("a.pdf");
        var cover = Path.Combine(_library.RootPath, book.Folder, LibraryLayout.CoverFileName);
        var image = _temp.Combine("cover.png");
        await File.WriteAllBytesAsync(image, SyntheticImages.Png(300, 450, SkiaSharp.SKColors.Lime), Ct);

        Assert.True(await _editor.SetCoverAsync(_library, book.Metadata.Id, image, Ct));
        Assert.True(SyntheticImages.Near(SyntheticImages.Inspect(await File.ReadAllBytesAsync(cover, Ct)).Center, SkiaSharp.SKColors.Lime));

        Assert.True(await _editor.UseFirstPageAsync(_library, book.Metadata.Id, Ct));
        Assert.True(SyntheticImages.Near(SyntheticImages.Inspect(await File.ReadAllBytesAsync(cover, Ct)).Center, SyntheticBooks.CoverColor));
    }

    [Fact]
    public async Task Imported_books_are_identified_and_safe_matches_approved_automatically()
    {
        var book = await ImportAsync("saga_vol1.pdf");
        _identifier.Respond = (b, _) => new IdentificationResult(BookQuery.From(b),
            [FakeIdentifier.Saga(0.98, MatchReason.Isbn, MatchReason.SecondSource, MatchReason.Title, MatchReason.Number)], []);

        var queue = await StartQueueAsync();

        var saved = (await Index.GetBookAsync(book.Metadata.Id, Ct))!.Metadata;
        Assert.Equal("Saga, Vol. 1", saved.Title);
        Assert.Equal(new SeriesInfo("Saga", "1"), saved.Series);
        Assert.Equal(("4000-1", "9781607066019"), (saved.Identifiers.ComicVine, saved.Identifiers.Isbn));
        Assert.Equal((ReviewStatus.Approved, MatchedBy.Auto), (saved.Review.Status, saved.Review.MatchedBy));
        Assert.Equal(IdentificationState.AutoApproved, (await queue.GetAsync(book.Metadata.Id, Ct))!.State);
        Assert.Equal(new IdentificationCounts(0, 0, 1, 0), await queue.GetCountsAsync(Ct));
    }

    [Fact]
    public async Task Uncertain_matches_wait_in_the_review_and_are_approved_with_the_chosen_fields()
    {
        var book = await ImportAsync("saga_vol1.pdf");
        _identifier.Respond = (b, _) => new IdentificationResult(BookQuery.From(b), [FakeIdentifier.Saga(0.7, MatchReason.Title)], []);
        var queue = await StartQueueAsync();

        var item = Assert.Single(await queue.GetReviewItemsAsync(Ct));
        Assert.Equal(IdentificationState.NeedsReview, item.State);
        Assert.Equal(ReviewStatus.Pending, (await Index.GetBookAsync(book.Metadata.Id, Ct))!.Metadata.Review.Status);

        var candidate = await queue.CompleteAsync(item.Result!.Best!, Ct);
        await queue.ApproveAsync(book.Metadata.Id, candidate, new HashSet<ProposalField> { ProposalField.Title, ProposalField.Writers }, Ct);

        var saved = (await Index.GetBookAsync(book.Metadata.Id, Ct))!.Metadata;
        Assert.Equal("Saga, Vol. 1", saved.Title);
        Assert.Null(saved.Publisher);
        Assert.Equal("Brian K. Vaughan", Assert.Single(saved.Credits).Name);
        Assert.Equal((ReviewStatus.Approved, MatchedBy.ComicVineVolume), (saved.Review.Status, saved.Review.MatchedBy));
        Assert.Empty(await queue.GetReviewItemsAsync(Ct));
    }

    [Fact]
    public async Task The_source_cover_replaces_the_file_cover_only_when_chosen()
    {
        var book = await ImportAsync("saga_vol1.pdf");
        var cover = Path.Combine(_library.RootPath, book.Folder, LibraryLayout.CoverFileName);
        _identifier.Respond = (b, _) => new IdentificationResult(BookQuery.From(b), [FakeIdentifier.Saga(0.7)], []);
        var queue = await StartQueueAsync();
        var candidate = (await queue.GetReviewItemsAsync(Ct)).Single().Result!.Best!;

        var rows = Proposal.Compare(book.Metadata, candidate.Record);
        Assert.Contains(rows, r => r.Field == ProposalField.Cover);
        Assert.DoesNotContain(ProposalField.Cover, Proposal.DefaultSelection(rows));

        await queue.ApproveAsync(book.Metadata.Id, candidate, new HashSet<ProposalField> { ProposalField.Title, ProposalField.Cover }, Ct);

        var folder = (await Index.GetBookAsync(book.Metadata.Id, Ct))!.Folder;
        cover = Path.Combine(_library.RootPath, folder, LibraryLayout.CoverFileName);
        Assert.True(SyntheticImages.Near(SyntheticImages.Inspect(await File.ReadAllBytesAsync(cover, Ct)).Center, SkiaSharp.SKColors.Lime));
    }

    [Fact]
    public async Task Automatic_approval_keeps_the_file_cover()
    {
        var book = await ImportAsync("saga_vol1.pdf");
        _identifier.Respond = (b, _) => new IdentificationResult(BookQuery.From(b), [FakeIdentifier.Saga(0.98, MatchReason.Isbn, MatchReason.SecondSource)], []);

        await StartQueueAsync();

        var saved = (await Index.GetBookAsync(book.Metadata.Id, Ct))!;
        Assert.Equal(MatchedBy.Auto, saved.Metadata.Review.MatchedBy);
        var cover = Path.Combine(_library.RootPath, saved.Folder, LibraryLayout.CoverFileName);
        Assert.True(SyntheticImages.Near(SyntheticImages.Inspect(await File.ReadAllBytesAsync(cover, Ct)).Center, SyntheticBooks.CoverColor));
    }

    [Fact]
    public async Task A_comicvine_link_becomes_the_candidate_and_the_file_pages_are_known()
    {
        var book = await ImportAsync("houseofslaughter_vol1.pdf");
        Assert.Equal(2, book.MainFile!.Pages);
        var queue = await StartQueueAsync();

        Assert.Equal(0, await queue.UseComicVineLinkAsync(book.Metadata.Id, "https://comicvine.gamespot.com/x/4050-1/", Ct));
        Assert.Equal(1, await queue.UseComicVineLinkAsync(book.Metadata.Id, "https://comicvine.gamespot.com/x/4000-1/", Ct));

        var item = (await queue.GetAsync(book.Metadata.Id, Ct))!;
        Assert.Equal(IdentificationState.NeedsReview, item.State);
        Assert.Equal(MatchReason.ChosenByUser, Assert.Single(item.Result!.Candidates).Reasons.Single());
    }

    [Fact]
    public async Task No_match_can_be_searched_again_with_other_words_and_skipped()
    {
        var book = await ImportAsync("houseofslaughter_vol1.pdf");
        _identifier.Respond = (b, text) => text is null
            ? new IdentificationResult(BookQuery.From(b), [], ["comicvine:no-key"])
            : new IdentificationResult(BookQuery.From(b, text), [FakeIdentifier.Saga(0.6)], []);
        var queue = await StartQueueAsync();

        var item = Assert.Single(await queue.GetReviewItemsAsync(Ct));
        Assert.Equal((IdentificationState.NoMatch, "comicvine:no-key"), (item.State, item.Message));

        var result = await queue.SearchAgainAsync(book.Metadata.Id, "House of Slaughter", Ct);
        Assert.Single(result.Candidates);
        Assert.Equal(IdentificationState.NeedsReview, (await queue.GetAsync(book.Metadata.Id, Ct))!.State);

        await queue.SkipAsync(book.Metadata.Id, Ct);
        Assert.True((await queue.GetAsync(book.Metadata.Id, Ct))!.Skipped);
    }

    [Fact]
    public async Task Approved_books_are_only_identified_again_on_request_and_never_changed_automatically()
    {
        var book = await ImportAsync("saga_vol1.pdf");
        _identifier.Respond = (b, _) => new IdentificationResult(BookQuery.From(b),
            [FakeIdentifier.Saga(0.99, MatchReason.Isbn, MatchReason.SecondSource)], []);
        var queue = await StartQueueAsync();
        Assert.Single(_identifier.Asked);

        // Not queued again on its own…
        Assert.Equal(0, await queue.EnqueueAsync([book.Metadata.Id], force: false, Ct));

        // …but "Fetch metadata" does, and the result goes to the Review instead of being applied.
        await _editor.UpdateAsync(_library, book.Metadata.Id, m => m with { Title = "Saga (my edition)" }, Ct);
        Assert.Equal(1, await queue.EnqueueAsync([book.Metadata.Id], force: true, Ct));
        await queue.WaitUntilIdleAsync(Ct);

        Assert.Equal(2, _identifier.Asked.Count);
        Assert.Equal("Saga (my edition)", (await Index.GetBookAsync(book.Metadata.Id, Ct))!.Metadata.Title);
        Assert.Equal(IdentificationState.NeedsReview, (await queue.GetAsync(book.Metadata.Id, Ct))!.State);
    }

    [Fact]
    public async Task Pause_stops_identification_and_is_remembered()
    {
        await ImportAsync("a.pdf");
        _queue = new IdentificationQueueFactory(_libraries, _identifier, _editor, _covers, _settings, NullLogger<IdentificationQueue>.Instance).Create(_library);
        await _queue.PauseAsync(Ct);
        await _queue.StartAsync(Ct);
        await Task.Delay(100, Ct);

        Assert.Empty(_identifier.Asked);
        Assert.Equal(1, (await _queue.GetCountsAsync(Ct)).Queued);

        await _queue.ResumeAsync(Ct);
        await _queue.WaitUntilIdleAsync(Ct);
        Assert.Single(_identifier.Asked);
    }

    [Fact]
    public async Task Removing_a_book_moves_its_folder_to_the_trash_and_forgets_it()
    {
        var book = await ImportAsync("wrong_file.pdf");
        var keep = await ImportAsync("saga_vol1.pdf", pages: 3);
        await _editor.UpdateAsync(_library, book.Metadata.Id, m => m with { Title = "Wrong file" }, Ct);
        var queue = await StartQueueAsync();
        string? removed = null;
        _editor.BookRemoved += (_, e) => removed = e.BookId;
        var folder = Path.Combine(_library.RootPath, (await Index.GetBookAsync(book.Metadata.Id, Ct))!.Folder);

        await _editor.RemoveAsync(_library, book.Metadata.Id, BookRemoval.ToTrash, Ct);

        Assert.Equal(book.Metadata.Id, removed);
        Assert.False(Directory.Exists(folder));
        Assert.Null(await Index.GetBookAsync(book.Metadata.Id, Ct));
        Assert.Null(await queue.GetAsync(book.Metadata.Id, Ct));
        Assert.NotNull(await Index.GetBookAsync(keep.Metadata.Id, Ct));
        // "_To review" still has the other book; nothing above the removed folder is left empty.
        Assert.True(Directory.Exists(Path.Combine(_library.RootPath, "Books", LibraryLayout.ToReviewFolderName)));

        var trashed = Directory.GetDirectories(LibraryLayout.TrashPath(_library.RootPath)).Single();
        var moved = Directory.GetDirectories(trashed).Single();
        Assert.True(File.Exists(Path.Combine(moved, "Wrong file.pdf")));
        Assert.True(File.Exists(Path.Combine(moved, LibraryLayout.MetadataFileName)));
        var note = await File.ReadAllTextAsync(moved + ".trash.json", Ct);
        Assert.Contains(book.Metadata.Id, note);
        // The history stays, in case the folder is brought back.
        Assert.NotEmpty(await _editor.GetHistoryAsync(_library, book.Metadata.Id, Ct));

        // The trash is not part of the library.
        var rebuilt = await _libraries.RebuildIndexAsync(_library, Ct);
        Assert.Equal(1, rebuilt.IndexedBooks);
    }

    [Fact]
    public async Task Removing_a_book_and_its_files_deletes_the_folder_and_the_history()
    {
        var book = await ImportAsync("wrong_file.pdf");
        await _editor.UpdateAsync(_library, book.Metadata.Id, m => m with
        {
            Title = "Wrong file",
            Review = new ReviewInfo { Status = ReviewStatus.Approved, MatchedBy = MatchedBy.Manual },
        }, Ct);
        var folder = Path.Combine(_library.RootPath, (await Index.GetBookAsync(book.Metadata.Id, Ct))!.Folder);
        Assert.True(Directory.Exists(folder));

        await _editor.RemoveAsync(_library, book.Metadata.Id, BookRemoval.DeleteFiles, Ct);

        Assert.False(Directory.Exists(folder));
        // "_No publisher" was left empty, so it goes too.
        Assert.False(Directory.Exists(Path.Combine(_library.RootPath, "Books", "_No publisher")));
        Assert.True(Directory.Exists(LibraryLayout.BooksPath(_library.RootPath)));
        Assert.False(Directory.Exists(LibraryLayout.TrashPath(_library.RootPath)));
        Assert.Empty(await _editor.GetHistoryAsync(_library, book.Metadata.Id, Ct));
        Assert.Equal(0, await Index.CountBooksAsync(Ct));
    }

    [Fact]
    public async Task A_book_whose_files_were_deleted_by_hand_can_still_be_removed()
    {
        var book = await ImportAsync("wrong_file.pdf");
        var folder = Path.Combine(_library.RootPath, book.Folder);
        File.Delete(Path.Combine(folder, book.MainFile!.Name));

        var check = BookFilesCheck.Check(_library.RootPath, await Index.GetBooksAsync(Ct), Ct)!;
        Assert.Equal([book.Metadata.Id], check.MissingOriginal);
        Assert.Empty(check.MissingFolder);

        Directory.Delete(folder, recursive: true);
        check = BookFilesCheck.Check(_library.RootPath, await Index.GetBooksAsync(Ct), Ct)!;
        Assert.Empty(check.MissingOriginal);
        Assert.Equal([book.Metadata.Id], check.MissingFolder);

        await _editor.RemoveAsync(_library, book.Metadata.Id, BookRemoval.ToTrash, Ct);
        Assert.Equal(0, await Index.CountBooksAsync(Ct));
        Assert.False(Directory.Exists(LibraryLayout.TrashPath(_library.RootPath)));
    }

    [Fact]
    public void An_unreachable_library_is_not_reported_as_missing_every_file()
    {
        Assert.Null(BookFilesCheck.Check(_temp.Combine("Unplugged"), [new CatalogBook(new BookMetadata { Id = "x", Title = "X" }, "Books/X")], Ct));
    }
}

/// <summary>Every cover download answers a lime image.</summary>
internal sealed class LimeCovers : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(SyntheticImages.Jpeg(300, 450, SkiaSharp.SKColors.Lime)) { Headers = { ContentType = new("image/jpeg") } },
        });
}
