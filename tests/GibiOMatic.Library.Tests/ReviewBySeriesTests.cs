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

/// <summary>The automatic groups of the Review by series (SPEC §7.1), on synthetic queues.</summary>
public sealed class SeriesGroupingTests
{
    private static int _next;

    private static MetadataCandidate Candidate(int volumeId, string number) => new(new MetadataRecord
    {
        Source = SourceIds.ComicVine,
        Title = $"Volume {volumeId} #{number}",
        Number = number,
        ComicVineId = $"4000-{volumeId}{number}",
        ComicVineVolumeId = volumeId,
    }, 0.7, [MatchReason.Title]);

    private static ReviewGroupBook Book(string title, MetadataCandidate? best = null)
    {
        var id = $"book{Interlocked.Increment(ref _next):D4}";
        var metadata = new BookMetadata { Id = id, Title = title };
        var result = new IdentificationResult(BookQuery.From(metadata), best is null ? [] : [best], []);
        return SeriesGrouping.Describe(new CatalogBook(metadata, "Books/_To review/" + id),
            new BookIdentification(id, best is null ? IdentificationState.NoMatch : IdentificationState.NeedsReview, result, null, false));
    }

    private static readonly Dictionary<string, string?> NoPins = [];

    [Theory]
    [InlineData("Deadly Class, Vol. 3", "deadlyclass")]
    [InlineData("DeadlyClass_v5", "deadlyclass")]
    [InlineData("The Boys Vol. 11 - Over the Hill", "boys")]
    [InlineData("Lá Fora Vol. 1", "lafora")]
    [InlineData("Vol. 3", null)]
    public void The_key_ignores_case_punctuation_accents_the_and_spaces(string title, string? key) =>
        Assert.Equal(key, SeriesGrouping.SeriesKey(SeriesGrouping.Describe(new CatalogBook(new BookMetadata { Id = "x", Title = title }, "f"),
            new BookIdentification("x", IdentificationState.NoMatch, null, null, false)).SeriesName));

    [Fact]
    public void Books_of_one_series_go_together_and_a_one_shot_has_no_group()
    {
        var v3 = Book("Deadly Class, Vol. 3", Candidate(100, "3"));
        var v5 = Book("DeadlyClass_v5", Candidate(100, "5"));
        var plutona = Book("Plutona", Candidate(300, "1"));

        var grouping = SeriesGrouping.Build([plutona, v5, v3], NoPins);

        var group = Assert.Single(grouping.Groups);
        Assert.Equal("Deadly Class", group.Name);
        Assert.Equal("key:deadlyclass", group.Id);
        Assert.Equal([v3.BookId, v5.BookId], group.Books.Select(b => b.BookId));
        Assert.False(group.HasWarnings);
        Assert.Equal([plutona.BookId], grouping.Ungrouped.Select(b => b.BookId));
    }

    [Fact]
    public void Keys_whose_books_share_a_volume_are_merged()
    {
        var a = Book("Deadly Class Vol. 1", Candidate(100, "1"));
        var b = Book("Deadly Class Vol. 2", Candidate(100, "2"));
        var typo = Book("Dedly Clas Vol. 4", Candidate(100, "4"));
        var other = Book("Saga Vol. 1", Candidate(200, "1"));
        var other2 = Book("Saga Vol. 2", Candidate(200, "2"));

        var grouping = SeriesGrouping.Build([a, b, typo, other, other2], NoPins);

        Assert.Equal(2, grouping.Groups.Count);
        var deadly = grouping.Groups.Single(g => g.Id == "key:deadlyclass");
        Assert.Equal(3, deadly.Books.Count);
        Assert.Empty(grouping.Ungrouped);
    }

