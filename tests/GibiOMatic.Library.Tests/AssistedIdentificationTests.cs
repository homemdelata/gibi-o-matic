using System.Text.Json;
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
using GibiOMatic.Metadata.Assisted;
using GibiOMatic.Metadata.Http;
using GibiOMatic.Metadata.Identification;
using GibiOMatic.Metadata.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace GibiOMatic.Library.Tests;

/// <summary>Claude Code as the app sees it: a status and a recorded answer per run; it records what each run was given.</summary>
internal sealed class FakeClaude : IClaudeCode
{
    public ClaudeStatus Status { get; set; } = new(true, "2.1.316", true, "claude.ai", "claude");

    public Func<ClaudeRun, string?> Answer { get; set; } = _ => null;

    public List<ClaudeRun> Runs { get; } = [];

    public Task<ClaudeStatus> GetStatusAsync(CancellationToken ct) => Task.FromResult(Status);

    public Task<ClaudeRunResult> RunAsync(ClaudeRun run, CancellationToken ct)
    {
        Runs.Add(run);
        var output = Answer(run);
        return Task.FromResult(output is null ? ClaudeRunResult.Failed("no-answer", TimeSpan.FromSeconds(1)) : new ClaudeRunResult(output, null, 12, TimeSpan.FromSeconds(20)));
    }
}

/// <summary>Only the volumes Claude may name.</summary>
internal sealed class VolumesOnly : IComicVineApi
{
    public Task<bool> HasKeyAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<IReadOnlyList<CvVolume>> SearchVolumesAsync(string query, CancellationToken ct, int limit = 30) => Task.FromResult<IReadOnlyList<CvVolume>>([]);

    public Task<IReadOnlyList<CvIssueSummary>> GetIssuesAsync(IReadOnlyCollection<int> volumeIds, CancellationToken ct, string? issueNumber = null) =>
        Task.FromResult<IReadOnlyList<CvIssueSummary>>([]);

    public Task<CvIssue?> GetIssueAsync(int id, CancellationToken ct) => Task.FromResult<CvIssue?>(null);

    public Task<CvVolume?> GetVolumeAsync(int id, CancellationToken ct) =>
        Task.FromResult<CvVolume?>(id == 4242 ? new CvVolume(4242, "Snotgirl", 2016, "Image", 20) : null);
}

/// <summary>Covers that never match the books' (a drawing, where the books' are a plain colour).</summary>
internal sealed class OtherCovers : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var surface = SKSurface.Create(new SKImageInfo(200, 300));
        surface.Canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Black };
        for (var x = 0; x < 200; x += 40)
        {
            surface.Canvas.DrawRect(x, 0, 20, 300, paint);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(data.ToArray()) { Headers = { ContentType = new("image/jpeg") } },
        });
    }
}

/// <summary>Assisted identification (SPEC §8.4) on a synthetic library, with a fake Claude Code.</summary>
public sealed class AssistedIdentificationTests : IAsyncLifetime
{
    private readonly TempDirectory _temp = new();
    private readonly AppPaths _paths;
    private readonly LibraryService _libraries;
    private readonly JsonSettingsService _settings;
    private readonly BookEditor _editor;
    private readonly FakeIdentifier _identifier = new();
    private readonly FakeClaude _claude = new();
    private readonly ImportQueueFactory _imports;
    private LibraryInfo _library = null!;
    private IdentificationQueue _queue = null!;
    private AssistedQueue _assisted = null!;

    public AssistedIdentificationTests()
    {
        _paths = new AppPaths(_temp.Combine("config"), _temp.Combine("data"));
        _libraries = new LibraryService(_paths, NullLogger<LibraryService>.Instance);
        _settings = new JsonSettingsService(_paths, NullLogger<JsonSettingsService>.Instance);
        var reader = new BookFileReader(NullLogger<BookFileReader>.Instance);
        var thumbnails = new ThumbnailCache(_paths, NullLogger<ThumbnailCache>.Instance);
        _editor = new BookEditor(_libraries, reader, NullLogger<BookEditor>.Instance);
        _imports = new ImportQueueFactory(_libraries, new ImportAnalyzer(reader, thumbnails),
            new BookImporter(reader, NullLogger<BookImporter>.Instance), thumbnails, _settings, new FakeRecycleBin(_temp.Combine("Lixeira")),
            NullLogger<ImportQueue>.Instance);
    }

