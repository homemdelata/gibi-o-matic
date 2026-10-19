using GibiOMatic.Conversion;
using GibiOMatic.Conversion.Kcc;
using GibiOMatic.Core;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Core.Settings;
using GibiOMatic.Destinations.Kindle;
using GibiOMatic.Destinations.Sync;
using GibiOMatic.Library;
using GibiOMatic.Library.Editing;
using GibiOMatic.Library.Import;
using GibiOMatic.Library.Index;
using Microsoft.Extensions.Logging.Abstractions;

namespace GibiOMatic.Destinations.Tests;

/// <summary>Writes a small "MOBI" holding what it was asked for, instead of running the KCC.</summary>
public sealed class FakeKcc : IKccRunner
{
    public List<KccJob> Jobs { get; } = [];

    public Func<KccJob, Exception?> Fail { get; set; } = _ => null;

    public async Task<string> ConvertAsync(KccJob job, IProgress<double>? progress, CancellationToken ct)
    {
        lock (Jobs)
        {
            Jobs.Add(job);
        }

        if (Fail(job) is { } error)
        {
            throw error;
        }

        Assert.True(File.Exists(job.Input), "the KCC gets a CBZ");
        Directory.CreateDirectory(job.OutputDirectory);
        var output = Path.Combine(job.OutputDirectory, Path.GetFileNameWithoutExtension(job.Input) + ".mobi");
        await File.WriteAllTextAsync(output, $"{job.Title}|{job.Author}|{job.Options.Profile}|{job.Options.Color}", ct);
        progress?.Report(1);
        return output;
    }
}

/// <summary>A Kindle in memory: its documents folder and what was deleted from it.</summary>
public sealed class FakeKindle : IKindleDevices, IKindleConnection
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> NotesFolders { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Deleted { get; } = [];

    public bool Connected { get; set; } = true;

    public bool IsSupported => true;

    public string Name => "Kindle Colorsoft";

    public IReadOnlyList<KindleDeviceInfo> Find() => Connected ? [new KindleDeviceInfo("usb-1", Name)] : [];

    public IKindleConnection Connect(string deviceId) => Connected ? this : throw new KindleDeviceException("unplugged");

    public KindleSpace? Space() => new(9_800_000_000, 16_000_000_000);

    public IReadOnlyList<KindleFile> ListBooks() => Files.Keys.Order().Select(f => new KindleFile(f, Files[f].Length, null)).ToList();

    public bool Exists(string path) => Files.ContainsKey(path);

    public void Upload(string localFile, string path, IProgress<double>? progress, CancellationToken ct)
    {
        Files[KindleBooks.Checked(path)] = File.ReadAllText(localFile);
        progress?.Report(1);
    }

    public void Delete(string path)
    {
        Deleted.Add(path);
        Files.Remove(path);
        NotesFolders.Remove(KindleBooks.NotesFolderOf(path));
    }

    public void Dispose()
    {
    }
}

public sealed class KindleTests : IAsyncLifetime
{
    private const string DestinationId = "01ARYZ6S41TSV4RRFFQ69GKNDL";

    private readonly TempDirectory _temp = new();
    private readonly AppPaths _paths;
    private readonly LibraryService _libraries;
    private readonly JsonSettingsService _settings;
    private readonly BookEditor _editor;
    private readonly ImportQueueFactory _imports;
    private readonly FakeKcc _kcc = new();
    private readonly FakeKindle _kindle = new();
    private readonly KindleDestinationType _type;
    private LibraryInfo _library = null!;
    private DestinationService _service = null!;

