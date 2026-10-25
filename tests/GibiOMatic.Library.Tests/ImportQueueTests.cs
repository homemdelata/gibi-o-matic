using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using GibiOMatic.Conversion;
using GibiOMatic.Core;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Ids;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Core.Quality;
using GibiOMatic.Core.Settings;
using GibiOMatic.Library.Import;
using GibiOMatic.Library.Index;
using Microsoft.Extensions.Logging.Abstractions;

namespace GibiOMatic.Library.Tests;

public sealed class ImportQueueTests : IAsyncLifetime
{
    private readonly TempDirectory _temp = new();
    private readonly AppPaths _paths;
    private readonly LibraryService _libraries;
    private readonly JsonSettingsService _settings;
    private readonly ImportQueueFactory _factory;
    private LibraryInfo _library = null!;
    private ImportQueue _queue = null!;

    public ImportQueueTests()
    {
        _paths = new AppPaths(_temp.Combine("config"), _temp.Combine("data"));
        _libraries = new LibraryService(_paths, NullLogger<LibraryService>.Instance);
        _settings = new JsonSettingsService(_paths, NullLogger<JsonSettingsService>.Instance);
        var reader = new BookFileReader(NullLogger<BookFileReader>.Instance);
        var thumbnails = new ThumbnailCache(_paths, NullLogger<ThumbnailCache>.Instance);
        _factory = new ImportQueueFactory(_libraries, new ImportAnalyzer(reader, thumbnails),
            new BookImporter(reader, NullLogger<BookImporter>.Instance), thumbnails, _settings, new FakeRecycleBin(_temp.Combine("Lixeira")), NullLogger<ImportQueue>.Instance);
    }

