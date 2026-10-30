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

/// <summary>The automatic inbox (F5): the watched folder, imports without confirmation and the batch summary.</summary>
public sealed class InboxWatcherTests : IAsyncLifetime
{
    private static readonly InboxWatchOptions FastOptions = new()
    {
        SettleTime = TimeSpan.FromMilliseconds(150),
        PollInterval = TimeSpan.FromMilliseconds(50),
        RescanInterval = TimeSpan.FromMinutes(5),
        QuietTime = TimeSpan.FromMilliseconds(100),
    };

    private readonly TempDirectory _temp = new();
    private readonly AppPaths _paths;
    private readonly LibraryService _libraries;
    private readonly JsonSettingsService _settings;
    private readonly ImportQueueFactory _imports;
    private readonly IdentificationQueueFactory _identifications;
    private readonly FakeIdentifier _identifier = new();
    private readonly FakeRecycleBin _recycleBin;
    private LibraryInfo _library = null!;
    private ImportQueue _queue = null!;

    public InboxWatcherTests()
    {
        _paths = new AppPaths(_temp.Combine("config"), _temp.Combine("data"));
        _recycleBin = new FakeRecycleBin(_temp.Combine("Lixeira"));
        _libraries = new LibraryService(_paths, NullLogger<LibraryService>.Instance);
        _settings = new JsonSettingsService(_paths, NullLogger<JsonSettingsService>.Instance);
        var reader = new BookFileReader(NullLogger<BookFileReader>.Instance);
        var thumbnails = new ThumbnailCache(_paths, NullLogger<ThumbnailCache>.Instance);
        _imports = new ImportQueueFactory(_libraries, new ImportAnalyzer(reader, thumbnails),
            new BookImporter(reader, NullLogger<BookImporter>.Instance), thumbnails, _settings, _recycleBin, NullLogger<ImportQueue>.Instance);
        var covers = new CoverImageCache(new HttpClient(new LimeCovers()), _temp.Combine("covers"), NullLogger<CoverImageCache>.Instance);
        _identifications = new IdentificationQueueFactory(_libraries, _identifier, new BookEditor(_libraries, reader, NullLogger<BookEditor>.Instance),
            covers, _settings, NullLogger<IdentificationQueue>.Instance);
    }

    public async ValueTask InitializeAsync()
    {
        _library = await _libraries.CreateAsync(_temp.Combine("Library"), Ct);
        Directory.CreateDirectory(Inbox());
        // Most tests follow the files to _imported; the recycle bin and deleting have their own.
        await _settings.SaveAsync(_settings.Current with { InboxPath = Inbox(), AfterImport = AfterImportAction.MoveToImported }, Ct);
        _queue = _imports.Create(_library);
        await _queue.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _queue.DisposeAsync();
        _temp.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Inbox(params string[] parts) => _temp.Combine(["Inbox", .. parts]);

    private string Imported(params string[] parts) => Inbox([InboxFolder.ImportedFolderName, .. parts]);

    private LibraryIndex Index => _libraries.GetIndex(_library);

    private static string SagaInfo => SyntheticBooks.ComicInfo(series: "Saga", number: "1", year: 2012, writer: "Brian K. Vaughan",
        penciller: "Fiona Staples", publisher: "Image Comics", format: "TPB");

    private InboxWatcher Watch()
    {
        var watcher = new InboxWatcher(_queue, FastOptions, NullLogger<InboxWatcher>.Instance);
        watcher.Watch(Inbox());
        return watcher;
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        // Generous: a slow CI runner once took 2m42s for this project's tests, against about 20 s locally.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(30, Ct);
        }
    }

    private async Task AddAutoAndWaitAsync(params string[] files)
    {
        await _queue.AddAutoAsync(files, retryFailed: false, Ct);
        await _queue.WaitUntilIdleAsync(Ct);
    }

    // ---- Paths ----

    [Fact]
    public void Only_likely_books_outside_imported_are_picked_up_and_they_keep_their_subfolders()
    {
        var inbox = Inbox();
        Assert.True(InboxFolder.IsCandidate(inbox, Inbox("Saga.cbz")));
        Assert.True(InboxFolder.IsCandidate(inbox, Inbox("Humble", "Saga.PDF")));
        Assert.False(InboxFolder.IsCandidate(inbox, Inbox("Saga.cbz.crdownload")));
        Assert.False(InboxFolder.IsCandidate(inbox, Inbox("Saga.pdf.part")));
        Assert.False(InboxFolder.IsCandidate(inbox, Inbox(".Saga.pdf")));
        Assert.False(InboxFolder.IsCandidate(inbox, Imported("Saga.cbz")));
        Assert.False(InboxFolder.IsCandidate(inbox, _temp.Combine("Downloads", "Saga.cbz")));

        Assert.Equal(Imported("Humble", "Saga.pdf"), InboxFolder.ImportedPathFor(inbox, Inbox("Humble", "Saga.pdf")));
        Assert.Null(InboxFolder.ImportedPathFor(inbox, Imported("Saga.pdf")));
        Assert.Null(InboxFolder.ImportedPathFor(inbox, _temp.Combine("Downloads", "Saga.pdf")));

        SyntheticBooks.Pdf(Imported("Saga.pdf"));
        Assert.Equal(Imported("Saga (2).pdf"), InboxFolder.Unique(Imported("Saga.pdf")));
    }