    public KindleTests()
    {
        _paths = new AppPaths(_temp.Combine("config"), _temp.Combine("data"));
        _libraries = new LibraryService(_paths, NullLogger<LibraryService>.Instance);
        _settings = new JsonSettingsService(_paths, NullLogger<JsonSettingsService>.Instance);
        var reader = new BookFileReader(NullLogger<BookFileReader>.Instance);
        var thumbnails = new ThumbnailCache(_paths, NullLogger<ThumbnailCache>.Instance);
        _editor = new BookEditor(_libraries, reader, NullLogger<BookEditor>.Instance);
        _imports = new ImportQueueFactory(_libraries, new ImportAnalyzer(reader, thumbnails),
            new BookImporter(reader, NullLogger<BookImporter>.Instance), thumbnails, _settings, NullLogger<ImportQueue>.Instance);
        _type = new KindleDestinationType(_kcc, _paths, NullLoggerFactory.Instance);
    }

    public async ValueTask InitializeAsync()
    {
        _library = await _libraries.CreateAsync(_temp.Combine("Arquivo"), Ct);
        _service = new DestinationServiceFactory(_libraries, [_type], _settings, NullLogger<DestinationService>.Instance)
        {
            GatherDelay = TimeSpan.FromMilliseconds(20),
        }.Create(_library);
        _service.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync();
        _temp.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private KindleDestination Destination => (KindleDestination)_service.Find(DestinationId)!;

    private string KccTool
    {
        get
        {
            var path = _temp.Combine("kcc", "kcc-c2e.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fake");
            return path;
        }
    }

    private async Task ChooseAsync(params string[] bookIds)
    {
        var config = _settings.Current.Destinations.FirstOrDefault()
                     ?? _type.CreateDefault(DestinationId, "Meu Kindle") with { KccPath = KccTool };
        await _settings.SaveAsync(_settings.Current with { Destinations = [config with { ChosenBookIds = bookIds }] }, Ct);
        await _service.MarkConfirmedAsync(DestinationId, Ct);
    }

    private async Task PrepareAsync(params string[] changed)
    {
        foreach (var id in changed)
        {
            _service.OnBookChanged(id);
        }

        await _service.WaitUntilIdleAsync(Ct);
    }

    private async Task<CatalogBook> ImportAsync(string fileName, string series, string number)
    {
        await using var imports = _imports.Create(_library);
        await imports.StartAsync(Ct);
        var source = SyntheticBooks.Cbz(_temp.Combine("Downloads", fileName), pages: 2 + number.Length,
            comicInfoXml: SyntheticBooks.ComicInfo(series: series, number: number, year: 2012, writer: "Brian K. Vaughan", publisher: "Image Comics"));
        await imports.AddAsync([source], Ct);
        await imports.WaitUntilIdleAsync(Ct);
        await imports.ConfirmAsync(Ct);
        await imports.WaitUntilIdleAsync(Ct);
        return (await _libraries.GetIndex(_library).GetBooksAsync(Ct)).Single(b => b.Metadata.Files.Any(f => f.DownloadedAs == fileName));
    }

    private string Prepared(string name) => Path.Combine(Destination.Config.Folder!, name);

    [Fact]
    public async Task Chosen_books_are_prepared_without_the_device_and_sent_when_the_user_confirms()
    {
        var saga = await ImportAsync("saga.cbz", "Saga", "1");
        var other = await ImportAsync("paper.cbz", "Paper Girls", "1");
        await ChooseAsync(saga.Metadata.Id);

        await PrepareAsync(saga.Metadata.Id);

        // Prepared with the Colorsoft profile and colour; books not chosen are left alone.
        var job = Assert.Single(_kcc.Jobs);
        Assert.Equal(("KCS", true, "Brian K. Vaughan"), (job.Options.Profile, job.Options.Color, job.Author));
        Assert.True(File.Exists(Prepared("Saga v01 (2012).mobi")));
        var status = await _service.Kindle.StatusAsync(Destination, Ct);
        Assert.Equal(KindleBookState.Ready, status[saga.Metadata.Id].State);
        Assert.False(status.ContainsKey(other.Metadata.Id));

        // Sending: the plan first, then the copy; the prepared file is deleted afterwards.
        var plan = await _service.Kindle.PlanAsync(Destination, _kindle, Ct);
        var item = Assert.Single(plan.ToSend);
        Assert.Equal((KindleSendKind.New, "Saga v01 (2012).mobi"), (item.Kind, item.DeviceFile));
        var result = await _service.Kindle.SendAsync(Destination, plan, _kindle, null, Ct);

        Assert.Equal(1, result.Sent);
        Assert.StartsWith("Saga, Vol. 1|", _kindle.Files["Saga v01 (2012).mobi"]);
        Assert.False(File.Exists(Prepared("Saga v01 (2012).mobi")));
        Assert.Equal(KindleBookState.Sent, (await _service.Kindle.StatusAsync(Destination, Ct))[saga.Metadata.Id].State);
        Assert.Contains(await _service.HistoryAsync(DestinationId, 10, Ct), e => e.Kind == DestinationLogKind.Sent);

        // Sent counts as done: nothing is prepared again, nothing more to send.
        await PrepareAsync(saga.Metadata.Id);
        Assert.Single(_kcc.Jobs);
        Assert.Empty((await _service.Kindle.PlanAsync(Destination, _kindle, Ct)).Items);
    }

    [Fact]
    public async Task A_changed_book_is_prepared_again_and_replaces_only_the_file_the_app_sent()
    {
        var saga = await ImportAsync("saga.cbz", "Saga", "1");
        await ChooseAsync(saga.Metadata.Id);
        await PrepareAsync(saga.Metadata.Id);
        await _service.Kindle.SendAsync(Destination, await _service.Kindle.PlanAsync(Destination, _kindle, Ct), _kindle, null, Ct);
        _kindle.NotesFolders.Add("Saga v01 (2012).sdr");

        await _editor.UpdateAsync(_library, saga.Metadata.Id, m => m with { Title = "Saga, Volume One" }, Ct);
        await PrepareAsync(saga.Metadata.Id);

        Assert.Equal(KindleBookState.ReadyToReplace, (await _service.Kindle.StatusAsync(Destination, Ct))[saga.Metadata.Id].State);
        var plan = await _service.Kindle.PlanAsync(Destination, _kindle, Ct);
        Assert.Equal(KindleSendKind.Replace, Assert.Single(plan.ToSend).Kind);
        await _service.Kindle.SendAsync(Destination, plan, _kindle, null, Ct);
        Assert.StartsWith("Saga, Volume One|", _kindle.Files["Saga v01 (2012).mobi"]);

        // Replacing keeps the Kindle's notes and reading position, and deletes nothing else.
        Assert.Contains("Saga v01 (2012).sdr", _kindle.NotesFolders);
        Assert.Empty(_kindle.Deleted);
    }

    [Fact]
    public async Task A_file_the_app_did_not_send_is_never_overwritten()
    {
        var saga = await ImportAsync("saga.cbz", "Saga", "1");
        await ChooseAsync(saga.Metadata.Id);
        await PrepareAsync(saga.Metadata.Id);
        _kindle.Files["Saga v01 (2012).mobi"] = "the user's own copy";

        var plan = await _service.Kindle.PlanAsync(Destination, _kindle, Ct);

        Assert.Equal(KindleSendKind.Blocked, Assert.Single(plan.Items).Kind);
        Assert.Empty(plan.ToSend);
        await _service.Kindle.SendAsync(Destination, plan, _kindle, null, Ct);
        Assert.Equal("the user's own copy", _kindle.Files["Saga v01 (2012).mobi"]);
    }

    [Fact]
    public async Task Leaving_the_list_deletes_the_prepared_copy_but_never_touches_the_device()
    {
        var saga = await ImportAsync("saga.cbz", "Saga", "1");
        var paper = await ImportAsync("paper.cbz", "Paper Girls", "1");
        await ChooseAsync(saga.Metadata.Id, paper.Metadata.Id);
        await PrepareAsync(saga.Metadata.Id, paper.Metadata.Id);
        var plan = await _service.Kindle.PlanAsync(Destination, _kindle, Ct);
        await _service.Kindle.SendAsync(Destination, plan with { Items = plan.Items.Where(i => i.BookId == saga.Metadata.Id).ToList() }, _kindle, null, Ct);

        await ChooseAsync();
        await PrepareAsync(saga.Metadata.Id, paper.Metadata.Id);

        Assert.False(File.Exists(Prepared("Paper Girls v01 (2012).mobi")));
        Assert.True(_kindle.Files.ContainsKey("Saga v01 (2012).mobi"));
        Assert.Empty(_kindle.Deleted);
    }

    [Fact]
    public async Task The_manager_deletes_only_the_files_picked_with_their_notes()
    {
        var saga = await ImportAsync("saga.cbz", "Saga", "1");
        await ChooseAsync(saga.Metadata.Id);
        await PrepareAsync(saga.Metadata.Id);
        await _service.Kindle.SendAsync(Destination, await _service.Kindle.PlanAsync(Destination, _kindle, Ct), _kindle, null, Ct);
        _kindle.Files["Dicionário.azw3"] = "the user's";
        _kindle.NotesFolders.Add("Saga v01 (2012).sdr");
        Assert.Equal(["Saga v01 (2012).mobi"], KindleSender.SentFiles(Destination));

        var books = await _service.Kindle.DeleteAsync(Destination, _kindle, [new KindleFile("Saga v01 (2012).mobi", 1, null)], Ct);

        Assert.Equal([saga.Metadata.Id], books);
        Assert.Equal(["Dicionário.azw3"], _kindle.Files.Keys);
        Assert.Empty(_kindle.NotesFolders);
        Assert.Empty(KindleSender.SentFiles(Destination));
        Assert.Contains(await _service.HistoryAsync(DestinationId, 10, Ct), e => e.Kind == DestinationLogKind.RemovedFromDevice);
    }

    [Fact]
    public async Task Without_the_kcc_nothing_is_prepared_and_a_failed_conversion_is_in_the_history()
    {
        var saga = await ImportAsync("saga.cbz", "Saga", "1");
        await _settings.SaveAsync(_settings.Current with
        {
            Destinations = [_type.CreateDefault(DestinationId, "Meu Kindle") with { KccPath = _temp.Combine("missing.exe"), ChosenBookIds = [saga.Metadata.Id] }],
        }, Ct);
        await _service.MarkConfirmedAsync(DestinationId, Ct);

        Assert.Equal(DestinationHealth.NotConfigured, Destination.GetStatus().Health);
        await PrepareAsync(saga.Metadata.Id);
        Assert.Empty(_kcc.Jobs);

        await _settings.SaveAsync(_settings.Current with { Destinations = [Destination.Config with { KccPath = KccTool }] }, Ct);
        _kcc.Fail = _ => new KccException("KCC did not write the book (exit code 0): Error: KindleGen failed to create MOBI!");
        await PrepareAsync(saga.Metadata.Id);

        Assert.Single(_kcc.Jobs);
        Assert.Equal(KindleBookState.Waiting, (await _service.Kindle.StatusAsync(Destination, Ct))[saga.Metadata.Id].State);
        var failed = Assert.Single(await _service.HistoryAsync(DestinationId, 10, Ct), e => e.Kind == DestinationLogKind.Failed);
        Assert.Contains("KindleGen", failed.Message);
        // The work folder of the failed conversion was cleaned up.
        var work = Path.Combine(Destination.Config.Folder!, ".work");
        Assert.True(!Directory.Exists(work) || !Directory.EnumerateFileSystemEntries(work).Any());
    }

    /// <summary>A destination whose copies take as long as the test wants (a Komga converting big PDFs).</summary>
    private sealed class SlowType : IDestinationType
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "slow";

        public IReadOnlySet<BookKind> AcceptedKinds { get; } = new HashSet<BookKind> { BookKind.Tpb, BookKind.Issue };

        public DestinationConfig CreateDefault(string id, string name) => new() { Id = id, Type = Id, Name = name, Folder = Path.GetTempPath() };

        public IDestination Create(DestinationConfig config) => new Slow(config, this);

        private sealed class Slow(DestinationConfig config, SlowType type) : IDestination
        {
            public DestinationConfig Config { get; } = config;
            public DestinationStatus GetStatus() => new(DestinationHealth.Ready);
            public bool Includes(BookMetadata book) => true;
            public string OutputPath(CatalogBook book) => $"slow-{book.Metadata.Id}.bin";
            public string ContentSignature(CatalogBook book) => "1";

            public async Task GenerateAsync(LibraryInfo library, CatalogBook book, string target, IProgress<double>? progress, CancellationToken ct)
            {
                type.Started.TrySetResult();
                await type.Release.Task.WaitAsync(ct);
                await File.WriteAllTextAsync(target, "x", ct);
            }

            public Task<AfterSyncResult?> AfterSyncAsync(CancellationToken ct) => Task.FromResult<AfterSyncResult?>(null);
        }
    }