    [Fact]
    public void Disagreements_are_warned_and_come_first()
    {
        var ok1 = Book("Saga Vol. 1", Candidate(200, "1"));
        var ok2 = Book("Saga Vol. 2", Candidate(200, "2"));
        var a = Book("Wynd Vol. 1", Candidate(100, "1"));
        var b = Book("Wynd Vol. 1", Candidate(101, "1"));
        var c = Book("Wynd Vol. 2", Candidate(100, "2"));
        var d = Book("Wynd - Extra");

        var grouping = SeriesGrouping.Build([ok1, ok2, a, b, c, d], NoPins);

        var wynd = grouping.Groups[0];
        Assert.Equal("Wynd", wynd.Name);
        Assert.Equal(2, wynd.Diverging);
        Assert.True(wynd.NumberRepeated);
        Assert.True(wynd.NumberMissing);
        Assert.False(grouping.Groups[1].HasWarnings);
    }

    [Fact]
    public void The_users_corrections_win_over_the_automatic_grouping()
    {
        var a = Book("Saga Vol. 1", Candidate(200, "1"));
        var b = Book("Saga Vol. 2", Candidate(200, "2"));
        var c = Book("Saga Vol. 3", Candidate(200, "3"));
        var stray = Book("Sagaa Vol 4");
        var lone = Book("Plutona");

        // Removed from its group, a stray book added, and a group created by hand.
        var pins = new Dictionary<string, string?>
        {
            [c.BookId] = null,
            [stray.BookId] = "key:saga",
            [lone.BookId] = "group:1",
        };
        var grouping = SeriesGrouping.Build([a, b, c, stray, lone], pins);

        Assert.Equal([a.BookId, b.BookId, stray.BookId], grouping.Groups.Single(g => g.Id == "key:saga").Books.Select(x => x.BookId).Order());
        Assert.Equal([lone.BookId], grouping.Groups.Single(g => g.Id == "group:1").Books.Select(x => x.BookId));
        Assert.Equal([c.BookId], grouping.Ungrouped.Select(x => x.BookId));
    }
}

/// <summary>Corrections kept in the index, group approval and undoing it, on a synthetic library.</summary>
public sealed class ReviewBySeriesTests : IAsyncLifetime
{
    private readonly TempDirectory _temp = new();
    private readonly LibraryService _libraries;
    private readonly JsonSettingsService _settings;
    private readonly BookEditor _editor;
    private readonly FakeIdentifier _identifier = new();
    private readonly CoverImageCache _covers;
    private readonly ImportQueueFactory _imports;
    private LibraryInfo _library = null!;
    private IdentificationQueue _queue = null!;

