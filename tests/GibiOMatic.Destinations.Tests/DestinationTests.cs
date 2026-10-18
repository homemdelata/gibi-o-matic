using System.IO.Compression;
using System.Net;
using System.Xml.Linq;
using GibiOMatic.Conversion;
using GibiOMatic.Core;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Core.Secrets;
using GibiOMatic.Core.Settings;
using GibiOMatic.Destinations.Komga;
using GibiOMatic.Destinations.Sync;
using GibiOMatic.Library;
using GibiOMatic.Library.Editing;
using GibiOMatic.Library.Import;
using GibiOMatic.Library.Index;
using Microsoft.Extensions.Logging.Abstractions;

namespace GibiOMatic.Destinations.Tests;

public sealed class OutputNamesTests
{
    private static BookMetadata Book(string title, string? series = null, string? number = null, int? year = null, BookKind kind = BookKind.Tpb,
        bool? oneShot = null) => new()
        {
            Id = "01ARYZ6S41TSV4RRFFQ69G5FAV",
            Title = title,
            Kind = kind,
            Series = series is null ? null : new SeriesInfo(series, number),
            Date = year is null ? null : new PartialDate(year.Value),
            Publisher = "Image",
            OneShot = oneShot,
        };

    [Fact]
    public void Komga_default_names_follow_the_spec()
    {
        Assert.Equal("Saga/Saga v01 (2012)", OutputNames.KomgaPath(Book("Saga, Vol. 1", "Saga", "1", 2012)));
        Assert.Equal("Saga/Saga v01", OutputNames.KomgaPath(Book("Saga, Vol. 1", "Saga", "1")));
        Assert.Equal("House of Slaughter/House of Slaughter #001 (2021)",
            OutputNames.KomgaPath(Book("House of Slaughter #1", "House of Slaughter", "1", 2021, BookKind.Issue)));
        Assert.Equal("Deadly Class/Deadly Class v12.5", OutputNames.KomgaPath(Book("x", "Deadly Class", "12.5")));
        Assert.Equal("Deadly Class - Reagan Youth/Deadly Class - Reagan Youth v01", OutputNames.KomgaPath(Book("x", "Deadly Class: Reagan Youth", "1")));
    }

    [Fact]
    public void One_shots_go_to_the_one_shots_folder_or_to_a_folder_of_their_own()
    {
        Assert.Equal("_oneshots/Plutona (2017)", OutputNames.KomgaPath(Book("Plutona", year: 2017)));
        Assert.Equal("_oneshots/Plutona (2017)", OutputNames.KomgaPath(Book("Plutona", "Plutona", "1", 2017, oneShot: true)));
        Assert.Equal("Plutona/Plutona (2017)", OutputNames.KomgaPath(Book("Plutona", year: 2017), oneShotsFolder: ""));
    }

    [Fact]
    public void Templates_can_add_the_publisher_folder()
    {
        Assert.Equal("Image/Saga/Saga v01 (2012)",
            OutputNames.KomgaPath(Book("Saga, Vol. 1", "Saga", "1", 2012), seriesTemplate: "{publisher}/{series}/{series} {num} ({year})"));
    }
}

