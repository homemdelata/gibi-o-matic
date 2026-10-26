using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Quality;

namespace GibiOMatic.Core.Tests.Quality;

public sealed class QualityAnalyzerTests
{
    private static readonly DateTimeOffset Added = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static BookMetadata Book(
        string title,
        string authors = "Ann Smith",
        string? date = "2019-03-02",
        string? series = null,
        string? number = null,
        string? isbn = null,
        string? publisher = "Pub")
    {
        PartialDate? parsed = null;
        if (date is not null)
        {
            var parts = date.Split('-').Select(int.Parse).ToArray();
            parsed = new PartialDate(parts[0], parts.ElementAtOrDefault(1) is var m and > 0 ? m : null, parts.ElementAtOrDefault(2) is var d and > 0 ? d : null);
        }

        return new BookMetadata
        {
            Id = "01ARYZ6S41TSV4RRFFQ69G5FAV",
            Title = title,
            Credits = authors.Split(" & ", StringSplitOptions.RemoveEmptyEntries).Select(a => new Credit(CreditRole.Writer, a)).ToList(),
            Date = parsed,
            Series = series is null ? null : new SeriesInfo(series, number),
            Identifiers = new BookIdentifiers { Isbn = isbn },
            Publisher = publisher,
            CreatedAt = Added,
        };
    }

    /// <summary>
    /// The same books scored by <c>reference/python/analyze.py</c> (with its "\b" typo fixed),
    /// so the port keeps the exact scores of the validated script.
    /// </summary>
    [Fact]
    public void Scores_match_the_reference_script()
    {
        var books = new Dictionary<string, BookMetadata>
        {
            ["A"] = Book("houseofslaughter vol1", "Desconhecido", null, publisher: null),
            ["B"] = Book("Saga, Vol. 1", "Brian K. Vaughan & Fiona Staples", "2012-10-10", "Saga", "1", "9781607066019", "Image Comics"),
            ["C"] = Book("somethingiskillingthechildren", "Desconhecido", null, publisher: "BOOM! Studios"),
            ["D"] = Book("DEPARTMENT OF TRUTH, Vol. 01", "James Tynion IV| Martin Simmonds", "2015-12-10", isbn: "hum-truth_vol1", publisher: "Image"),
            ["E"] = Book("Hellboy, Vol. 02 [prop]", "Mignola", "2012-05-05", "Hellboy", "3", publisher: "Dark Horse"),
            ["F"] = Book("Filler One", date: "2012-05-05"),
            ["G"] = Book("Filler Two", date: "2012-05-05"),
            ["H"] = Book("Filler Three", date: "2012-05-05"),
            ["I"] = Book("Filler Four", date: "2012-05-05"),
            ["J"] = Book("Lumberjanes_Issue_18.cbz", "Al Bo & Al Bo & Cy Do & Ed Fu & Gil Ho & Ian Jo", "2026-01-01", publisher: "BOOM! Box"),
            ["K"] = Book("Untitled", @"Jane Doe\Smith", "2020-01-01", isbn: "9781607066018", publisher: "X"),
            ["L"] = Book("Test  Double  Space ", "Jo Ann", "2019-03-02"),
            ["M"] = Book("Vol. 3", "Jo Ann", "2019-03-03"),
            ["N"] = Book("Locke & Key, Vol. 4", "Joe Hill & Gabriel Rodriguez", "2011-01-01", "Locke & Key", "4", "9781600108860", "IDW"),
        };
        var expected = new Dictionary<string, int>
        {
            ["A"] = 18, ["B"] = 100, ["C"] = 14, ["D"] = 32, ["E"] = 45, ["F"] = 94, ["G"] = 94,
            ["H"] = 94, ["I"] = 94, ["J"] = 43, ["K"] = 59, ["L"] = 92, ["M"] = 100, ["N"] = 100,
        };
        var context = QualityContext.From(books.Values);

        var actual = books.ToDictionary(kv => kv.Key, kv => QualityAnalyzer.Analyze(kv.Value, context).Score);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(59, QualityBand.Bad)]
    [InlineData(60, QualityBand.Review)]
    [InlineData(84, QualityBand.Review)]
    [InlineData(85, QualityBand.Ok)]
    public void Bands_follow_the_spec_thresholds(int score, QualityBand band) =>
        Assert.Equal(band, QualityAnalyzer.BandOf(score));

    [Theory]
    [InlineData("saga vol 1", QualityIssue.TitleAllLowercase)]
    [InlineData("somethingiskillingthechildren", QualityIssue.TitleGluedWords)]
    [InlineData("Saga_Vol_1", QualityIssue.TitleFileName)]
    [InlineData("Saga Vol 1.pdf", QualityIssue.TitleFileName)]
    [InlineData("Untitled", QualityIssue.TitleGeneric)]
    [InlineData("Desconhecido", QualityIssue.TitleGeneric)]
    [InlineData(" Saga", QualityIssue.TitleExtraSpaces)]
    [InlineData("SAGA DELUXE", QualityIssue.TitleAllCaps)]
    [InlineData("DEPARTMENT OF TRUTH, Vol. 1", QualityIssue.TitleAllCaps)]
    [InlineData("Saga (prop)", QualityIssue.TitleJunk)]
    [InlineData("Saga [Digital]", QualityIssue.TitleJunk)]
    [InlineData("Saga, Vol. 01", QualityIssue.TitleLeadingZero)]
    public void Title_rules(string title, QualityIssue issue) =>
        Assert.Contains(QualityAnalyzer.Analyze(Book(title)).Problems, p => p.Issue == issue);