    public ReviewBySeriesTests()
    {
        var paths = new AppPaths(_temp.Combine("config"), _temp.Combine("data"));
        _libraries = new LibraryService(paths, NullLogger<LibraryService>.Instance);
        _settings = new JsonSettingsService(paths, NullLogger<JsonSettingsService>.Instance);
        var reader = new BookFileReader(NullLogger<BookFileReader>.Instance);
        var thumbnails = new ThumbnailCache(paths, NullLogger<ThumbnailCache>.Instance);
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

    private static MetadataCandidate Issue(int volumeId, string volumeName, string number, string? issueName = null) => new(new MetadataRecord
    {
        Source = SourceIds.ComicVine,
        Title = $"{volumeName}, Vol. {number}",
        SeriesName = volumeName,
        Number = number,
        Kind = BookKind.Tpb,
        Date = new PartialDate(2014 + int.Parse(number, System.Globalization.CultureInfo.InvariantCulture)),
        Publisher = "Image",
        ComicVineId = $"4000-{volumeId}{number}",
        ComicVineVolumeId = volumeId,
        VolumeName = volumeName,
        IssueName = issueName,
        IsDetailed = false,
    }, 0.7, [MatchReason.Title]);

    /// <summary>Imports synthetic PDFs (each with another page count, so none is a duplicate) and identifies them.</summary>
    private async Task<Dictionary<string, string>> ImportAsync(params string[] fileNames)
    {
        _identifier.Respond = (book, _) => new IdentificationResult(BookQuery.From(book), book.Title switch
        {
            "Deadly Class Vol 1" => [Issue(100, "Deadly Class", "1", "Reagan Youth")],
            "DeadlyClass v2" => [Issue(100, "Deadly Class", "2", "Kids of the Black Hole")],
            "Deadly Class Vol 11" => [Issue(144057, "Deadly Class: A Fond Farewell Part One", "11", "Volume 11")],
            _ => [],
        }, []);

        await using (var imports = _imports.Create(_library))
        {
            await imports.StartAsync(Ct);
            var pages = 2;
            await imports.AddAsync(fileNames.Select(f => SyntheticBooks.Pdf(_temp.Combine("Downloads", f), new SyntheticBooks.PdfOptions { Pages = pages++ })).ToList(), Ct);
            await imports.WaitUntilIdleAsync(Ct);
            await imports.ConfirmAsync(Ct);
            await imports.WaitUntilIdleAsync(Ct);
        }

        _queue = new IdentificationQueueFactory(_libraries, _identifier, _editor, _covers, _settings, NullLogger<IdentificationQueue>.Instance).Create(_library);
        await _queue.StartAsync(Ct);
        await _queue.WaitUntilIdleAsync(Ct);
        return (await Index.GetBooksAsync(Ct)).ToDictionary(b => b.Metadata.Files[0].DownloadedAs!, b => b.Metadata.Id);
    }

    [Fact]
    public async Task Corrections_survive_recomputing_and_are_dropped_when_the_book_leaves_the_queue()
    {
        var ids = await ImportAsync("Deadly Class Vol 1.pdf", "DeadlyClass_v2.pdf", "Deadly Class Vol 11.pdf", "Plutona.pdf");
        var groups = new ReviewGroups(Index, _queue);

        var grouping = await groups.GetAsync(Ct);
        var deadly = Assert.Single(grouping.Groups);
        Assert.Equal(3, deadly.Books.Count);
        Assert.Equal([ids["Plutona.pdf"]], grouping.Ungrouped.Select(b => b.BookId));

        await groups.RemoveAsync([ids["Deadly Class Vol 11.pdf"]], Ct);
        await groups.AddAsync(deadly, [ids["Plutona.pdf"]], Ct);
        grouping = await groups.GetAsync(Ct);
        Assert.Equal(3, Assert.Single(grouping.Groups).Books.Count);
        Assert.Contains(grouping.Groups[0].Books, b => b.BookId == ids["Plutona.pdf"]);
        Assert.Equal([ids["Deadly Class Vol 11.pdf"]], grouping.Ungrouped.Select(b => b.BookId));

        // Leaving the queue (approved elsewhere) drops the book's correction.
        await _queue.ApproveAsync(ids["Plutona.pdf"], Issue(9, "Plutona", "1"), new HashSet<ProposalField>(), Ct);
        await groups.GetAsync(Ct);
        await using var connection = await Index.OpenAsync(Ct);
        Assert.Equal(0L, await Dapper.SqlMapper.ExecuteScalarAsync<long>(connection,
            new Dapper.CommandDefinition("SELECT COUNT(*) FROM review_group_books WHERE book_id = @id;", new { id = ids["Plutona.pdf"] }, cancellationToken: Ct)));

        // Ungroup, then create a group by hand.
        grouping = await groups.GetAsync(Ct);
        await groups.UngroupAsync(grouping.Groups[0], Ct);
        Assert.Empty((await groups.GetAsync(Ct)).Groups);
        var created = await groups.CreateAsync([ids["Deadly Class Vol 1.pdf"], ids["Deadly Class Vol 11.pdf"]], Ct);
        Assert.Equal(created, Assert.Single((await groups.GetAsync(Ct)).Groups).Id);
    }

    [Fact]
    public async Task A_group_is_approved_as_one_batch_and_undone_at_once()
    {
        var ids = await ImportAsync("Deadly Class Vol 1.pdf", "DeadlyClass_v2.pdf", "Deadly Class Vol 11.pdf", "Deadly Class Vol 6.pdf");
        var groups = new ReviewGroups(Index, _queue);
        var group = Assert.Single((await groups.GetAsync(Ct)).Groups);
        Assert.Equal(4, group.Books.Count);

        // The volume has issues 1 and 2; Vol 11 keeps its own candidate; Vol 6 has no match.
        var volume = new VolumeIssues(new Metadata.Sources.CvVolume(100, "Deadly Class", 2014, "Image", 2),
        [
            new Metadata.Sources.CvIssueSummary(1001, 100, "Deadly Class", "1", "Reagan Youth", new PartialDate(2015)),
            new Metadata.Sources.CvIssueSummary(1002, 100, "Deadly Class", "2", "Kids of the Black Hole", new PartialDate(2015, 6)),
        ]);
        var series = new GroupSeries("Deadly Class", "Image Comics", 2014, 100, 2);
        var rows = group.Books
            .Select(b => SeriesPlan.Row(b, VolumeMatcher.Match(volume, new VolumeBook(b.Book.Metadata, b.Number, b.Best)), series, TitleTemplate))
            .ToList();
        Assert.Equal(["Deadly Class Vol. 1: Reagan Youth", "Deadly Class Vol. 2: Kids of the Black Hole", null, "Deadly Class Vol. 11: A Fond Farewell Part One"],
            rows.Select(r => r.NewTitle));
        Assert.True(rows[3].Changes);
        Assert.False(rows[3].ChangesVolume);

        var batch = await _queue.ApproveGroupAsync("Deadly Class", series, rows, null, Ct);

        Assert.NotNull(batch);
        Assert.Equal(3, batch.Entries.Count);
        var approved = await Index.GetBookAsync(ids["DeadlyClass_v2.pdf"], Ct);
        Assert.Equal("Deadly Class Vol. 2: Kids of the Black Hole", approved!.Metadata.Title);
        Assert.Equal(new SeriesInfo("Deadly Class", "2"), approved.Metadata.Series);
        Assert.Equal("Image Comics", approved.Metadata.Publisher);
        Assert.Equal(MatchedBy.ComicVineVolume, approved.Metadata.Review.MatchedBy);
        Assert.Equal("detailed", approved.Metadata.Summary);
        Assert.StartsWith("Books/Image Comics/Deadly Class/", approved.Folder);
        var farewell = await Index.GetBookAsync(ids["Deadly Class Vol 11.pdf"], Ct);
        Assert.Equal(new SeriesInfo("Deadly Class", "11"), farewell!.Metadata.Series);

        // The book without a match stays in the Review, in its group.
        var left = await groups.GetAsync(Ct);
        Assert.Empty(left.Groups);
        Assert.Equal([ids["Deadly Class Vol 6.pdf"]], left.Ungrouped.Select(b => b.BookId));

        // Each book's history knows the batch; undoing it brings all three back to the Review.
        var history = await _editor.GetHistoryAsync(_library, ids["DeadlyClass_v2.pdf"], Ct);
        Assert.Equal(batch.Id, history[0].Batch?.Id);

        var restored = await _queue.UndoBatchAsync(batch.Id, Ct);

        Assert.Equal(3, restored.Count);
        var back = await Index.GetBookAsync(ids["DeadlyClass_v2.pdf"], Ct);
        Assert.Equal(ReviewStatus.Pending, back!.Metadata.Review.Status);
        Assert.Equal("DeadlyClass v2", back.Metadata.Title);
        Assert.StartsWith("Books/_To review/", back.Folder);
        Assert.Equal(IdentificationState.NeedsReview, (await _queue.GetAsync(ids["DeadlyClass_v2.pdf"], Ct))!.State);
        Assert.Equal(4, Assert.Single((await groups.GetAsync(Ct)).Groups).Books.Count);
        Assert.Null((await _editor.GetHistoryAsync(_library, ids["DeadlyClass_v2.pdf"], Ct)).FirstOrDefault(h => h.Batch is not null));
        Assert.Empty(await _queue.UndoBatchAsync(batch.Id, Ct));
    }

    private const string TitleTemplate = Core.Text.TitleTemplate.Default;
}