public sealed class DestinationFolderTests
{
    [Theory]
    [InlineData(@"Z:\", @"Saga/Saga v01.cbz", @"Z:\Saga\Saga v01.cbz")]
    [InlineData(@"Z:\Quadrinhos\", @"_oneshots/Shrugged.cbz", @"Z:\Quadrinhos\_oneshots\Shrugged.cbz")]
    [InlineData(@"Z:\Quadrinhos", @"Saga.cbz", @"Z:\Quadrinhos\Saga.cbz")]
    public void Copies_go_inside_the_folder_even_at_a_drive_root(string folder, string relative, string expected)
    {
        Assert.Equal(expected, DestinationSync.InsideFolder(folder, relative));
    }

    [Theory]
    [InlineData(@"Z:\Quadrinhos", @"../outra/Saga.cbz")]
    [InlineData(@"Z:\Quadrinhos", @"..\Quadrinhos2\Saga.cbz")]
    [InlineData(@"Z:\", @"")]
    public void Paths_leaving_the_folder_are_refused(string folder, string relative)
    {
        Assert.Throws<InvalidOperationException>(() => DestinationSync.InsideFolder(folder, relative));
    }
}

public sealed class KomgaClientTests
{
    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_libraries_and_asks_for_a_scan_with_the_api_key()
    {
        var http = new FakeHttp(r => r.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[{"id":"0A1","name":"Quadrinhos","root":"/data/comics","oneshotsDirectory":"_oneshots"}]""",
                    System.Text.Encoding.UTF8, "application/json"),
            }
            : new HttpResponseMessage(HttpStatusCode.Accepted));
        var client = new KomgaClient(new HttpClient(http), NullLogger<KomgaClient>.Instance);

        var library = Assert.Single(await client.GetLibrariesAsync("http://192.168.0.10:25600", "secret-key", Ct));
        await client.ScanAsync("http://192.168.0.10:25600/", "secret-key", library.Id, Ct);

        Assert.Equal(new KomgaLibrary("0A1", "Quadrinhos", "/data/comics", "_oneshots"), library);
        Assert.Equal("http://192.168.0.10:25600/api/v1/libraries", http.Requests[0].RequestUri!.ToString());
        Assert.Equal("http://192.168.0.10:25600/api/v1/libraries/0A1/scan", http.Requests[1].RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, http.Requests[1].Method);
        Assert.All(http.Requests, r => Assert.Equal("secret-key", r.Headers.GetValues("X-API-Key").Single()));
    }

    [Fact]
    public async Task Errors_are_reported()
    {
        var client = new KomgaClient(new HttpClient(new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))), NullLogger<KomgaClient>.Instance);

        var ex = await Assert.ThrowsAsync<KomgaException>(() => client.GetLibrariesAsync("http://komga", "bad", Ct));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.Status);
        await Assert.ThrowsAsync<KomgaException>(() => client.GetLibrariesAsync("not a url", "k", Ct));
    }

    private static HttpResponseMessage WebPage() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("<!DOCTYPE html><html></html>", System.Text.Encoding.UTF8, "text/html"),
    };

    [Fact]
    public async Task An_address_copied_from_the_browser_falls_back_to_the_server_root()
    {
        var http = new FakeHttp(r => r.RequestUri!.AbsolutePath == "/api/v1/libraries"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json") }
            : WebPage());
        var client = new KomgaClient(new HttpClient(http), NullLogger<KomgaClient>.Instance);

        Assert.Empty(await client.GetLibrariesAsync("http://komga:25600/libraries/0A1/series#top", "k", Ct));
        Assert.Equal("http://komga:25600/libraries/0A1/series/api/v1/libraries", http.Requests[0].RequestUri!.ToString());
        Assert.Equal("http://komga:25600/api/v1/libraries", http.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task A_web_page_instead_of_the_api_is_reported_not_thrown_as_json_error()
    {
        var client = new KomgaClient(new HttpClient(new FakeHttp(_ => WebPage())), NullLogger<KomgaClient>.Instance);

        var ex = await Assert.ThrowsAsync<KomgaException>(() => client.GetLibrariesAsync("http://komga:25600", "k", Ct));
        Assert.True(ex.NotApi);

        var plain = new KomgaClient(new HttpClient(new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>") })),
            NullLogger<KomgaClient>.Instance);
        Assert.True((await Assert.ThrowsAsync<KomgaException>(() => plain.GetLibrariesAsync("http://komga:25600", "k", Ct))).NotApi);
    }
}

public sealed class SyncTests : IAsyncLifetime
{
    private sealed class NoSecrets : ISecretStore
    {
        public bool IsSupported => false;
        public Task<string?> GetAsync(string name, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetAsync(string name, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
    }

    private readonly TempDirectory _temp = new();
    private readonly LibraryService _libraries;
    private readonly JsonSettingsService _settings;
    private readonly BookEditor _editor;
    private readonly ImportQueueFactory _imports;
    private readonly KomgaDestinationType _komga;
    private LibraryInfo _library = null!;
    private DestinationService _service = null!;

    public SyncTests()
    {
        var paths = new AppPaths(_temp.Combine("config"), _temp.Combine("data"));
        _libraries = new LibraryService(paths, NullLogger<LibraryService>.Instance);
        _settings = new JsonSettingsService(paths, NullLogger<JsonSettingsService>.Instance);
        var reader = new BookFileReader(NullLogger<BookFileReader>.Instance);
        var thumbnails = new ThumbnailCache(paths, NullLogger<ThumbnailCache>.Instance);
        _editor = new BookEditor(_libraries, reader, NullLogger<BookEditor>.Instance);
        _imports = new ImportQueueFactory(_libraries, new ImportAnalyzer(reader, thumbnails),
            new BookImporter(reader, NullLogger<BookImporter>.Instance), thumbnails, _settings, NullLogger<ImportQueue>.Instance);
        _komga = new KomgaDestinationType(new KomgaClient(new HttpClient(), NullLogger<KomgaClient>.Instance), new NoSecrets(), NullLoggerFactory.Instance);
    }

    public async ValueTask InitializeAsync()
    {
        _library = await _libraries.CreateAsync(_temp.Combine("Arquivo"), Ct);
        await ConfigureAsync(c => c);
        _service = new DestinationServiceFactory(_libraries, [_komga], _settings, NullLogger<DestinationService>.Instance).Create(_library);
        _service.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync();
        _temp.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string KomgaFolder => _temp.Combine("Komga");

    private const string DestinationId = "01ARYZ6S41TSV4RRFFQ69G5KOM";

    private Task ConfigureAsync(Func<DestinationConfig, DestinationConfig> change)
    {
        Directory.CreateDirectory(KomgaFolder);
        var config = change(_komga.CreateDefault(DestinationId, "Komga de casa") with { Folder = KomgaFolder });
        return _settings.SaveAsync(_settings.Current with { Destinations = [config] }, Ct);
    }

    private IDestination Destination => _service.Find(DestinationId)!;

    private async Task<CatalogBook> ImportAsync(string fileName, Func<string, string>? make = null, bool approve = true)
    {
        await using var imports = _imports.Create(_library);
        await imports.StartAsync(Ct);
        var source = make?.Invoke(_temp.Combine("Downloads", fileName))
                     ?? SyntheticBooks.Cbz(_temp.Combine("Downloads", fileName), comicInfoXml: SyntheticBooks.ComicInfo(series: "Saga", number: "1", year: 2012, publisher: "Image Comics"));
        await imports.AddAsync([source], Ct);
        await imports.WaitUntilIdleAsync(Ct);
        await imports.ConfirmAsync(Ct);
        await imports.WaitUntilIdleAsync(Ct);
        var book = (await _libraries.GetIndex(_library).GetBooksAsync(Ct)).Single(b => b.Metadata.Files.Any(f => f.DownloadedAs == fileName));
        return approve ? await ApproveAsync(book.Metadata.Id) : book;
    }

    private Task<CatalogBook> ApproveAsync(string id) => _editor.UpdateAsync(_library, id,
        m => m with { Review = new ReviewInfo { Status = ReviewStatus.Approved, MatchedBy = MatchedBy.Manual } }, Ct);

    private async Task<SyncResult> RunAsync()
    {
        _service.Run(await _service.PlanAsync(DestinationId, Ct));
        await _service.WaitUntilIdleAsync(Ct);
        return _service.LastResult(DestinationId)!;
    }

    [Fact]
    public async Task Only_reviewed_books_are_planned_and_the_copy_is_a_cbz_with_comic_info()
    {
        await ImportAsync("saga.cbz");
        var pending = await ImportAsync("other.cbz", p => SyntheticBooks.Pdf(p), approve: false);

        var plan = await _service.PlanAsync(DestinationId, Ct);

        Assert.Equal((1, 0, 0), (plan.Creates, plan.Updates, plan.Deletes));
        Assert.DoesNotContain(plan.Actions, a => a.BookId == pending.Metadata.Id);
        Assert.False(await _service.IsConfirmedAsync(DestinationId, Ct));

        var result = await RunAsync();

        Assert.Equal((1, 0), (result.Written, result.Failures.Count));
        var cbz = Path.Combine(KomgaFolder, "Saga", "Saga v01 (2012).cbz");
        using (var zip = ZipFile.OpenRead(cbz))
        {
            var info = XDocument.Load(zip.GetEntry("ComicInfo.xml")!.Open()).Root!;
            Assert.Equal(("Saga", "1", "Image Comics"), (info.Element("Series")?.Value, info.Element("Number")?.Value, info.Element("Publisher")?.Value));
        }

        Assert.True(await _service.IsConfirmedAsync(DestinationId, Ct));
        Assert.True((await _service.PlanAsync(DestinationId, Ct)).IsEmpty);
        Assert.Empty(Directory.GetFiles(KomgaFolder, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Renaming_a_book_moves_its_copy_and_cleans_up_and_leaving_the_destination_removes_it()
    {
        var book = await ImportAsync("saga.cbz");
        await RunAsync();
        var stranger = Path.Combine(KomgaFolder, "Saga", "my own notes.txt");
        await File.WriteAllTextAsync(Path.Combine(KomgaFolder, "keep-me.cbz"), "not ours", Ct);

        await _editor.UpdateAsync(_library, book.Metadata.Id, m => m with { Series = new SeriesInfo("Saga Deluxe", "1") }, Ct);
        var plan = await _service.PlanAsync(DestinationId, Ct);
        Assert.Equal(SyncActionKind.Update, Assert.Single(plan.Actions).Kind);
        await RunAsync();

        Assert.True(File.Exists(Path.Combine(KomgaFolder, "Saga Deluxe", "Saga Deluxe v01 (2012).cbz")));
        Assert.False(Directory.Exists(Path.Combine(KomgaFolder, "Saga")), "the old series folder is gone");

        await ConfigureAsync(c => c with { Which = DestinationScope.ChosenBooks });
        Assert.Equal(SyncActionKind.Delete, Assert.Single((await _service.PlanAsync(DestinationId, Ct)).Actions).Kind);
        await RunAsync();

        Assert.False(Directory.Exists(Path.Combine(KomgaFolder, "Saga Deluxe")));
        Assert.True(File.Exists(Path.Combine(KomgaFolder, "keep-me.cbz")), "files the app did not create are never touched");
        Assert.False(File.Exists(stranger));
    }

    [Fact]
    public async Task Automatic_destinations_follow_edits_only_after_the_first_confirmed_sync()
    {
        var book = await ImportAsync("saga.cbz");
        _editor.BookSaved += (_, e) => _service.OnBookChanged(e.BookId);

        await _editor.UpdateAsync(_library, book.Metadata.Id, m => m with { Summary = "before the first sync" }, Ct);
        await _service.WaitUntilIdleAsync(Ct);
        Assert.Empty(Directory.GetFiles(KomgaFolder, "*.cbz", SearchOption.AllDirectories));

        await RunAsync();
        await _editor.UpdateAsync(_library, book.Metadata.Id, m => m with { Title = "Saga, Vol. 1: Chapter One", Summary = "after" }, Ct);
        await _service.WaitUntilIdleAsync(Ct);

        var cbz = Path.Combine(KomgaFolder, "Saga", "Saga v01 (2012).cbz");
        using var zip = ZipFile.OpenRead(cbz);
        var info = XDocument.Load(zip.GetEntry("ComicInfo.xml")!.Open()).Root!;
        Assert.Equal(("Saga, Vol. 1: Chapter One", "after"), (info.Element("Title")?.Value, info.Element("Summary")?.Value));

        var copies = await _service.CopiesOfAsync((await _libraries.GetIndex(_library).GetBookAsync(book.Metadata.Id, Ct))!, Ct);
        Assert.Equal(CopyState.UpToDate, Assert.Single(copies).State);
    }

    [Fact]
    public async Task Text_books_go_as_epub_only_when_chosen()
    {
        var novel = await ImportAsync("novel.epub", p => SyntheticBooks.Epub(p, new SyntheticBooks.EpubOptions
        {
            Title = "A Novel", TextOnly = true, FixedLayout = false, DeclareCover = false, Pages = 8,
        }));
        Assert.Equal(BookKind.Book, novel.Metadata.Kind);
        Assert.True((await _service.PlanAsync(DestinationId, Ct)).IsEmpty);

        await ConfigureAsync(c => c with { IncludeTextBooks = true });
        await RunAsync();

        Assert.True(File.Exists(Path.Combine(KomgaFolder, "_oneshots", "A Novel.epub")));
    }

    [Fact]
    public async Task An_unavailable_folder_is_reported_and_nothing_is_written()
    {
        await ImportAsync("saga.cbz");
        await ConfigureAsync(c => c with { Folder = _temp.Combine("unplugged") });

        Assert.Equal(DestinationHealth.Unavailable, Destination.GetStatus().Health);
        _service.Run(await _service.PlanAsync(DestinationId, Ct));
        await _service.WaitUntilIdleAsync(Ct);

        Assert.Null(_service.LastResult(DestinationId));
        Assert.False(Directory.Exists(_temp.Combine("unplugged")));
    }
}
