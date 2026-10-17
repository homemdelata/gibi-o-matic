using System.Globalization;
using GibiOMatic.Core.Domain;

namespace GibiOMatic.Core.Quality;

public enum QualityBand
{
    /// <summary>Score below 60 ("Ruim").</summary>
    Bad,

    /// <summary>Score from 60 to 84 ("Revisar").</summary>
    Review,

    /// <summary>Score of 85 or more ("OK").</summary>
    Ok,
}

public enum QualityField
{
    Title,
    Authors,
    Date,
    Series,
    Identifier,
    Publisher,
}

public enum QualityIssue
{
    TitleAllLowercase,
    TitleGluedWords,
    TitleFileName,
    TitleGeneric,
    TitleExtraSpaces,
    TitleAllCaps,
    TitleJunk,
    TitleLeadingZero,
    AuthorUnknown,
    AuthorSingleName,
    AuthorsGlued,
    AuthorBackslash,
    AuthorDuplicate,
    AuthorsTooMany,
    DateMissing,
    DateHumble,
    DateRepeated,
    DateSameAsAdded,
    SeriesInTitleOnly,
    SeriesNumberMismatch,
    InvalidIsbn,
    NoPublisher,
}

/// <param name="Severity">1 (minor) to 3 (serious).</param>
/// <param name="Detail">Value that explains the problem (a name, a date…), shown to the user.</param>
/// <param name="Count">Extra number for some issues (how many books share a date).</param>
public sealed record QualityProblem(QualityField Field, int Severity, QualityIssue Issue, string? Detail = null, int Count = 0);

public sealed record QualityReport(int Score, QualityBand Band, IReadOnlyList<QualityProblem> Problems)
{
    public bool HasProblemIn(QualityField field) => Problems.Any(p => p.Field == field);

    /// <summary>The most serious problem of a field, if any.</summary>
    public QualityProblem? WorstIn(QualityField field) =>
        Problems.Where(p => p.Field == field).OrderByDescending(p => p.Severity).FirstOrDefault();
}

/// <summary>Collection-wide facts some rules need (a date repeated in many books is suspicious).</summary>
public sealed class QualityContext
{
    private readonly Dictionary<DateOnly, int> _dateCounts;

    private QualityContext(Dictionary<DateOnly, int> dateCounts) => _dateCounts = dateCounts;

    public static QualityContext Empty { get; } = new([]);

    public static QualityContext From(IEnumerable<BookMetadata> books)
    {
        var counts = new Dictionary<DateOnly, int>();
        foreach (var book in books)
        {
            if (book.Date is { Month: { } month, Day: { } day } date
                && DateOnly.TryParseExact($"{date.Year:D4}-{month:D2}-{day:D2}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var full))
            {
                counts[full] = counts.GetValueOrDefault(full) + 1;
            }
        }

        return new QualityContext(counts);
    }

    public int CountOfDate(DateOnly date) => _dateCounts.GetValueOrDefault(date);
}