    public async ValueTask InitializeAsync()
    {
        _library = await _libraries.CreateAsync(_temp.Combine("Arquivo"), Ct);
        _queue = await StartQueueAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _queue.DisposeAsync();
        _temp.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Downloads(params string[] parts) => _temp.Combine(["Downloads", .. parts]);

    private async Task<ImportQueue> StartQueueAsync()
    {
        var queue = _factory.Create(_library);
        await queue.StartAsync(Ct);
        return queue;
    }

    private async Task<IReadOnlyList<InboxEntry>> AddAndAnalyzeAsync(params string[] paths)
    {
        await _queue.AddAsync(paths, Ct);
        await _queue.WaitUntilIdleAsync(Ct);
        return await _queue.GetEntriesAsync(Ct);
    }

    private async Task<IReadOnlyList<InboxEntry>> ConfirmAndWaitAsync()
    {
        await _queue.ConfirmAsync(Ct);
        await _queue.WaitUntilIdleAsync(Ct);
        return await _queue.GetEntriesAsync(Ct);
    }

    private async Task<IReadOnlyList<CatalogBook>> BooksAsync() => await _libraries.GetIndex(_library).GetBooksAsync(Ct);

    private static string Sha256Of(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static string SagaInfo => SyntheticBooks.ComicInfo(series: "Saga", number: "1", year: 2012, writer: "Brian K. Vaughan",
        penciller: "Fiona Staples", publisher: "Image Comics", format: "TPB");

    [Fact]
    public async Task A_file_is_analysed_but_only_copied_after_confirmation()
    {
        var source = SyntheticBooks.Cbz(Downloads("Saga_Vol1_Image.cbz"), comicInfoXml: SagaInfo);

        var entry = Assert.Single(await AddAndAnalyzeAsync(source));

        Assert.Equal(InboxItemState.Ready, entry.Item.State);
        Assert.Equal(ImportDecision.Import, entry.Decision);
        Assert.Null(entry.Duplicate);
        var analysis = entry.Item.Analysis!;
        Assert.Equal(("Saga, Vol. 1", BookFormat.Cbz), (analysis.Title, analysis.Format));
        Assert.Equal(Sha256Of(source), analysis.Sha256);
        Assert.True(analysis.HasThumbnail);
        Assert.Empty(await BooksAsync());
        Assert.Empty(Directory.EnumerateFileSystemEntries(LibraryLayout.BooksPath(_library.RootPath)));

        var imported = Assert.Single(await ConfirmAndWaitAsync());

        Assert.Equal(InboxItemState.Imported, imported.Item.State);
        var book = Assert.Single(await BooksAsync());
        Assert.Equal(imported.Item.BookId, book.Metadata.Id);
        // Publisher and series are still guesses: the book waits in "_a revisar" until approved.
        Assert.Equal($"Livros/_a revisar/Saga, Vol. 1 [{Ulid.ShortId(book.Metadata.Id)}]", book.Folder);

        var folder = Path.Combine(_library.RootPath, book.Folder);
        var original = Path.Combine(folder, "Saga, Vol. 1.cbz");
        Assert.Equal(Sha256Of(source), Sha256Of(original));
        Assert.True(File.Exists(Path.Combine(folder, "cover.jpg")));
        Assert.True(File.Exists(source), "the downloaded file stays where it was");

        var metadata = await MetadataFile.ReadAsync(Path.Combine(folder, "metadata.json"), Ct);
        var file = Assert.Single(metadata.Files);
        Assert.Equal(("Saga_Vol1_Image.cbz", FileRole.Original, Sha256Of(source)), (file.DownloadedAs, file.Role, file.Sha256));
        Assert.Equal(new SeriesInfo("Saga", "1"), metadata.Series);
        Assert.Equal(BookKind.Tpb, metadata.Kind);
        Assert.Equal(ReviewStatus.Pending, metadata.Review.Status);
        Assert.Equal(["Brian K. Vaughan", "Fiona Staples"], metadata.Credits.Select(c => c.Name));
    }

    [Fact]
    public async Task Books_without_metadata_go_to_the_to_review_folder_with_the_file_name_as_title()
    {
        var source = SyntheticBooks.Pdf(Downloads("houseofslaughter_vol1.pdf"));

        await AddAndAnalyzeAsync(source);
        await ConfirmAndWaitAsync();

        var book = Assert.Single(await BooksAsync());
        Assert.Equal("houseofslaughter vol1", book.Metadata.Title);
        Assert.StartsWith("Livros/_a revisar/houseofslaughter vol1 [", book.Folder);
        Assert.Equal(QualityBand.Bad, QualityAnalyzer.Analyze(book.Metadata).Band);
    }

    [Fact]
    public async Task The_same_file_twice_is_skipped()
    {
        var source = SyntheticBooks.Cbz(Downloads("saga.cbz"), comicInfoXml: SagaInfo);
        await AddAndAnalyzeAsync(source);
        await ConfirmAndWaitAsync();
        await _queue.ClearFinishedAsync(Ct);

        var copy = Downloads("copia", "saga (1).cbz");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(source, copy);
        var entry = Assert.Single(await AddAndAnalyzeAsync(copy));

        Assert.Equal(DuplicateKind.SameFileInLibrary, entry.Duplicate);
        Assert.Equal([ImportDecision.Skip], entry.AllowedDecisions);

        var done = Assert.Single(await ConfirmAndWaitAsync());
        Assert.Equal((InboxItemState.Skipped, InboxMessage.AlreadyInLibrary), (done.Item.State, done.Item.Message));
        Assert.Single(await BooksAsync());
    }

    [Fact]
    public async Task The_same_book_in_two_formats_becomes_one_book_with_the_preferred_format_as_original()
    {
        var pdf = SyntheticBooks.Pdf(Downloads("Saga Vol 1.pdf"), new SyntheticBooks.PdfOptions { Title = "Saga, Vol. 1" });
        var cbz = SyntheticBooks.Cbz(Downloads("Saga_v01.cbz"), comicInfoXml: SagaInfo);

        var entries = await AddAndAnalyzeAsync(pdf, cbz);

        Assert.Null(entries[0].Duplicate);
        Assert.Equal(DuplicateKind.SameBookInList, entries[1].Duplicate);
        Assert.Equal(ImportDecision.KeepBoth, entries[1].Decision);

        await ConfirmAndWaitAsync();

        var book = Assert.Single(await BooksAsync());
        Assert.Equal(2, book.Metadata.Files.Count);
        Assert.Equal(BookFormat.Cbz, book.MainFile!.Format);
        Assert.Equal(BookFormat.Pdf, Assert.Single(book.Metadata.Files, f => f.Role == FileRole.Extra).Format);
        // The CBZ only filled what the PDF did not have; the book is still waiting for review.
        Assert.Equal("Image Comics", book.Metadata.Publisher);
        Assert.StartsWith("Livros/_a revisar/Saga, Vol. 1 [", book.Folder);
        Assert.Single(Directory.GetDirectories(Path.Combine(_library.RootPath, "Livros")));
        Assert.True(File.Exists(Path.Combine(_library.RootPath, book.Folder, "Saga, Vol. 1.cbz")));
        Assert.True(File.Exists(Path.Combine(_library.RootPath, book.Folder, "Saga, Vol. 1.pdf")));
    }

    [Fact]
    public async Task Replace_moves_the_old_original_to_the_internal_trash_and_keeps_history()
    {
        var pdf = SyntheticBooks.Pdf(Downloads("saga.pdf"), new SyntheticBooks.PdfOptions { Title = "Saga, Vol. 1" });
        await AddAndAnalyzeAsync(pdf);
        await ConfirmAndWaitAsync();
        var before = Assert.Single(await BooksAsync());

        var cbz = SyntheticBooks.Cbz(Downloads("saga.cbz"), comicInfoXml: SagaInfo);
        var entry = (await AddAndAnalyzeAsync(cbz)).Single(e => e.Item.State == InboxItemState.Ready);

        Assert.Equal(DuplicateKind.SameBookInLibrary, entry.Duplicate);
        Assert.Equal(ImportDecision.Replace, entry.Decision); // CBZ is preferred over PDF
        await ConfirmAndWaitAsync();

        var after = Assert.Single(await BooksAsync());
        Assert.Equal(before.Metadata.Id, after.Metadata.Id);
        var file = Assert.Single(after.Metadata.Files);
        Assert.Equal((BookFormat.Cbz, FileRole.Original), (file.Format, file.Role));
        Assert.False(File.Exists(Path.Combine(_library.RootPath, after.Folder, "Saga, Vol. 1.pdf")));

        var trashed = Directory.GetFiles(LibraryLayout.TrashPath(_library.RootPath), "*.pdf", SearchOption.AllDirectories);
        Assert.Equal(Sha256Of(pdf), Sha256Of(Assert.Single(trashed)));
        using var note = JsonDocument.Parse(await File.ReadAllTextAsync(trashed[0] + ".trash.json", Ct));
        Assert.Equal(after.Metadata.Id, note.RootElement.GetProperty("bookId").GetString());

        var history = Directory.GetFiles(Path.Combine(LibraryLayout.HistoryPath(_library.RootPath), after.Metadata.Id));
        Assert.Single(history);
    }

    [Fact]
    public async Task A_worse_format_of_an_existing_book_is_kept_as_extra()
    {
        await AddAndAnalyzeAsync(SyntheticBooks.Cbz(Downloads("saga.cbz"), comicInfoXml: SagaInfo));
        await ConfirmAndWaitAsync();

        var cbr = SyntheticBooks.Cbr(Downloads("saga.cbr"), comicInfoXml: SagaInfo);
        var entry = (await AddAndAnalyzeAsync(cbr)).Single(e => e.Item.State == InboxItemState.Ready);
        Assert.Equal(ImportDecision.KeepBoth, entry.Decision);
        await ConfirmAndWaitAsync();

        var book = Assert.Single(await BooksAsync());
        Assert.Equal(BookFormat.Cbz, book.MainFile!.Format);
        Assert.Equal(2, book.Metadata.Files.Count);
    }

    [Fact]
    public async Task The_user_can_skip_a_file()
    {
        var entry = Assert.Single(await AddAndAnalyzeAsync(SyntheticBooks.Pdf(Downloads("a.pdf"))));

        await _queue.SetDecisionAsync(entry.Item.Id, ImportDecision.Skip, Ct);
        var done = Assert.Single(await ConfirmAndWaitAsync());

        Assert.Equal((InboxItemState.Skipped, InboxMessage.SkippedByUser), (done.Item.State, done.Item.Message));
        Assert.Empty(await BooksAsync());
    }

    [Fact]
    public async Task Folders_are_searched_for_books_and_other_files_are_left_out()
    {
        SyntheticBooks.Pdf(Downloads("lote", "a.pdf"));
        SyntheticBooks.Epub(Downloads("lote", "sub", "b.epub"));
        await File.WriteAllTextAsync(Downloads("lote", "leia-me.txt"), "hello", Ct);

        var entries = await AddAndAnalyzeAsync(Downloads("lote"));

        Assert.Equal(["a.pdf", "b.epub"], entries.Select(e => Path.GetFileName(e.Item.SourcePath)));
        Assert.All(entries, e => Assert.Equal(InboxItemState.Ready, e.Item.State));
    }

    [Fact]
    public async Task A_file_that_is_not_a_book_fails_with_a_reason()
    {
        var text = Downloads("notes.cbz");
        Directory.CreateDirectory(Downloads());
        await File.WriteAllTextAsync(text, "not a comic", Ct);

        var entry = Assert.Single(await AddAndAnalyzeAsync(text));

        Assert.Equal((InboxItemState.Failed, InboxMessage.UnsupportedFormat), (entry.Item.State, entry.Item.Message));
    }

    [Fact]
    public async Task A_file_changed_after_analysis_is_not_imported()
    {
        var source = SyntheticBooks.Pdf(Downloads("saga.pdf"));
        await AddAndAnalyzeAsync(source);
        SyntheticBooks.Pdf(source, new SyntheticBooks.PdfOptions { Pages = 5 });

        var entry = Assert.Single(await ConfirmAndWaitAsync());

        Assert.Equal((InboxItemState.Failed, InboxMessage.SourceChanged), (entry.Item.State, entry.Item.Message));
        Assert.Empty(await BooksAsync());
        Assert.Empty(Directory.EnumerateFiles(LibraryLayout.BooksPath(_library.RootPath), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Files_inside_the_library_are_ignored_and_repeated_paths_are_added_once()
    {
        var source = SyntheticBooks.Pdf(Downloads("a.pdf"));
        var inside = SyntheticBooks.Pdf(Path.Combine(_library.RootPath, "Livros", "x.pdf"));

        Assert.Equal(1, await _queue.AddAsync([source, source, inside], Ct));
        Assert.Equal(0, await _queue.AddAsync([source], Ct));
    }

    [Fact]
    public async Task Pause_is_remembered_and_the_queue_resumes_after_restarting()
    {
        await _queue.PauseAsync(Ct);
        await _queue.AddAsync([SyntheticBooks.Pdf(Downloads("a.pdf")), SyntheticBooks.Pdf(Downloads("b.pdf"), new SyntheticBooks.PdfOptions { Pages = 3 })], Ct);
        await Task.Delay(100, Ct);
        Assert.All(await _queue.GetEntriesAsync(Ct), e => Assert.Equal(InboxItemState.Pending, e.Item.State));

        // "Close the app" and open it again.
        await _queue.DisposeAsync();
        _queue = await StartQueueAsync();
        Assert.True(_queue.IsPaused);

        await _queue.ResumeAsync(Ct);
        await _queue.WaitUntilIdleAsync(Ct);
        await ConfirmAndWaitAsync();

        Assert.Equal(2, (await BooksAsync()).Count);
        var summary = await _queue.GetSummaryAsync(Ct);
        Assert.Equal((2, 0, false), (summary.Imported, summary.Failed, summary.HasWork));
    }

    [Fact]
    public async Task Rebuilding_the_index_keeps_exact_duplicate_detection_and_the_inbox()
    {
        var source = SyntheticBooks.Pdf(Downloads("a.pdf"));
        await AddAndAnalyzeAsync(source);
        await ConfirmAndWaitAsync();

        await _libraries.RebuildIndexAsync(_library, Ct);

        Assert.NotNull(await _libraries.GetIndex(_library).FindBookBySha256Async(Sha256Of(source), Ct));
        Assert.Single(await _queue.GetEntriesAsync(Ct));
    }

    [Fact]
    public async Task A_library_of_1000_books_loads_quickly()
    {
        var index = _libraries.GetIndex(_library);
        var now = DateTimeOffset.UtcNow;
        var books = Enumerable.Range(1, 1000).Select(i => new IndexedBook(new BookMetadata
        {
            Id = Ulid.NewUlid(),
            Title = $"Series {i % 50}, Vol. {i}",
            Series = new SeriesInfo($"Series {i % 50}", i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Publisher = "Image Comics",
            Credits = [new Credit(CreditRole.Writer, "Ann Writer"), new Credit(CreditRole.Artist, "Bo Artist")],
            Date = new PartialDate(2000 + i % 25, 1 + i % 12, 1 + i % 28),
            Files = [new BookFile { Name = $"{i}.cbz", Format = BookFormat.Cbz, Sha256 = $"{i:x64}", Bytes = 1000 }],
            CreatedAt = now,
            UpdatedAt = now,
        }, $"Livros/Image Comics/Series/{i}", DateTime.UtcNow));
        await index.ReplaceAllAsync(books, Ct);

        var watch = Stopwatch.StartNew();
        var loaded = await index.GetBooksAsync(Ct);
        var context = QualityContext.From(loaded.Select(b => b.Metadata));
        var reports = loaded.Select(b => QualityAnalyzer.Analyze(b.Metadata, context)).ToList();
        watch.Stop();

        Assert.Equal(1000, reports.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Loading took {watch.Elapsed}");
    }
}