    public async ValueTask InitializeAsync()
    {
        _library = await _libraries.CreateAsync(_temp.Combine("Library"), Ct);
        await _settings.SaveAsync(new AppSettings { Claude = new ClaudeSettings { Enabled = true } }, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_assisted is not null)
        {
            await _assisted.DisposeAsync();
        }

        if (_queue is not null)
        {
            await _queue.DisposeAsync();
        }

        _temp.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private LibraryIndex Index => _libraries.GetIndex(_library);

    /// <summary>Imports synthetic PDFs (each with another page count), identifies them, and starts the Claude queue.</summary>
    private async Task<Dictionary<string, string>> StartAsync(HttpMessageHandler? covers = null, params string[] fileNames)
    {
        await using (var imports = _imports.Create(_library))
        {
            await imports.StartAsync(Ct);
            var pages = 4;
            await imports.AddAsync(fileNames.Select(f => SyntheticBooks.Pdf(_temp.Combine("Downloads", f), new SyntheticBooks.PdfOptions { Pages = pages++ })).ToList(), Ct);
            await imports.WaitUntilIdleAsync(Ct);
            await imports.ConfirmAsync(Ct);
            await imports.WaitUntilIdleAsync(Ct);
        }

        var coverCache = new CoverImageCache(new HttpClient(covers ?? new LimeCovers()), _temp.Combine("covers"), NullLogger<CoverImageCache>.Instance);
        _queue = new IdentificationQueueFactory(_libraries, _identifier, _editor, coverCache, _settings, NullLogger<IdentificationQueue>.Instance).Create(_library);
        await _queue.StartAsync(Ct);
        await _queue.WaitUntilIdleAsync(Ct);
        var identifier = new AssistedIdentifier(_claude, _identifier, new VolumesOnly(), coverCache, _settings, _paths,
            new ClaudeBridge("GibiOMatic.exe", []), NullLogger<AssistedIdentifier>.Instance);
        _assisted = new AssistedQueue(_library, Index, _queue, identifier, _settings, NullLogger<AssistedQueue>.Instance);
        _assisted.Start();
        return (await Index.GetBooksAsync(Ct)).ToDictionary(b => b.Metadata.Files[0].DownloadedAs!, b => b.Metadata.Id);
    }

    private static string Json(object answer) => JsonSerializer.Serialize(answer, Core.Json.GibiJson.Options);

    private static readonly string FoundSaga = Json(new BookAnswer
    {
        Identified = true,
        Title = "Saga, Vol. 1",
        ComicVineUrl = "https://comicvine.gamespot.com/saga-1-volume-one/4000-1/",
        Confidence = 0.97,
        Evidence = ["The credits page says Saga Volume One, Image Comics, 2012."],
        Isbn = "9781607066019",
    });

    [Fact]
    public async Task A_quick_answer_is_checked_stored_and_joins_the_books_candidates()
    {
        var ids = await StartAsync(null, "BTMMFDRS.pdf");
        var bookId = ids["BTMMFDRS.pdf"];
        _claude.Answer = _ => FoundSaga;

        var result = await _assisted.RunBookAsync(bookId, AssistedLevel.Quick, Ct);

        // What Claude was given: the book's folder, ComicVine through the app, the site guard.
        var run = Assert.Single(_claude.Runs);
        Assert.Equal("haiku", run.Model);
        Assert.Equal("low", run.Effort);
        Assert.True(File.Exists(Path.Combine(run.Folder, "cover.jpg")));
        Assert.True(File.Exists(Path.Combine(run.Folder, "page-002.jpg")));
        Assert.Contains("\"page-002.jpg\"", await File.ReadAllTextAsync(Path.Combine(run.Folder, "book.json"), Ct));
        Assert.Contains("--claude-mcp", await File.ReadAllTextAsync(run.McpConfigPath, Ct));
        Assert.Contains("--claude-hook", await File.ReadAllTextAsync(run.SettingsPath, Ct));
        Assert.Contains("amazon.com", await File.ReadAllLinesAsync(Path.Combine(run.Folder, SiteGuard.SitesFile), Ct));

        // The record was read again by its id and the covers compared (both plain colours: the same image).
        Assert.Equal(AssistedOutcome.Found, result!.Outcome);
        Assert.Equal("4000-1", result.Candidate!.Record.ComicVineId);
        Assert.True(result.Candidate.Assisted!.CoverMatches);
        Assert.Equal(0.97, result.Candidate.Confidence);

        var identification = await _queue.GetAsync(bookId, Ct);
        Assert.Equal(IdentificationState.NeedsReview, identification!.State);
        Assert.NotNull(identification.Result!.Candidates[0].Assisted);

        // A new search of the algorithm keeps Claude's answer.
        await _queue.SearchAgainAsync(bookId, "other words", Ct);
        Assert.NotNull((await _queue.GetAsync(bookId, Ct))!.Result!.Candidates.Single().Assisted);
    }

    [Fact]
    public async Task A_record_with_another_cover_is_kept_with_a_warning()
    {
        var ids = await StartAsync(new OtherCovers(), "saga.pdf");
        _claude.Answer = _ => FoundSaga;

        var result = await _assisted.RunBookAsync(ids["saga.pdf"], AssistedLevel.Quick, Ct);

        Assert.False(result!.Candidate!.Assisted!.CoverMatches);
    }

    [Fact]
    public async Task An_edition_missing_from_comicvine_gets_a_record_from_the_answer()
    {
        var ids = await StartAsync(null, "snotgirl_v4.pdf");
        _claude.Answer = _ => Json(new BookAnswer
        {
            Identified = true,
            Title = "Snotgirl, Vol. 4: Bitch Factor",
            Series = "Snotgirl",
            Number = "4",
            Kind = "tpb",
            ComicVineSeriesUrl = "https://comicvine.gamespot.com/snotgirl/4050-4242/",
            Isbn = "978-1-5343-2991-1",
            Credits = [new AnswerCredit("Writer", "Bryan Lee O'Malley")],
            Sources = new Dictionary<string, string> { ["isbn"] = "indicia page-004" },
            Confidence = 0.9,
        });

        var result = await _assisted.RunBookAsync(ids["snotgirl_v4.pdf"], AssistedLevel.Deep, Ct);

        Assert.Equal(AssistedOutcome.EditionMissing, result!.Outcome);
        Assert.Equal("sonnet", _claude.Runs.Single().Model);
        Assert.Equal(SourceIds.Claude, result.Candidate!.Record.Source);
        Assert.Equal(4242, result.Candidate.Record.ComicVineVolumeId);
        Assert.Contains(ProposalField.Writers, result.Candidate.Assisted!.Unticked);
    }

    [Fact]
    public async Task Without_a_login_nothing_runs_and_nothing_is_stored()
    {
        var ids = await StartAsync(null, "saga.pdf");
        _claude.Status = _claude.Status with { LoggedIn = false };

        var result = await _assisted.RunBookAsync(ids["saga.pdf"], AssistedLevel.Quick, Ct);

        Assert.Equal(AssistedOutcome.Failed, result!.Outcome);
        Assert.Equal("not-logged-in", result.Message);
        Assert.Empty(_claude.Runs);
        Assert.Empty(await _assisted.Store.GetAsync(ids["saga.pdf"], Ct));
    }

    [Fact]
    public async Task With_automatic_approval_on_a_checked_answer_that_agrees_with_the_algorithm_approves_the_book()
    {
        _identifier.Respond = (book, _) => new IdentificationResult(BookQuery.From(book), [FakeIdentifier.Saga(0.6, MatchReason.Title)], []);
        var ids = await StartAsync(null, "saga.pdf");
        await _settings.SaveAsync(_settings.Current with { Claude = _settings.Current.Claude with { AutoApprove = true } }, Ct);
        _claude.Answer = _ => FoundSaga;

        await _assisted.RunBookAsync(ids["saga.pdf"], AssistedLevel.Quick, Ct);

        var book = await Index.GetBookAsync(ids["saga.pdf"], Ct);
        Assert.Equal(ReviewStatus.Approved, book!.Metadata.Review.Status);
        Assert.Equal(MatchedBy.ClaudeAuto, book.Metadata.Review.MatchedBy);
        Assert.Equal(IdentificationState.AutoApproved, (await _queue.GetAsync(ids["saga.pdf"], Ct))!.State);
    }

    [Fact]
    public async Task Automatic_approval_stays_off_by_default()
    {
        _identifier.Respond = (book, _) => new IdentificationResult(BookQuery.From(book), [FakeIdentifier.Saga(0.6, MatchReason.Title)], []);
        var ids = await StartAsync(null, "saga.pdf");
        _claude.Answer = _ => FoundSaga;

        await _assisted.RunBookAsync(ids["saga.pdf"], AssistedLevel.Quick, Ct);

        Assert.Equal(ReviewStatus.Pending, (await Index.GetBookAsync(ids["saga.pdf"], Ct))!.Metadata.Review.Status);
    }

    [Fact]
    public async Task In_the_automatic_mode_every_book_that_goes_to_the_review_is_asked_once()
    {
        await _settings.SaveAsync(_settings.Current with { Claude = _settings.Current.Claude with { When = ClaudeRunMode.Automatic } }, Ct);
        _claude.Answer = _ => FoundSaga;
        var ids = await StartAsync(null, "saga.pdf");

        // The book was identified before the Claude queue existed: identifying it again sends it to Claude.
        await _queue.EnqueueAsync([ids["saga.pdf"]], force: true, Ct);
        await _queue.WaitUntilIdleAsync(Ct);
        await WaitForAsync(() => _claude.Runs.Count == 1 && !_assisted.IsBusy);
        await WaitForAsync(async () => (await _assisted.Store.GetAsync(ids["saga.pdf"], Ct)).Count == 1);

        await _queue.EnqueueAsync([ids["saga.pdf"]], force: true, Ct);
        await _queue.WaitUntilIdleAsync(Ct);
        await Task.Delay(200, Ct);
        Assert.Single(_claude.Runs);
    }

    [Fact]
    public async Task A_batch_runs_one_book_at_a_time_and_can_be_stopped()
    {
        var ids = await StartAsync(null, "a.pdf", "b.pdf", "c.pdf");
        var gate = new TaskCompletionSource();
        _claude.Answer = _ =>
        {
            gate.Task.Wait(TimeSpan.FromSeconds(10));
            return FoundSaga;
        };

        _assisted.Enqueue(ids.Values);
        await WaitForAsync(() => _claude.Runs.Count == 1);
        _assisted.CancelBatch();
        gate.SetResult();
        await WaitForAsync(() => !_assisted.IsBusy);

        Assert.Single(_claude.Runs);
    }

    [Fact]
    public async Task A_batch_skips_the_books_approved_while_they_waited_and_a_book_asked_by_hand_leaves_it()
    {
        var ids = await StartAsync(null, "a.pdf", "b.pdf", "c.pdf");
        var gate = new TaskCompletionSource();
        _claude.Answer = _ =>
        {
            gate.Task.Wait(TimeSpan.FromSeconds(10));
            return FoundSaga;
        };

        _assisted.Enqueue([ids["a.pdf"], ids["b.pdf"], ids["c.pdf"]]);
        await WaitForAsync(() => _claude.Runs.Count == 1);
        Assert.Equal(ids["a.pdf"], _assisted.CurrentBookId);
        Assert.True(_assisted.IsBatchRunning);
        Assert.True(_assisted.IsQueued(bookId: ids["b.pdf"]));

        // b is approved while it waits; c is asked by hand, so the batch doesn't ask about it again.
        await _queue.ApproveAsync(ids["b.pdf"], FakeIdentifier.Saga(0.9), new HashSet<ProposalField>(), Ct);
        var byHand = _assisted.RunBookAsync(ids["c.pdf"], AssistedLevel.Quick, Ct);
        Assert.False(_assisted.IsQueued(bookId: ids["c.pdf"]));

        gate.SetResult();
        await byHand;
        await WaitForAsync(() => !_assisted.IsBusy && !_assisted.HasBatch);

        bool Asked(string file) => _claude.Runs.Any(r => r.Folder.EndsWith(ids[file][^12..], StringComparison.Ordinal));
        Assert.True(Asked("a.pdf"));
        Assert.False(Asked("b.pdf"));
        Assert.Single(_claude.Runs, r => r.Folder.EndsWith(ids["c.pdf"][^12..], StringComparison.Ordinal) && r.Folder.Contains("-quick-", StringComparison.Ordinal));
        Assert.Null(_assisted.CurrentBookId);
    }

    [Fact]
    public async Task A_batch_of_series_asks_once_per_group_about_the_books_still_in_the_review()
    {
        var ids = await StartAsync(null, "The Boys Vol. 1.pdf", "The Boys Vol. 2.pdf", "The Boys Vol. 3.pdf");
        var group = Assert.Single((await new ReviewGroups(Index, _queue).GetAsync(Ct)).Groups);
        var (v1, v3) = (ids["The Boys Vol. 1.pdf"], ids["The Boys Vol. 3.pdf"]);
        await _queue.ApproveAsync(v3, FakeIdentifier.Saga(0.9), new HashSet<ProposalField>(), Ct);
        _claude.Answer = run => Json(new GroupAnswer
        {
            Series = new GroupAnswerSeries { Name = "The Boys", Publisher = "Dynamite", StartYear = 2006 },
            Books = [new GroupAnswerBook { Id = v1, Number = "1", Confidence = 0.5 }],
            MissingNumbers = [],
        });

        Assert.Equal(1, _assisted.EnqueueGroups([group, group]));
        await WaitForAsync(async () => await _assisted.Store.GetGroupAsync(group.Id, Ct) is not null);
        await WaitForAsync(() => !_assisted.IsBusy && !_assisted.HasBatch);

        var run = Assert.Single(_claude.Runs);
        var sent = await File.ReadAllTextAsync(Path.Combine(run.Folder, "group.json"), Ct);
        Assert.Contains(v1, sent);
        Assert.DoesNotContain(v3, sent);
        Assert.Contains(group.Id, await _assisted.Store.GetAnsweredGroupsAsync(Ct));
        Assert.Null(_assisted.CurrentGroupId);
    }

    [Fact]
    public async Task A_group_run_fills_the_rows_that_pass_the_checks_and_points_out_the_outsiders()
    {
        var ids = await StartAsync(null, "The Boys Vol. 1.pdf", "The Boys Vol. 2.pdf", "The Boys - Dear Becky 1.pdf");
        var groups = new ReviewGroups(Index, _queue);
        var group = Assert.Single((await groups.GetAsync(Ct)).Groups);
        var (v1, v2, becky) = (ids["The Boys Vol. 1.pdf"], ids["The Boys Vol. 2.pdf"], ids["The Boys - Dear Becky 1.pdf"]);
        _claude.Answer = run => Json(new GroupAnswer
        {
            Series = new GroupAnswerSeries { Name = "The Boys", Publisher = "Dynamite", StartYear = 2006 },
            Pattern = "Each trade is its own one-issue volume.",
            Books =
            [
                new GroupAnswerBook { Id = v1, Number = "1", ComicVineIssueUrl = "https://comicvine.gamespot.com/x/4000-1/", Confidence = 0.9, Evidence = ["title"] },
                new GroupAnswerBook { Id = v2, Number = "2", ComicVineIssueUrl = null, Confidence = 0.4, Doubts = "Not on ComicVine." },
                new GroupAnswerBook { Id = becky, Belongs = false, ComicVineIssueUrl = "https://comicvine.gamespot.com/x/4000-1/", Confidence = 0.9 },
                new GroupAnswerBook { Id = "not-in-the-group", Number = "9", Confidence = 1 },
            ],
            NotInGroup = [new GroupAnswerOutsider { Id = becky, Reason = "A separate mini-series.", SuggestedSeries = "The Boys: Dear Becky" }],
            MissingNumbers = [],
        });

        var result = await _assisted.RunGroupAsync(group, Ct);

        var run = Assert.Single(_claude.Runs);
        Assert.Equal("medium", run.Effort);
        Assert.True(File.Exists(Path.Combine(run.Folder, "group.json")));
        Assert.True(File.Exists(Path.Combine(run.Folder, "covers", v1 + ".jpg")));
        Assert.Equal(AssistedFolders.GroupBooksWithPages, Directory.GetFiles(Path.Combine(run.Folder, "pages")).Select(f => Path.GetFileName(f)[..26]).Distinct().Count());

        Assert.Equal(3, result.Rows.Count);
        Assert.NotNull(result.Rows.Single(r => r.BookId == v1).Candidate);
        Assert.Null(result.Rows.Single(r => r.BookId == v2).Candidate);
        Assert.Null(result.Rows.Single(r => r.BookId == becky).Candidate);
        Assert.Equal("The Boys: Dear Becky", Assert.Single(result.Outsiders).SuggestedSeries);

        // Stored for the group, and the checked row is also a candidate of its book.
        Assert.Equal("Each trade is its own one-issue volume.", (await _assisted.Store.GetGroupAsync(group.Id, Ct))!.Pattern);
        Assert.Equal(AssistedLevel.Group, (await _queue.GetAsync(v1, Ct))!.Result!.Candidates[0].Assisted!.Level);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25, Ct);
        }

        Assert.True(condition());
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200 && !await condition(); i++)
        {
            await Task.Delay(25, Ct);
        }

        Assert.True(await condition());
    }
}
