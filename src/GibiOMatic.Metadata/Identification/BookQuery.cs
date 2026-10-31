using System.Text.RegularExpressions;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Text;

namespace GibiOMatic.Metadata.Identification;

/// <summary>
/// What to look for, taken from the book (SPEC §8.2 item 2) with <see cref="TitleParser.Parse"/>:
/// "The Boys Vol. 11 - Over the Hill…" → series "The Boys", number 11, kind TPB, subtitle "Over the
/// Hill…"; "RoboCop Vol. 3 - Last Stand #5" → series "RoboCop: Last Stand", number 5, kind issue.
/// </summary>
public sealed partial record BookQuery
{
    /// <summary>The series, or the first part of a title without a number: the broad search.</summary>
    public required string SearchText { get; init; }
    public string? Series { get; init; }
    public string? Number { get; init; }
    public string? Subtitle { get; init; }
    public BookKind? Kind { get; init; }
    public string? Publisher { get; init; }
    public int? Year { get; init; }
    public string? Isbn { get; init; }
    public string? Language { get; init; }

    /// <summary>The words typed in the Review's search box, as typed; null when the book itself was searched.</summary>
    public string? TypedText { get; init; }

    /// <summary>Year that tells apart series with the same name ("RoboCop (2003)"), compared with the volume's start year.</summary>
    public int? SeriesYear { get; init; }

    /// <summary>Series and subtitle together, as ComicVine names volumes: "Hellboy: Krampusnacht".</summary>
    public string FullName => Subtitle is null ? SearchText : $"{SearchText}: {Subtitle}";

    /// <summary>
    /// What is sent to ComicVine, most specific first: series and subtitle together (collected
    /// editions and mini-series are volumes of their own, named "Series: Subtitle"), then the series
    /// alone, then the part of the series before a colon. Colons are left out: ComicVine's search
    /// does not need them.
    /// </summary>
    public IReadOnlyList<string> SearchTexts
    {
        get
        {
            var texts = new List<string>();
            if (Subtitle is not null)
            {
                texts.Add(Searchable(FullName));
            }

            texts.Add(Searchable(SearchText));
            var colon = SearchText.IndexOf(':');
            if (colon > 0)
            {
                texts.Add(Searchable(SearchText[..colon]));
            }

            return texts.Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>
    /// The text shown in the Review's search box: the series with its number ("Saga vol 2",
    /// "Lumberjanes #18") and subtitle, so that searching again gives the same query.
    /// </summary>
    public string EditableText
    {
        get
        {
            var head = SeriesYear is { } year ? $"{SearchText} ({year})" : SearchText;
            if (Series is not null && Number is not null)
            {
                head = Kind == BookKind.Issue ? $"{head} #{Number}" : $"{head} vol {Number}";
            }

            return Subtitle is null ? head : $"{head}: {Subtitle}";
        }
    }

    /// <param name="overrideText">Text typed by the user in the Review ("Search again").</param>
    public static BookQuery From(BookMetadata book, string? overrideText = null)
    {
        var parts = TitleParser.Parse(overrideText ?? book.Title);
        string searchText;
        string? series;
        string? number;
        BookKind? kind;
        var subtitle = parts.Subtitle;

        if (overrideText is null && book.Series is { Name.Length: > 0 } s)
        {
            searchText = series = s.Name;
            number = s.Number;
            kind = book.Kind is BookKind.Issue or BookKind.Tpb or BookKind.Omnibus ? book.Kind : null;
        }
        else
        {
            series = parts.Number is not null && parts.Name.Length > 0 ? parts.Name : null;
            number = parts.Number;
            kind = parts.Kind;
            if (parts.Name.Length > 0)
            {
                searchText = parts.Name;
            }
            else
            {
                // "Vol. 3 - Something": nothing before the number, so the subtitle is all there is.
                searchText = parts.Subtitle ?? Whitespace().Replace(overrideText ?? book.Title, " ").Trim();
                subtitle = null;
            }
        }

        return new BookQuery
        {
            SearchText = searchText,
            Series = series,
            Number = number,
            Subtitle = string.IsNullOrWhiteSpace(subtitle) ? null : subtitle,
            Kind = kind,
            SeriesYear = parts.SeriesYear,
            TypedText = string.IsNullOrWhiteSpace(overrideText) ? null : overrideText.Trim(),
            Publisher = overrideText is null ? book.Publisher : null,
            Year = overrideText is null ? book.Date?.Year : null,
            Isbn = Core.Text.Isbn.IsValid(book.Identifiers.Isbn) ? Core.Text.Isbn.Normalize(book.Identifiers.Isbn!) : null,
            Language = book.Language,
        };
    }

    private static string Searchable(string text) => Whitespace().Replace(text.Replace(':', ' '), " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
