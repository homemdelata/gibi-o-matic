using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Settings;
using GibiOMatic.Metadata.Identification;
using GibiOMatic.Metadata.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace GibiOMatic.Metadata.Tests;

/// <summary>A small ComicVine shaped like the real answers seen in docs/RESEARCH.md.</summary>
internal sealed class FakeComicVine : IComicVineApi
{
    public bool HasKey { get; set; } = true;
    public List<CvVolume> Volumes { get; } = [];
    public List<CvIssueSummary> Issues { get; } = [];
    public Dictionary<int, CvIssue> Details { get; } = [];
    public List<string> Calls { get; } = [];

    public Task<bool> HasKeyAsync(CancellationToken ct) => Task.FromResult(HasKey);

    /// <summary>Answers for specific search texts; any other text gets <see cref="Volumes"/>.</summary>
    public Dictionary<string, List<CvVolume>> Searches { get; } = [];

    public Task<IReadOnlyList<CvVolume>> SearchVolumesAsync(string query, CancellationToken ct, int limit = 30)
    {
        Calls.Add($"search:{query}");
        return Task.FromResult<IReadOnlyList<CvVolume>>(Searches.GetValueOrDefault(query) ?? Volumes);
    }

    public Task<IReadOnlyList<CvIssueSummary>> GetIssuesAsync(IReadOnlyCollection<int> volumeIds, CancellationToken ct, string? issueNumber = null)
    {
        Calls.Add($"issues:{string.Join('|', volumeIds.Order())}{(issueNumber is null ? "" : "#" + issueNumber)}");
        return Task.FromResult<IReadOnlyList<CvIssueSummary>>(Issues
            .Where(i => volumeIds.Contains(i.VolumeId) && (issueNumber is null || i.IssueNumber == issueNumber)).ToList());
    }

    public Task<CvIssue?> GetIssueAsync(int id, CancellationToken ct)
    {
        Calls.Add($"issue:{id}");
        return Task.FromResult(Details.GetValueOrDefault(id));
    }

    public Task<CvVolume?> GetVolumeAsync(int id, CancellationToken ct)
    {
        Calls.Add($"volume:{id}");
        return Task.FromResult(Volumes.FirstOrDefault(v => v.Id == id));
    }

    public static FakeComicVine DeadlyClass()
    {
        var cv = new FakeComicVine();
        cv.Volumes.AddRange(
        [
            new(110313, "Deadly Class", 2015, "Urban Comics", 3),
            new(71032, "Deadly Class", 2014, "Image", 56),
            new(75698, "Deadly Class: Reagan Youth", 2014, "Image", 1),
            new(144057, "Deadly Class: A Fond Farewell Part One", 2022, "Image", 1),
            new(149368, "Deadly Class: A Fond Farewell Part Two", 2022, "Image", 1),
            new(36569, "Wolverine: First Class: Class Actions", 2010, "Marvel", 1),
        ]);
        cv.Issues.AddRange(
        [
            new(459636, 75698, "Deadly Class: Reagan Youth", "1", "Vol. 1", new PartialDate(2014, 7, 16)),
            new(937068, 144057, "Deadly Class: A Fond Farewell Part One", "1", "Volume 11", new PartialDate(2022, 6, 1)),
            new(950001, 149368, "Deadly Class: A Fond Farewell Part Two", "1", "Volume 12", new PartialDate(2022, 11, 2)),
            new(479942, 71032, "Deadly Class", "11", null, new PartialDate(2015, 2, 25)),
            new(800001, 110313, "Deadly Class", "1", null, new PartialDate(2015, 5, 1)),
        ]);
        cv.Details[937068] = new CvIssue(937068, 144057, "Deadly Class: A Fond Farewell Part One", "1", "Volume 11", new PartialDate(2022, 6, 1),
            [new Credit(CreditRole.Writer, "Rick Remender"), new Credit(CreditRole.Artist, "Wes Craig")], "Collects #49-52.", "https://cv/937068");
        return cv;
    }
}

internal sealed class FakeIsbnSource(string id, Dictionary<string, MetadataRecord> books) : IIsbnSource
{
    public string Id => id;