    // ---- Queue without confirmation ----

    [Fact]
    public async Task A_file_from_the_watched_folder_is_imported_without_confirmation_and_moved_to_imported()
    {
        var source = SyntheticBooks.Cbz(Inbox("Humble", "Saga_Vol1.cbz"), comicInfoXml: SagaInfo);

        await AddAutoAndWaitAsync(source);

        var book = Assert.Single(await Index.GetBooksAsync(Ct));
        Assert.Equal("Saga_Vol1.cbz", book.Metadata.Files[0].DownloadedAs);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(Imported("Humble", "Saga_Vol1.cbz")));
        var item = Assert.Single(await _queue.GetEntriesAsync(Ct)).Item;
        Assert.Equal((InboxItemState.Imported, true, Imported("Humble", "Saga_Vol1.cbz")), (item.State, item.Auto, item.SourcePath));
        // The download folder was left empty: it goes away, the inbox stays.
        Assert.False(Directory.Exists(Inbox("Humble")));
        Assert.True(Directory.Exists(Inbox()));
    }

    [Fact]
    public async Task Folders_that_still_hold_something_are_kept()
    {
        var saga = SyntheticBooks.Cbz(Inbox("Humble", "Saga", "saga.cbz"), comicInfoXml: SagaInfo);
        await File.WriteAllTextAsync(Inbox("Humble", "readme.txt"), "x", Ct);

        await AddAutoAndWaitAsync(saga);

        Assert.False(Directory.Exists(Inbox("Humble", "Saga")));
        Assert.True(File.Exists(Inbox("Humble", "readme.txt")));

        // Outside the inbox nothing is touched, even when empty.
        var outside = _temp.Combine("Downloads", "empty");
        Directory.CreateDirectory(outside);
        InboxFolder.RemoveEmptyFolders(Inbox(), outside);
        Assert.True(Directory.Exists(outside));
        InboxFolder.RemoveEmptyFolders(Inbox(), Inbox());
        Assert.True(Directory.Exists(Inbox()));
    }

    [Fact]
    public async Task A_file_already_in_the_library_is_skipped_and_moved_too()
    {
        var first = SyntheticBooks.Cbz(Inbox("saga.cbz"), comicInfoXml: SagaInfo);
        await AddAutoAndWaitAsync(first);
        File.Copy(Imported("saga.cbz"), Inbox("saga.cbz"));

        await AddAutoAndWaitAsync(Inbox("saga.cbz"));

        Assert.Single(await Index.GetBooksAsync(Ct));
        var second = (await _queue.GetEntriesAsync(Ct)).Last().Item;
        Assert.Equal((InboxItemState.Skipped, InboxMessage.AlreadyInLibrary), (second.State, second.Message));
        Assert.False(File.Exists(Inbox("saga.cbz")));
        Assert.True(File.Exists(Imported("saga (2).cbz")));
    }

    [Fact]
    public async Task By_default_an_imported_file_goes_to_the_recycle_bin()
    {
        await _settings.SaveAsync(_settings.Current with { AfterImport = AfterImportAction.RecycleBin }, Ct);
        var source = SyntheticBooks.Cbz(Inbox("Humble", "Saga_Vol1.cbz"), comicInfoXml: SagaInfo);

        await AddAutoAndWaitAsync(source);

        Assert.Single(await Index.GetBooksAsync(Ct));
        Assert.Equal(["Saga_Vol1.cbz"], _recycleBin.Recycled);
        Assert.False(File.Exists(source));
        Assert.False(Directory.Exists(Imported()));
        Assert.False(Directory.Exists(Inbox("Humble")));

        // The same file again: already in the library, so it goes to the recycle bin too.
        SyntheticBooks.Cbz(Inbox("Saga_Vol1.cbz"), comicInfoXml: SagaInfo);
        await AddAutoAndWaitAsync(Inbox("Saga_Vol1.cbz"));
        Assert.Equal(2, _recycleBin.Recycled.Count);
        Assert.Single(await Index.GetBooksAsync(Ct));
    }

    [Fact]
    public async Task Without_a_recycle_bin_the_file_goes_to_imported_instead_of_being_deleted()
    {
        await _settings.SaveAsync(_settings.Current with { AfterImport = AfterImportAction.RecycleBin }, Ct);
        _recycleBin.Available = false;
        var source = SyntheticBooks.Cbz(Inbox("Saga_Vol1.cbz"), comicInfoXml: SagaInfo);

        await AddAutoAndWaitAsync(source);

        Assert.Empty(_recycleBin.Recycled);
        Assert.True(File.Exists(Imported("Saga_Vol1.cbz")));
    }

    [Fact]
    public async Task Deleting_removes_the_file_from_the_inbox_for_good()
    {
        await _settings.SaveAsync(_settings.Current with { AfterImport = AfterImportAction.Delete }, Ct);
        var source = SyntheticBooks.Cbz(Inbox("Humble", "Saga_Vol1.cbz"), comicInfoXml: SagaInfo);

        await AddAutoAndWaitAsync(source);

        Assert.Single(await Index.GetBooksAsync(Ct));
        Assert.False(File.Exists(source));
        Assert.Empty(_recycleBin.Recycled);
        Assert.False(Directory.Exists(Imported()));
        Assert.False(Directory.Exists(Inbox("Humble")));
    }

    [Fact]
    public async Task A_better_format_of_a_book_in_the_library_replaces_it_as_suggested()
    {
        await AddAutoAndWaitAsync(SyntheticBooks.Pdf(Inbox("saga.pdf"), new SyntheticBooks.PdfOptions { Title = "Saga, Vol. 1" }));

        await AddAutoAndWaitAsync(SyntheticBooks.Cbz(Inbox("saga.cbz"), comicInfoXml: SagaInfo));

        var book = Assert.Single(await Index.GetBooksAsync(Ct));
        var file = Assert.Single(book.Metadata.Files);
        Assert.Equal((BookFormat.Cbz, FileRole.Original), (file.Format, file.Role));
        Assert.Single(Directory.GetFiles(LibraryLayout.TrashPath(_library.RootPath), "*.pdf", SearchOption.AllDirectories));
        Assert.Equal(ImportDecision.Replace, (await _queue.GetEntriesAsync(Ct)).Last().Item.Decision);
    }

    [Fact]
    public async Task A_failed_file_stays_and_is_tried_again_only_when_written_again()
    {
        var notABook = Inbox("notes.cbz");
        await File.WriteAllTextAsync(notABook, "not a comic", Ct);

        await AddAutoAndWaitAsync(notABook);
        Assert.Equal(InboxItemState.Failed, Assert.Single(await _queue.GetEntriesAsync(Ct)).Item.State);
        Assert.True(File.Exists(notABook));

        // A rescan finds it again: left alone.
        Assert.Equal(0, await _queue.AddAutoAsync([notABook], retryFailed: false, Ct));

        // Written again (a new download with the same name): tried again.
        File.Delete(notABook);
        SyntheticBooks.Cbz(notABook, comicInfoXml: SagaInfo);
        Assert.Equal(1, await _queue.AddAutoAsync([notABook], retryFailed: true, Ct));
        await _queue.WaitUntilIdleAsync(Ct);
        Assert.Equal(InboxItemState.Imported, Assert.Single(await _queue.GetEntriesAsync(Ct)).Item.State);
    }

    [Fact]
    public async Task Files_added_by_hand_still_wait_for_confirmation_and_stay_where_they_are()
    {
        var source = SyntheticBooks.Cbz(Inbox("saga.cbz"), comicInfoXml: SagaInfo);

        await _queue.AddAsync([source], Ct);
        await _queue.WaitUntilIdleAsync(Ct);
        Assert.Equal(InboxItemState.Ready, Assert.Single(await _queue.GetEntriesAsync(Ct)).Item.State);

        await _queue.ConfirmAsync(Ct);
        await _queue.WaitUntilIdleAsync(Ct);
        Assert.True(File.Exists(source));
    }

    // ---- Watcher ----

    [Fact]
    public async Task Files_already_in_the_folder_are_found_when_watching_starts()
    {
        SyntheticBooks.Cbz(Inbox("saga.cbz"), comicInfoXml: SagaInfo);
        SyntheticBooks.Pdf(Inbox("sub", "other.pdf"));
        SyntheticBooks.Pdf(Imported("old.pdf"));
        await File.WriteAllTextAsync(Inbox("readme.txt"), "x", Ct);

        using var watcher = Watch();

        Assert.Equal(InboxWatchStatus.Watching, watcher.Status);
        await WaitForAsync(async () => (await Index.GetBooksAsync(Ct)).Count == 2 && await _queue.CountOpenAutoAsync(Ct) == 0);
        Assert.True(File.Exists(Imported("old.pdf")));
        Assert.True(File.Exists(Imported("sub", "other.pdf")));
        Assert.True(File.Exists(Inbox("readme.txt")));
    }

    [Fact]
    public async Task A_file_being_written_is_picked_up_only_after_it_is_closed()
    {
        using var watcher = Watch();
        var complete = await File.ReadAllBytesAsync(SyntheticBooks.Cbz(_temp.Combine("Downloads", "saga.cbz"), comicInfoXml: SagaInfo), Ct);

        var path = Inbox("saga.cbz");
        await using (var download = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            await download.WriteAsync(complete.AsMemory(0, complete.Length / 2), Ct);
            await download.FlushAsync(Ct);
            // Stays the same size for longer than the settle time, but the browser still has it open.
            // Only Windows tells: on Linux file sharing is advisory, and the settle time is the only guard.
            await Task.Delay(FastOptions.SettleTime * 4, Ct);
            if (OperatingSystem.IsWindows())
            {
                Assert.Empty(await _queue.GetEntriesAsync(Ct));
            }

            await download.WriteAsync(complete.AsMemory(complete.Length / 2), Ct);
        }

        await WaitForAsync(async () => (await _queue.GetEntriesAsync(Ct)).SingleOrDefault()?.Item.State == InboxItemState.Imported);
        Assert.Single(await Index.GetBooksAsync(Ct));
        Assert.True(File.Exists(Imported("saga.cbz")));
    }

    [Fact]
    public async Task A_missing_folder_is_watched_as_soon_as_it_appears()
    {
        Directory.Delete(Inbox(), recursive: true);
        using var watcher = Watch();
        Assert.Equal(InboxWatchStatus.Missing, watcher.Status);

        SyntheticBooks.Cbz(Inbox("saga.cbz"), comicInfoXml: SagaInfo);

        await WaitForAsync(() => Task.FromResult(watcher.Status == InboxWatchStatus.Watching));
        await WaitForAsync(async () => (await Index.GetBooksAsync(Ct)).Count == 1);

        watcher.Watch(null);
        Assert.Equal(InboxWatchStatus.Off, watcher.Status);
    }

    // ---- Batch summary ----

    [Fact]
    public async Task A_batch_is_reported_once_its_books_are_imported_and_identified()
    {
        _identifier.Respond = (book, _) => new IdentificationResult(BookQuery.From(book),
            book.Title.Contains("Saga", StringComparison.Ordinal)
                ? [FakeIdentifier.Saga(0.98, MatchReason.Isbn, MatchReason.SecondSource, MatchReason.Title, MatchReason.Number)]
                : [],
            []);
        // Before the batch: a book already in the library, whose copy arrives again.
        await AddAutoAndWaitAsync(SyntheticBooks.Cbz(Inbox("deadly.cbz"), comicInfoXml: SyntheticBooks.ComicInfo(series: "Deadly Class", number: "1")));
        File.Copy(Imported("deadly.cbz"), Inbox("deadly again.cbz"));

        await using var identification = _identifications.Create(_library);
        await identification.StartAsync(Ct);
        _queue.BookImported += (_, bookId) => _ = identification.EnqueueAsync([bookId], force: false);
        using var tracker = new ArrivalTracker(_queue, identification, Index, FastOptions, NullLogger<ArrivalTracker>.Instance);
        var summaries = new List<ArrivalSummary>();
        tracker.Completed += (_, s) => summaries.Add(s);

        // Manual imports are not part of it.
        await _queue.AddAsync([SyntheticBooks.Pdf(_temp.Combine("Downloads", "manual.pdf"))], Ct);
        await _queue.WaitUntilIdleAsync(Ct);
        await _queue.ConfirmAsync(Ct);

        var saga = SyntheticBooks.Cbz(Inbox("saga.cbz"), comicInfoXml: SagaInfo);
        var unknown = SyntheticBooks.Pdf(Inbox("houseofslaughter_vol1.pdf"), new SyntheticBooks.PdfOptions { Pages = 4 });
        var broken = Inbox("notes.cbz");
        await File.WriteAllTextAsync(broken, "not a comic", Ct);
        await _queue.AddAutoAsync([saga, unknown, broken, Inbox("deadly again.cbz")], retryFailed: false, Ct);

        await WaitForAsync(async () =>
        {
            await tracker.CheckAsync();
            return summaries.Count > 0;
        });

        Assert.Equal(new ArrivalSummary(Files: 4, Approved: 1, ToReview: 1, AlreadyInLibrary: 1, Failed: 1), Assert.Single(summaries));
        await tracker.CheckAsync();
        Assert.Single(summaries);
    }
}
