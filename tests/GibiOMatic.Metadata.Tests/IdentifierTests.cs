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

    public Task<IReadOnlyList<CvVolume>> SearchVolumesAsync(string query, CancellationToken ct, int limit = 30)
    {
        Calls.Add($"search:{query}");
        return Task.FromResult<IReadOnlyList<CvVolume>>(Volumes);
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
    public void The_search_box_keeps_the_number_and_searching_it_again_gives_the_same_query(string title, string expected)
    {
        var query = BookQuery.From(Book(title));
        Assert.Equal(expected, query.EditableText);

        var again = BookQuery.From(Book(title), query.EditableText);
        Assert.Equal((query.SearchText, query.Number, query.Kind, query.Subtitle), (again.SearchText, again.Number, again.Kind, again.Subtitle));
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

        await Identifier(cv).IdentifyAsync(Book("whatever_file_name"), Settings, "Deadly Class: Reagan Youth", Ct);

        Assert.Equal("search:Deadly Class", cv.Calls[0]);
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