    [Fact]
    public async Task The_kindle_prepares_while_another_destination_is_busy_with_a_long_run()
    {
        var slow = new SlowType();
        await using var service = new DestinationServiceFactory(_libraries, [_type, slow], _settings, NullLogger<DestinationService>.Instance)
        {
            GatherDelay = TimeSpan.FromMilliseconds(20),
        }.Create(_library);
        service.Start();
        var saga = await ImportAsync("saga.cbz", "Saga", "1");
        var folder = _temp.Combine("slow");
        Directory.CreateDirectory(folder);
        await _settings.SaveAsync(_settings.Current with
        {
            Destinations =
            [
                slow.CreateDefault("01ARYZ6S41TSV4RRFFQ69GSLOW", "Komga lento") with { Folder = folder },
                _type.CreateDefault(DestinationId, "Meu Kindle") with { KccPath = KccTool, ChosenBookIds = [saga.Metadata.Id] },
            ],
        }, Ct);
        await service.MarkConfirmedAsync("01ARYZ6S41TSV4RRFFQ69GSLOW", Ct);
        await service.MarkConfirmedAsync(DestinationId, Ct);

        service.OnBookChanged(saga.Metadata.Id);
        await slow.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // The slow destination is still busy, and the Kindle's copy is ready anyway.
        var kindle = (KindleDestination)service.Find(DestinationId)!;
        for (var i = 0; i < 200 && (await service.Kindle.StatusAsync(kindle, Ct))[saga.Metadata.Id].State != KindleBookState.Ready; i++)
        {
            await Task.Delay(25, Ct);
        }

        Assert.Equal(KindleBookState.Ready, (await service.Kindle.StatusAsync(kindle, Ct))[saga.Metadata.Id].State);
        Assert.True(service.IsBusyOf("01ARYZ6S41TSV4RRFFQ69GSLOW"));
        Assert.Equal("Saga, Vol. 1", service.CurrentOf("01ARYZ6S41TSV4RRFFQ69GSLOW")?.Title);

        slow.Release.SetResult();
        await service.WaitUntilIdleAsync(Ct);
        Assert.True(File.Exists(Path.Combine(folder, $"slow-{saga.Metadata.Id}.bin")));
    }

    [Fact]
    public void Text_books_never_go_to_the_kindle()
    {
        var destination = (KindleDestination)_type.Create(_type.CreateDefault(DestinationId, "Kindle") with { ChosenBookIds = ["a", "b"] });
        BookMetadata Book(string id, BookKind kind) => new()
        {
            Id = id, Title = id, Kind = kind, Files = [new BookFile { Name = "x.cbz", Sha256 = "0", Format = BookFormat.Cbz }],
        };

        Assert.True(destination.Includes(Book("a", BookKind.Tpb)));
        Assert.False(destination.Includes(Book("b", BookKind.Book)));
        Assert.False(destination.Includes(Book("c", BookKind.Tpb)));
        Assert.Equal("Saga v01 (2012).mobi", OutputNames.Kindle(new BookMetadata
        {
            Id = "x", Title = "Saga, Vol. 1", Series = new SeriesInfo("Saga", "1"), Date = new PartialDate(2012),
        }));
    }
}