    [Fact]
    public void A_good_title_has_no_title_problems() =>
        Assert.False(QualityAnalyzer.Analyze(Book("Saga, Vol. 1", series: "Saga", number: "1")).HasProblemIn(QualityField.Title));

    [Theory]
    [InlineData("", QualityIssue.AuthorUnknown)]
    [InlineData("Desconhecido", QualityIssue.AuthorUnknown)]
    [InlineData("Various", QualityIssue.AuthorUnknown)]
    [InlineData("Mignola", QualityIssue.AuthorSingleName)]
    [InlineData("James Tynion IV| Chris O'Halloran| Gavin Fullerton", QualityIssue.AuthorsGlued)]
    [InlineData(@"Chris O\'Halloran", QualityIssue.AuthorBackslash)]
    [InlineData("Rick Remender & Rick Remender", QualityIssue.AuthorDuplicate)]
    [InlineData("Ann Smith| Ann Smith", QualityIssue.AuthorDuplicate)]
    [InlineData("A Bc & D Ef & G Hi & J Kl & M No", QualityIssue.AuthorsTooMany)]
    public void Author_rules(string authors, QualityIssue issue) =>
        Assert.Contains(QualityAnalyzer.Analyze(Book("Saga", authors)).Problems, p => p.Issue == issue);

    [Fact]
    public void Only_writers_count_as_authors()
    {
        var book = Book("Saga") with { Credits = [new Credit(CreditRole.Artist, "Fiona Staples")] };

        Assert.True(QualityAnalyzer.HasUnknownAuthor(book));
        Assert.Contains(QualityAnalyzer.Analyze(book).Problems, p => p.Issue == QualityIssue.AuthorUnknown);
    }

    [Fact]
    public void Date_rules()
    {
        Assert.Contains(QualityAnalyzer.Analyze(Book("Saga", date: null)).Problems, p => p.Issue == QualityIssue.DateMissing);
        Assert.Contains(QualityAnalyzer.Analyze(Book("Saga", date: "2015-12-11")).Problems, p => p.Issue == QualityIssue.DateHumble);
        Assert.Contains(QualityAnalyzer.Analyze(Book("Saga", date: "2026-01-01")).Problems, p => p.Issue == QualityIssue.DateSameAsAdded);

        var sameDay = Enumerable.Range(0, 5).Select(i => Book($"Book {i}", date: "2018-07-04")).ToList();
        var report = QualityAnalyzer.Analyze(sameDay[0], QualityContext.From(sameDay));
        var repeated = Assert.Single(report.Problems, p => p.Issue == QualityIssue.DateRepeated);
        Assert.Equal(5, repeated.Count);
    }

    [Fact]
    public void A_year_alone_is_not_suspicious()
    {
        var books = Enumerable.Range(0, 6).Select(i => Book($"Book {i}", date: "2015")).ToList();

        Assert.False(QualityAnalyzer.Analyze(books[0], QualityContext.From(books)).HasProblemIn(QualityField.Date));
    }

    [Fact]
    public void Series_rules()
    {
        var inTitle = QualityAnalyzer.Analyze(Book("Deadly Class, Vol. 11: A Fond Farewell"));
        var problem = Assert.Single(inTitle.Problems, p => p.Issue == QualityIssue.SeriesInTitleOnly);
        Assert.Equal("Deadly Class #11", problem.Detail);

        Assert.Contains(QualityAnalyzer.Analyze(Book("Hellboy, Vol. 2", series: "Hellboy", number: "3")).Problems,
            p => p.Issue == QualityIssue.SeriesNumberMismatch);
        Assert.False(QualityAnalyzer.Analyze(Book("Hellboy, Vol. 02", series: "Hellboy", number: "2")).HasProblemIn(QualityField.Series));
    }

    [Fact]
    public void Secondary_rules()
    {
        Assert.Contains(QualityAnalyzer.Analyze(Book("Saga", isbn: "MAY160548")).Problems, p => p.Issue == QualityIssue.InvalidIsbn);
        Assert.Contains(QualityAnalyzer.Analyze(Book("Saga", publisher: null)).Problems, p => p.Issue == QualityIssue.NoPublisher);
        Assert.Contains(QualityAnalyzer.Analyze(Book("Saga", publisher: " ")).Problems, p => p.Issue == QualityIssue.NoPublisher);
    }

    [Fact]
    public void An_author_counts_as_a_known_author()
    {
        var book = Book("Saga", authors: "") with { Credits = [new Credit(CreditRole.Author, "Ann Smith"), new Credit(CreditRole.Artist, "Bo Jones")] };

        Assert.Equal(["Ann Smith"], QualityAnalyzer.AuthorsOf(book));
        Assert.False(QualityAnalyzer.HasUnknownAuthor(book));
        Assert.False(QualityAnalyzer.Analyze(book).HasProblemIn(QualityField.Authors));
    }
}
