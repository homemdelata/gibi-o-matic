using System.Globalization;
using System.Text.RegularExpressions;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Text;

namespace GibiOMatic.Core.Quality;

/// <summary>
/// Metadata quality score from 0 to 100 (SPEC §5), ported from <c>reference/python/analyze.py</c>.
/// Each problem costs <c>severity × 8 × field weight</c> points.
/// </summary>
public static partial class QualityAnalyzer
{
    public const int ReviewThreshold = 60;
    public const int OkThreshold = 85;

    private static readonly HashSet<string> PlaceholderAuthors =
        new(["desconhecido", "unknown", "various", "vários", "varios", "autor desconhecido", "various artists"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> GenericTitles =
        new(["untitled", "sem título", "sem titulo", "unknown", "desconhecido", ""], StringComparer.OrdinalIgnoreCase);

    // Humble Bundle delivery dates that the old library used as publication dates.
    private static readonly DateOnly HumbleWindowStart = new(2015, 12, 9);
    private static readonly DateOnly HumbleWindowEnd = new(2015, 12, 14);

    public static double WeightOf(QualityField field) => field switch
    {
        QualityField.Title => 1.0,
        QualityField.Authors => 0.9,
        QualityField.Date => 0.7,
        QualityField.Series => 0.7,
        QualityField.Identifier => 0.3,
        QualityField.Publisher => 0.3,
        _ => 0,
    };

    public static QualityBand BandOf(int score) =>
        score < ReviewThreshold ? QualityBand.Bad : score < OkThreshold ? QualityBand.Review : QualityBand.Ok;

    public static QualityReport Analyze(BookMetadata book, QualityContext? context = null)
    {
        context ??= QualityContext.Empty;
        var problems = new List<QualityProblem>();
        CheckTitle(book.Title, problems);
        CheckAuthors(book, problems);
        CheckDate(book, context, problems);
        CheckSeries(book, problems);

        if (book.Identifiers.Isbn is { } isbn && !Isbn.IsValid(isbn))
        {
            problems.Add(new(QualityField.Identifier, 1, QualityIssue.InvalidIsbn, isbn));
        }

        if (string.IsNullOrWhiteSpace(book.Publisher))
        {
            problems.Add(new(QualityField.Publisher, 1, QualityIssue.NoPublisher));
        }

        var penalty = problems.Sum(p => p.Severity * 8 * WeightOf(p.Field));
        // Python's round() rounds half to even; keep the same scores as the reference script.
        var score = Math.Max(0, (int)Math.Round(100 - penalty, MidpointRounding.ToEven));
        return new QualityReport(score, BandOf(score), problems);
    }

    /// <summary>The authors and writers: "Autores" wherever a single field is expected (SPEC §4.3).</summary>
    public static IReadOnlyList<string> AuthorsOf(BookMetadata book) =>
        book.Credits.Where(c => c.Role is CreditRole.Author or CreditRole.Writer).OrderBy(c => c.Role).Select(c => c.Name).ToList();

    public static bool HasUnknownAuthor(BookMetadata book)
    {
        var authors = AuthorsOf(book);
        return authors.Count == 0 || authors.Any(a => PlaceholderAuthors.Contains(a.Trim()));
    }

    private static void CheckTitle(string t, List<QualityProblem> problems)
    {
        if (t == t.ToLowerInvariant() && t.Any(char.IsAsciiLetterLower))
        {
            problems.Add(new(QualityField.Title, 3, QualityIssue.TitleAllLowercase));
        }

        if (GluedWords().IsMatch(t) && !t.Trim().Contains(' '))
        {
            problems.Add(new(QualityField.Title, 3, QualityIssue.TitleGluedWords));
        }

        if (t.Contains('_') || FileExtension().IsMatch(t))
        {
            problems.Add(new(QualityField.Title, 3, QualityIssue.TitleFileName));
        }

        if (GenericTitles.Contains(t.Trim()))
        {
            problems.Add(new(QualityField.Title, 3, QualityIssue.TitleGeneric));
        }

        if (t.Contains("  ", StringComparison.Ordinal) || t != t.Trim())
        {
            problems.Add(new(QualityField.Title, 1, QualityIssue.TitleExtraSpaces));
        }

        var isUpper = t.Any(char.IsUpper) && !t.Any(char.IsLower);
        if ((isUpper && t.Length > 4) || CapsWords().IsMatch(t))
        {
            problems.Add(new(QualityField.Title, 1, QualityIssue.TitleAllCaps));
        }

        if (TitleJunk().IsMatch(t))
        {
            problems.Add(new(QualityField.Title, 2, QualityIssue.TitleJunk));
        }

        if (TitleParser.HasLeadingZeroVolume(t))
        {
            problems.Add(new(QualityField.Title, 1, QualityIssue.TitleLeadingZero));
        }
    }

    private static void CheckAuthors(BookMetadata book, List<QualityProblem> problems)
    {
        var authors = AuthorsOf(book).Select(a => a.Trim()).Where(a => a.Length > 0).ToList();

        if (authors.Count == 0 || authors.Any(PlaceholderAuthors.Contains))
        {
            problems.Add(new(QualityField.Authors, 3, QualityIssue.AuthorUnknown, string.Join(", ", authors)));
        }

        var single = authors.Where(a => !a.Contains(' ') && !PlaceholderAuthors.Contains(a) && !IsGlued(a)).ToList();
        if (single.Count > 0)
        {
            problems.Add(new(QualityField.Authors, 2, QualityIssue.AuthorSingleName, string.Join(", ", single)));
        }

        if (authors.FirstOrDefault(IsGlued) is { } glued)
        {
            problems.Add(new(QualityField.Authors, 3, QualityIssue.AuthorsGlued, glued));
        }

        if (authors.Any(a => a.Contains('\\')))
        {
            problems.Add(new(QualityField.Authors, 2, QualityIssue.AuthorBackslash));
        }

        var duplicated = authors.Distinct(StringComparer.OrdinalIgnoreCase).Count() != authors.Count
                         || authors.Any(a =>
                         {
                             var parts = GluedParts(a);
                             return parts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != parts.Length;
                         });
        if (duplicated)
        {
            problems.Add(new(QualityField.Authors, 2, QualityIssue.AuthorDuplicate));
        }

        if (authors.Count >= 5)
        {
            problems.Add(new(QualityField.Authors, 1, QualityIssue.AuthorsTooMany, authors.Count.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static bool IsGlued(string author) => author.Contains('|') || author.Contains(';');

    private static string[] GluedParts(string author) =>
        author.Split(['|', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void CheckDate(BookMetadata book, QualityContext context, List<QualityProblem> problems)
    {
        if (book.Date is not { } date)
        {
            problems.Add(new(QualityField.Date, 3, QualityIssue.DateMissing));
            return;
        }

        // The suspicious-date rules need a full date; a year alone is normal for comics.
        if (date is not { Month: { } month, Day: { } day } || !IsValidDate(date.Year, month, day))
        {
            return;
        }

        var full = new DateOnly(date.Year, month, day);
        var text = full.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (full >= HumbleWindowStart && full <= HumbleWindowEnd)
        {
            problems.Add(new(QualityField.Date, 2, QualityIssue.DateHumble, text));
        }
        else if (context.CountOfDate(full) >= 5)
        {
            problems.Add(new(QualityField.Date, 1, QualityIssue.DateRepeated, text, context.CountOfDate(full)));
        }
        else if (book.CreatedAt != default && DateOnly.FromDateTime(book.CreatedAt.UtcDateTime) == full)
        {
            problems.Add(new(QualityField.Date, 2, QualityIssue.DateSameAsAdded, text));
        }
    }

    private static bool IsValidDate(int year, int month, int day) =>
        year is >= 1 and <= 9999 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month);

    private static void CheckSeries(BookMetadata book, List<QualityProblem> problems)
    {
        if (book.Series is null || string.IsNullOrWhiteSpace(book.Series.Name))
        {
            // A bare "Vol. 3" names no series, so there is nothing to suggest.
            if (TitleParser.GuessSeries(book.Title) is { Name.Length: > 0 } guess)
            {
                problems.Add(new(QualityField.Series, 3, QualityIssue.SeriesInTitleOnly, $"{guess.Name} #{guess.Number}"));
            }

            return;
        }

        if (TitleParser.NumberInTitle(book.Title) is { } inTitle && !SameNumber(inTitle, book.Series.Number))
        {
            problems.Add(new(QualityField.Series, 2, QualityIssue.SeriesNumberMismatch, $"{book.Series.Number ?? "—"}|{inTitle}"));
        }
    }

    private static bool SameNumber(string inTitle, string? seriesNumber)
    {
        var parsedSeries = decimal.TryParse(seriesNumber ?? "0", NumberStyles.Number, CultureInfo.InvariantCulture, out var s);
        var parsedTitle = decimal.TryParse(inTitle, NumberStyles.Number, CultureInfo.InvariantCulture, out var t);
        return parsedSeries && parsedTitle ? s == t : string.Equals(inTitle, seriesNumber, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("[a-z]{18,}")]
    private static partial Regex GluedWords();

    [GeneratedRegex(@"\.(epub|pdf|cbz|cbr)$", RegexOptions.IgnoreCase)]
    private static partial Regex FileExtension();

    // The reference script has a stray backspace character where "\b" was meant; this is the intent.
    [GeneratedRegex(@"^[A-Z]{3,}(?: [A-Z]{2,})+\b")]
    private static partial Regex CapsWords();

    [GeneratedRegex(@"\(prop\)|\bprop\b|\[.*?\]", RegexOptions.IgnoreCase)]
    private static partial Regex TitleJunk();
}