    public Task<MetadataRecord?> LookupIsbnAsync(string isbn, CancellationToken ct) => Task.FromResult(books.GetValueOrDefault(isbn));
}

public sealed class IdentifierTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly MetadataSettings Settings = new();

    private static BookMetadata Book(string title, string? publisher = null, int? year = null, string? isbn = null) => new()
    {
        Id = "01ARYZ6S41TSV4RRFFQ69G5FAV",
        Title = title,
        Publisher = publisher,
        Date = year is null ? null : new PartialDate(year.Value),
        Identifiers = new BookIdentifiers { Isbn = isbn },
        CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    private static BookIdentifier Identifier(FakeComicVine cv, params IIsbnSource[] isbn) =>
        new(isbn, cv, NullLogger<BookIdentifier>.Instance);

    private static FakeIsbnSource OpenLibrary(params (string Isbn, MetadataRecord Record)[] books) =>
        new(SourceIds.OpenLibrary, books.ToDictionary(b => b.Isbn, b => b.Record));

    [Fact]
    public async Task A_numbered_trade_paperback_is_found_as_its_own_volume()
    {
        var cv = FakeComicVine.DeadlyClass();

        var result = await Identifier(cv).IdentifyAsync(Book("Deadly Class, Vol. 11"), Settings, null, Ct);

        var best = result.Best!;
        Assert.Equal("Deadly Class, Vol. 11: A Fond Farewell Part One", best.Record.Title);
        Assert.Equal(("Deadly Class", "11", BookKind.Tpb), (best.Record.SeriesName, best.Record.Number, best.Record.Kind));
        Assert.Equal("4000-937068", best.Record.ComicVineId);
        Assert.True(best.Record.IsDetailed, "details are fetched for the best match");
        Assert.Equal(["Rick Remender", "Wes Craig"], best.Record.Credits.Select(c => c.Name));
        Assert.Contains(MatchReason.Number, best.Reasons);
        Assert.InRange(best.Confidence, 0.5, 0.94); // no publisher or year to confirm: goes to Review
        Assert.False(Proposal.CanAutoApprove(Book("Deadly Class, Vol. 11"), result, Settings, out _));

        // One search, one request for the issues of short volumes, one for issue #11 of the long series, one detail.
        Assert.Equal(["search:Deadly Class", "issues:75698|110313|144057|149368", "issues:71032#11", "issue:937068"], cv.Calls);
    }

    [Fact]
    public async Task Glued_file_names_still_match()
    {
        var cv = FakeComicVine.DeadlyClass();

        var result = await Identifier(cv).IdentifyAsync(Book("deadlyclass vol11"), Settings, null, Ct);

        Assert.Equal("4000-937068", result.Best?.Record.ComicVineId);
    }

    [Fact]
    public async Task Isbn_confirmed_by_comicvine_is_approved_automatically()
    {
        var cv = FakeComicVine.DeadlyClass();
        var ol = OpenLibrary(("9781534321236", new MetadataRecord
        {
            Source = SourceIds.OpenLibrary, Title = "Deadly Class, Vol. 11: A Fond Farewell, Part One", Publisher = "Image Comics",
            Date = new PartialDate(2022), Isbn = "9781534321236", PageCount = 128,
        }));
        var book = Book("deadlyclass vol11", isbn: "978-1-5343-2123-6");

        var result = await Identifier(cv, ol).IdentifyAsync(book, Settings, null, Ct);

        var best = result.Best!;
        Assert.Equal(SourceIds.ComicVine, best.Record.Source);
        Assert.True(best.IsbnMatch);
        Assert.True(best.ConfirmedBySecondSource);
        Assert.True(best.Confidence >= 0.97);
        Assert.Equal(("9781534321236", 128), (best.Record.Isbn, best.Record.PageCount));
        Assert.Contains(result.Candidates, c => c.Record.Source == SourceIds.OpenLibrary);
        Assert.True(Proposal.CanAutoApprove(book, result, Settings, out var reason), reason);
        Assert.False(Proposal.CanAutoApprove(book, result, Settings with { AlwaysReview = true }, out _));
    }

    [Fact]
    public async Task A_good_existing_value_that_disagrees_sends_the_book_to_review()
    {
        var cv = FakeComicVine.DeadlyClass();
        var ol = OpenLibrary(("9781534321236", new MetadataRecord
        {
            Source = SourceIds.OpenLibrary, Title = "Deadly Class, Vol. 11", Publisher = "Image Comics", Isbn = "9781534321236",
        }));
        var book = Book("Deadly Class, Vol. 11", publisher: "Image Comics", isbn: "9781534321236") with
        {
            Date = new PartialDate(2019, 3, 5),
            Credits = [new Credit(CreditRole.Writer, "Rick Remender")],
            Series = new SeriesInfo("Deadly Class", "11"),
        };

        var result = await Identifier(cv, ol).IdentifyAsync(book, Settings, null, Ct);

        Assert.True(result.Best!.Confidence >= 0.95);
        Assert.False(Proposal.CanAutoApprove(book, result, Settings, out var reason));
        Assert.Equal("conflict", reason);
        var date = Proposal.Compare(book, result.Best.Record).Single(r => r.Field == ProposalField.Date);
        Assert.True(date.Conflict);
    }

    [Fact]
    public async Task Foreign_editions_rank_below_the_original()
    {
        var cv = new FakeComicVine();
        cv.Volumes.AddRange([new(1, "House of Slaughter", 2021, "Splitter", 5), new(2, "House of Slaughter", 2021, "Boom! Studios", 10)]);
        cv.Issues.AddRange([new(11, 1, "House of Slaughter", "1", null, new PartialDate(2022)), new(21, 2, "House of Slaughter", "1", null, new PartialDate(2021))]);

        var result = await Identifier(cv).IdentifyAsync(Book("House of Slaughter #1"), Settings, null, Ct);

        Assert.Equal("4000-21", result.Best?.Record.ComicVineId);
        Assert.Equal("House of Slaughter #1", result.Best?.Record.Title);
        Assert.Equal(BookKind.Issue, result.Best?.Record.Kind);
        Assert.True(result.Candidates.Single(c => c.Record.ComicVineId == "4000-11").Confidence < result.Best!.Confidence - 0.2);
    }

    [Fact]
    public async Task A_stand_alone_book_is_a_one_shot()
    {
        var cv = new FakeComicVine();
        cv.Volumes.Add(new(5, "Plutona", 2017, "Image", 1));
        cv.Issues.Add(new(55, 5, "Plutona", "1", null, new PartialDate(2017, 1, 18)));

        var result = await Identifier(cv).IdentifyAsync(Book("Plutona", publisher: "Image Comics", year: 2017), Settings, null, Ct);

        var record = result.Best!.Record;
        Assert.True(record.IsOneShot);
        Assert.Null(record.SeriesName);
        Assert.Equal("Plutona", record.Title);
    }

    [Fact]
    public async Task Without_a_comicvine_key_only_the_isbn_sources_are_used()
    {
        var cv = FakeComicVine.DeadlyClass();
        cv.HasKey = false;
        var ol = OpenLibrary(("9781607066019", new MetadataRecord { Source = SourceIds.OpenLibrary, Title = "Saga, Vol. 1", Isbn = "9781607066019" }));

        var result = await Identifier(cv, ol).IdentifyAsync(Book("x", isbn: "9781607066019"), Settings, null, Ct);

        Assert.Contains("comicvine:no-key", result.Notes);
        Assert.Empty(cv.Calls);
        var best = Assert.Single(result.Candidates);
        Assert.Equal(("Saga", "1", BookIdentifier.IsbnConfidence), (best.Record.SeriesName, best.Record.Number, best.Confidence));
    }

    [Fact]
    public async Task Disabled_sources_are_not_asked()
    {
        var cv = FakeComicVine.DeadlyClass();
        var settings = new MetadataSettings { Sources = [new("openlibrary", true), new("comicvine", false), new("googlebooks", false)] };

        var result = await Identifier(cv).IdentifyAsync(Book("Deadly Class, Vol. 11"), settings, null, Ct);

        Assert.Empty(result.Candidates);
        Assert.Empty(cv.Calls);
    }

    [Theory]
    [InlineData("somethingiskillingthechildren vol2", "somethingiskillingthechildren vol 2")]
    [InlineData("Deadly Class, Vol. 11: A Fond Farewell", "Deadly Class vol 11: A Fond Farewell")]
    [InlineData("Lumberjanes #18", "Lumberjanes #18")]
    [InlineData("Shrugged", "Shrugged")]
    [InlineData("RoboCop Vol. 3 - Last Stand #5", "RoboCop: Last Stand #5")]
    [InlineData("The Boys Vol. 11 - Over the Hill with the Swords of a Thousand Men", "The Boys vol 11: Over the Hill with the Swords of a Thousand Men")]
    [InlineData("Something Epic Vol. 1 (v2)", "Something Epic vol 1")]
    [InlineData("Hellboy - Krampusnacht", "Hellboy: Krampusnacht")]
    [InlineData("Wynd - Book One - Flight of the Prince", "Wynd vol 1: Flight of the Prince")]
    [InlineData("Pathfinder #1 - Map", "Pathfinder #1: Map")]
    [InlineData("Fathom - Kiani - Omnibus 1", "Fathom: Kiani Omnibus vol 1")]
    [InlineData("RoboCop (2003) Vol. 1", "RoboCop (2003) vol 1")]
    public void The_search_box_keeps_the_number_and_searching_it_again_gives_the_same_query(string title, string expected)
    {
        var query = BookQuery.From(Book(title));
        Assert.Equal(expected, query.EditableText);

        var again = BookQuery.From(Book(title), query.EditableText);
        Assert.Equal((query.SearchText, query.Number, query.Kind, query.Subtitle, query.SeriesYear),
            (again.SearchText, again.Number, again.Kind, again.Subtitle, again.SeriesYear));
    }

    [Theory]
    [InlineData("RoboCop Vol. 3 - Last Stand #5", new[] { "RoboCop Last Stand", "RoboCop" })]
    [InlineData("The Boys Vol. 11 - Over the Hill with the Swords of a Thousand Men", new[] { "The Boys Over the Hill with the Swords of a Thousand Men", "The Boys" })]
    [InlineData("Hellboy - Krampusnacht", new[] { "Hellboy Krampusnacht", "Hellboy" })]
    [InlineData("Saga, Vol. 1", new[] { "Saga" })]
    public void Searches_go_from_the_most_specific_text_to_the_series(string title, string[] expected) =>
        Assert.Equal(expected, BookQuery.From(Book(title)).SearchTexts);

    [Fact]
    public async Task A_mini_series_named_between_the_volume_and_the_issue_is_searched_for()
    {
        var cv = new FakeComicVine();
        cv.Volumes.AddRange(
        [
            new(66073, "Robocop: Last Stand", 2013, "Boom! Studios", 8),
            new(71995, "Robocop: Last Stand", 2014, "Boom! Studios", 2),
            new(4418, "RoboCop", 1990, "Marvel", 23),
            new(21144, "RoboCop: Roulette", 1993, "Dark Horse Comics", 4),
        ]);
        cv.Issues.AddRange(
        [
            new(5005, 66073, "Robocop: Last Stand", "5", null, new PartialDate(2013, 12, 11)),
            new(5001, 66073, "Robocop: Last Stand", "1", null, new PartialDate(2013, 8, 14)),
            new(7101, 71995, "Robocop: Last Stand", "1", "Volume 1", new PartialDate(2014, 3, 1)),
            new(4405, 4418, "RoboCop", "5", null, new PartialDate(1990, 7, 1)),
            new(2104, 21144, "RoboCop: Roulette", "4", null, new PartialDate(1994, 3, 1)),
        ]);

        var result = await Identifier(cv).IdentifyAsync(Book("RoboCop Vol. 3 - Last Stand #5"), Settings, null, Ct);

        var best = result.Best!;
        Assert.Equal("4000-5005", best.Record.ComicVineId);
        Assert.Equal(("Robocop: Last Stand #5", "Robocop: Last Stand", "5", BookKind.Issue),
            (best.Record.Title, best.Record.SeriesName, best.Record.Number, best.Record.Kind));
        Assert.DoesNotContain(result.Candidates, c => c.Record.ComicVineId == "4000-4405" && c.Confidence >= best.Confidence);
        Assert.Equal("search:RoboCop Last Stand", Assert.Single(cv.Calls, c => c.StartsWith("search:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_subtitle_picks_the_collected_edition_by_its_issue_name()
    {
        var cv = new FakeComicVine();
        cv.Searches["The Boys Over the Hill with the Swords of a Thousand Men"] = [];
        cv.Volumes.AddRange(
        [
            new(18033, "The Boys", 2006, "Dynamite Entertainment", 72),
            new(62088, "The Boys", 2007, "Panini Verlag", 13),
            new(900, "The Boys", 2007, "Dynamite Entertainment", 12),
        ]);
        cv.Issues.AddRange(
        [
            new(115525, 18033, "The Boys", "11", "Glorious Five Year Plan, Part One", new PartialDate(2007, 10, 1)),
            new(406047, 62088, "The Boys", "11", "Butcher", new PartialDate(2012, 11, 1)),
            new(9011, 900, "The Boys", "11", "Over the Hill with the Swords of a Thousand Men", new PartialDate(2012, 6, 1)),
            new(9010, 900, "The Boys", "10", "Butcher, Baker, Candlestickmaker", new PartialDate(2011, 11, 1)),
        ]);

        var result = await Identifier(cv).IdentifyAsync(Book("The Boys Vol. 11 - Over the Hill with the Swords of a Thousand Men"), Settings, null, Ct);

        var best = result.Best!;
        Assert.Equal("4000-9011", best.Record.ComicVineId);
        Assert.Equal(("The Boys", "11", BookKind.Tpb), (best.Record.SeriesName, best.Record.Number, best.Record.Kind));
        Assert.Contains(MatchReason.Subtitle, best.Reasons);
        Assert.Equal(["search:The Boys Over the Hill with the Swords of a Thousand Men", "search:The Boys"],
            cv.Calls.Where(c => c.StartsWith("search:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_title_with_a_dash_finds_the_volume_named_with_a_colon()
    {
        var cv = new FakeComicVine();
        cv.Volumes.AddRange(
        [
            new(1, "Hellboy: Into the Silent Sea", 2017, "Dark Horse Comics", 1),
            new(2, "Hellboy: Krampusnacht", 2017, "Dark Horse Comics", 1),
            new(3, "Hellboy", 1994, "Dark Horse Comics", 5),
        ]);
        cv.Issues.AddRange(
        [
            new(11, 1, "Hellboy: Into the Silent Sea", "1", null, new PartialDate(2017, 4, 5)),
            new(21, 2, "Hellboy: Krampusnacht", "1", null, new PartialDate(2017, 12, 6)),
            new(31, 3, "Hellboy", "1", "Seed of Destruction", new PartialDate(1994, 3, 1)),
        ]);

        var result = await Identifier(cv).IdentifyAsync(Book("Hellboy - Krampusnacht"), Settings, null, Ct);

        Assert.Equal("4000-21", result.Best?.Record.ComicVineId);
        Assert.Equal("search:Hellboy Krampusnacht", Assert.Single(cv.Calls, c => c.StartsWith("search:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_story_arc_named_like_a_book_is_not_a_collected_edition()
    {
        // Real ComicVine data: issues #1-#5 of Wynd are all named "Book One: The Flight of the Prince".
        var cv = new FakeComicVine();
        cv.Volumes.AddRange([new(127982, "Wynd", 2020, "Boom! Studios", 10), new(135402, "Wynd", 2021, "Boom! Studios", 3)]);
        for (var n = 1; n <= 5; n++)
        {
            cv.Issues.Add(new(768550 + n, 127982, "Wynd", n.ToString(System.Globalization.CultureInfo.InvariantCulture), "Book One: The Flight of the Prince", new PartialDate(2020, n + 5)));
        }

        cv.Issues.AddRange(
        [
            new(843108, 135402, "Wynd", "1", "Book One: The Flight of the Prince", new PartialDate(2021, 4, 17)),
            new(953034, 135402, "Wynd", "2", "Book Two: The Secret of the Wings", new PartialDate(2022, 3, 1)),
        ]);

        var result = await Identifier(cv).IdentifyAsync(Book("Wynd - Book One - Flight of the Prince"), Settings, null, Ct);

        Assert.Equal("4000-843108", result.Best?.Record.ComicVineId);
        Assert.Single(result.Candidates, c => c.Confidence >= 0.6);
    }

    [Theory]
    [InlineData("Volume 11", "11")]
    [InlineData("Book Two: The Secret of the Wings", "2")]
    [InlineData("Part One: The Beginning", "1")]
    [InlineData("The Kingdom, Part 2", null)]
    [InlineData("Glorious Five Year Plan, Part One", null)]
    public void Part_counts_only_at_the_start_of_an_issue_name(string name, string? number) =>
        Assert.Equal(number, TextMatch.CollectedNumber(name));

    private static FakeComicVine SpawnOrigins()
    {
        // Real ComicVine data: the collected editions are issues of one volume, #19 named after a story arc.
        var cv = new FakeComicVine();
        cv.Volumes.Add(new(48526, "Spawn Origins Collection", 2009, "Image", 22));
        cv.Issues.AddRange(
        [
            new(427159, 48526, "Spawn Origins Collection", "19", "The Kingdom, Part 2", new PartialDate(2013, 9, 25)),
            new(427000, 48526, "Spawn Origins Collection", "2", null, new PartialDate(2009, 12, 1)),
        ]);
        cv.Details[427159] = new CvIssue(427159, 48526, "Spawn Origins Collection", "19", "The Kingdom, Part 2", new PartialDate(2013, 9, 25),
            [new Credit(CreditRole.Writer, "Todd McFarlane")], null, "https://cv/427159");
        return cv;
    }

    [Fact]
    public async Task A_chapter_in_the_issue_name_is_not_the_volume_number()
    {
        var result = await Identifier(SpawnOrigins()).IdentifyAsync(Book("Spawn Origins Vol. 19"), Settings, null, Ct);

        var best = result.Best!;
        Assert.Equal("4000-427159", best.Record.ComicVineId);
        Assert.Equal(("Spawn Origins Collection, Vol. 19", "Spawn Origins Collection", "19", BookKind.Tpb),
            (best.Record.Title, best.Record.SeriesName, best.Record.Number, best.Record.Kind));
        Assert.Contains(MatchReason.Number, best.Reasons);
        Assert.Equal("Spawn Origins Collection #19 · The Kingdom, Part 2", best.Record.SourceName);
    }

    [Fact]
    public async Task An_issue_link_with_a_chapter_in_its_name_keeps_its_number()
    {
        var candidates = await Identifier(SpawnOrigins()).FromComicVineLinkAsync(Book("Spawn Origins Vol. 19"),
            "https://comicvine.gamespot.com/spawn-origins-collection-19-the-kingdom-part-2/4000-427159/", Ct);

        var candidate = Assert.Single(candidates);
        Assert.Equal(("Spawn Origins Collection, Vol. 19", "19"), (candidate.Record.Title, candidate.Record.Number));
    }

    [Fact]
    public async Task A_series_named_after_its_creator_and_numbered_by_the_issue_name()
    {
        var cv = new FakeComicVine();
        cv.Volumes.AddRange([new(27642, "Richard Stark's Parker", 2009, "IDW Publishing", 4), new(82260, "The Hunter", 2015, "Nobrow", 1)]);
        cv.Issues.AddRange(
        [
            new(1, 27642, "Richard Stark's Parker", "1", "The Hunter", new PartialDate(2009, 7, 1)),
            new(2, 27642, "Richard Stark's Parker", "2", "The Outfit", new PartialDate(2010, 10, 1)),
            new(3, 82260, "The Hunter", "1", null, new PartialDate(2015, 5, 1)),
        ]);

        var result = await Identifier(cv).IdentifyAsync(Book("Parker - The Hunter"), Settings, null, Ct);

        Assert.Equal("4000-1", result.Best?.Record.ComicVineId);
        Assert.Contains(MatchReason.Subtitle, result.Best!.Reasons);
    }

    [Fact]
    public async Task The_only_book_of_a_volume_is_its_volume_one()
    {
        var cv = new FakeComicVine();
        cv.Volumes.Add(new(152084, "Scotch McTiernan Versus the Forces of Evil", 2023, "Image", 1));
        cv.Issues.Add(new(1, 152084, "Scotch McTiernan Versus the Forces of Evil", "1", null, new PartialDate(2023, 8, 1)));

        var result = await Identifier(cv).IdentifyAsync(Book("Scotch McTiernan Versus the Forces of Evil Vol. 1"), Settings, null, Ct);

        var best = result.Best!;
        Assert.Equal(("Scotch McTiernan Versus the Forces of Evil, Vol. 1", "1", BookKind.Tpb, false),
            (best.Record.Title, best.Record.Number, best.Record.Kind, best.Record.IsOneShot));
        Assert.Contains(MatchReason.Number, best.Reasons);
    }

    [Fact]
    public async Task The_year_in_parentheses_picks_the_series_of_that_year()
    {
        var cv = new FakeComicVine();
        cv.Volumes.AddRange([new(1, "Ghost", 1995, "Dark Horse Comics", 36), new(2, "Ghost", 2012, "Dark Horse Comics", 4)]);
        cv.Issues.AddRange([new(11, 1, "Ghost", "1", null, new PartialDate(1995, 4, 1)), new(21, 2, "Ghost", "1", null, new PartialDate(2012, 9, 1))]);

        var result = await Identifier(cv).IdentifyAsync(Book("Ghost (2012) #1"), Settings, null, Ct);

        Assert.Equal("4000-21", result.Best?.Record.ComicVineId);
        Assert.Equal("search:Ghost", cv.Calls[0]);
    }

    [Theory]
    [InlineData("https://comicvine.gamespot.com/deadly-class-a-fond-farewell-part-one-1-volume-11/4000-937068/", true, 937068)]
    [InlineData("comicvine.gamespot.com/deadly-class/4050-71032/", false, 71032)]
    public void ComicVine_links_are_recognised(string link, bool isIssue, int id) =>
        Assert.Equal((isIssue, id), BookIdentifier.ParseComicVineLink(link));

    [Theory]
    [InlineData("https://comicvine.gamespot.com/x/4000-937068/", BookReferenceKind.ComicVineIssue, null)]
    [InlineData("https://comicvine.gamespot.com/deadly-class/4050-71032/", BookReferenceKind.ComicVineVolume, null)]
    [InlineData("https://www.amazon.com/Saga-Vol-1-Brian-Vaughan/dp/1607066017/ref=sr_1_1?crid=X", BookReferenceKind.Isbn, "9781607066019")]
    [InlineData("https://www.amazon.com.br/gp/product/1607066017", BookReferenceKind.Isbn, "9781607066019")]
    [InlineData("https://www.amazon.com/Saga-Vol-1-ebook/dp/B00AAJQ8WE", BookReferenceKind.AmazonKindle, null)]
    [InlineData("https://www.goodreads.com/book/show/15704307-saga-volume-1", BookReferenceKind.Goodreads, null)]
    [InlineData("ISBN 978-1-60706-601-9", BookReferenceKind.Isbn, "9781607066019")]
    [InlineData("160706601X", BookReferenceKind.Unknown, null)]
    [InlineData("https://example.com/whatever", BookReferenceKind.Unknown, null)]
    public void Pasted_links_and_isbns_are_recognised(string text, BookReferenceKind kind, string? isbn)
    {
        var reference = BookReference.Parse(text);
        Assert.Equal((kind, isbn), (reference.Kind, reference.Isbn));
    }

    [Fact]
    public async Task An_issue_link_chosen_by_the_user_is_the_candidate()
    {
        var cv = FakeComicVine.DeadlyClass();

        var candidates = await Identifier(cv).FromComicVineLinkAsync(Book("whatever"),
            "https://comicvine.gamespot.com/x/4000-937068/", Ct);

        var candidate = Assert.Single(candidates);
        Assert.Equal((1.0, "Deadly Class, Vol. 11: A Fond Farewell Part One"), (candidate.Confidence, candidate.Record.Title));
        Assert.Equal([MatchReason.ChosenByUser], candidate.Reasons);
        Assert.Equal("Image", candidate.Record.Publisher);
        Assert.True(candidate.Record.IsDetailed);
    }

    [Fact]
    public async Task A_volume_link_offers_its_issues_closest_first()
    {
        var cv = FakeComicVine.DeadlyClass();

        var candidates = await Identifier(cv).FromComicVineLinkAsync(Book("Deadly Class #11"), "https://comicvine.gamespot.com/deadly-class/4050-71032/", Ct);

        Assert.Equal("4000-479942", candidates[0].Record.ComicVineId);
        Assert.Empty(await Identifier(cv).FromComicVineLinkAsync(Book("x"), "https://example.com/not-comicvine", Ct));
    }

    [Fact]
    public async Task The_user_can_search_with_other_words()
    {
        var cv = FakeComicVine.DeadlyClass();

        var result = await Identifier(cv).IdentifyAsync(Book("whatever_file_name"), Settings, "Deadly Class: Reagan Youth", Ct);

        // Series and subtitle together; the volume is found, so the broad search is not needed.
        Assert.Equal("search:Deadly Class Reagan Youth", cv.Calls[0]);
        Assert.DoesNotContain("search:Deadly Class", cv.Calls);
        Assert.Equal("4000-459636", result.Best?.Record.ComicVineId);
    }
}

public sealed class ProposalTests
{
    private static readonly MetadataRecord Record = new()
    {
        Source = SourceIds.ComicVine,
        Title = "Saga, Vol. 1",
        SeriesName = "Saga",
        Number = "1",
        Kind = BookKind.Tpb,
        Date = new PartialDate(2012, 10, 10),
        Publisher = "Image",
        Credits = [new Credit(CreditRole.Writer, "Brian K. Vaughan"), new Credit(CreditRole.Artist, "Fiona Staples")],
        Summary = "Space opera.",
        ComicVineId = "4000-1",
    };

    private static readonly BookMetadata Book = new()
    {
        Id = "01ARYZ6S41TSV4RRFFQ69G5FAV",
        Title = "saga vol1",
        Publisher = "Image Comics",
        Credits = [new Credit(CreditRole.Writer, "Desconhecido"), new Credit(CreditRole.Letterer, "Fonografiks")],
    };

    [Fact]
    public void Compare_marks_changes_and_equivalent_values()
    {
        var rows = Proposal.Compare(Book, Record).ToDictionary(r => r.Field);

        Assert.True(rows[ProposalField.Title].Changed);
        Assert.False(rows[ProposalField.Publisher].Changed, "Image = Image Comics");
        Assert.True(rows[ProposalField.Writers].Changed);
        Assert.False(rows[ProposalField.Writers].Conflict, "an unknown author is not a good value");
        Assert.False(rows[ProposalField.Letterers].Changed, "nothing proposed");
        Assert.DoesNotContain(rows.Values, r => r.Conflict);
    }

    [Fact]
    public void Apply_changes_only_the_chosen_fields_and_keeps_the_comicvine_id()
    {
        var applied = Proposal.Apply(Book, Record, new HashSet<ProposalField> { ProposalField.Title, ProposalField.Writers, ProposalField.Series });

        Assert.Equal("Saga, Vol. 1", applied.Title);
        Assert.Equal(new SeriesInfo("Saga", null), applied.Series);
        Assert.Equal("Image Comics", applied.Publisher);
        Assert.Equal([new Credit(CreditRole.Writer, "Brian K. Vaughan"), new Credit(CreditRole.Letterer, "Fonografiks")], applied.Credits);
        Assert.Null(applied.Summary);
        Assert.Equal("4000-1", applied.Identifiers.ComicVine);
    }
}
