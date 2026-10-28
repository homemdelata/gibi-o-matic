using System.Text.RegularExpressions;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Text;

namespace GibiOMatic.Metadata.Identification;

/// <summary>
/// What to look for, taken from the book (SPEC §8.2 item 2): "Deadly Class, Vol. 11: A Fond Farewell"
/// → series "Deadly Class", number 11, kind TPB, subtitle "A Fond Farewell".
/// </summary>
public sealed partial record BookQuery
{
    public required string SearchText { get; init; }
    public string? Series { get; init; }
    public string? Number { get; init; }
    public string? Subtitle { get; init; }
    public BookKind? Kind { get; init; }
    public string? Publisher { get; init; }
    public int? Year { get; init; }
    public string? Isbn { get; init; }
    public string? Language { get; init; }

    /// <summary>
    /// The text shown in the Review's search box: the series with its number ("Saga vol 2",
    /// "Lumberjanes #18") and subtitle, so that searching again keeps them. Only the series goes
    /// to ComicVine; the number picks among the results.
    /// </summary>
    public string EditableText
    {
        get
        {
            if (Series is null || Number is null)
            {
                return SearchText;
            }

            var text = Kind == BookKind.Issue ? $"{Series} #{Number}" : $"{Series} vol {Number}";
            return Subtitle is null ? text : $"{text}: {Subtitle}";
        }
    }

    /// <param name="overrideText">Text typed by the user in the Review ("Search again").</param>
    public static BookQuery From(BookMetadata book, string? overrideText = null)
    {
        var title = Clean(overrideText ?? book.Title);
        string? series = null;
        string? number = null;
        BookKind? kind = null;

        if (overrideText is null && book.Series is { Name.Length: > 0 } s)
        {
            series = s.Name;
            number = s.Number;
            kind = book.Kind is BookKind.Issue or BookKind.Tpb or BookKind.Omnibus ? book.Kind : null;
        }
        else if (TitleParser.GuessSeries(title) is { Name.Length: > 0 } guess)
        {
            series = guess.Name;
            number = guess.Number;
            kind = IssueMarker().IsMatch(title) ? BookKind.Issue : BookKind.Tpb;
        }

        var colon = title.IndexOf(':');
        var subtitle = colon > 0 ? title[(colon + 1)..].Trim() : null;
        if (subtitle is not null && TitleParser.GuessSeries(subtitle) is not null)
        {
            subtitle = null; // "Lumberjanes: Issue 18" has no subtitle
        }

        return new BookQuery
        {
            SearchText = series ?? (colon > 0 ? title[..colon].Trim() : title),
            Series = series,
            Number = number,
            Subtitle = string.IsNullOrWhiteSpace(subtitle) ? null : subtitle,
            Kind = kind,
            Publisher = overrideText is null ? book.Publisher : null,
            Year = overrideText is null ? book.Date?.Year : null,
            Isbn = Core.Text.Isbn.IsValid(book.Identifiers.Isbn) ? Core.Text.Isbn.Normalize(book.Identifiers.Isbn!) : null,
            Language = book.Language,
        };
    }

    /// <summary>Removes file-name leftovers: "_", extensions, "(prop)", "[Digital]".</summary>
    public static string Clean(string title)
    {
        var text = FileExtension().Replace(title, string.Empty).Replace('_', ' ');
        text = Junk().Replace(text, " ");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    [GeneratedRegex(@"\bIssue\b|#\s*\d", RegexOptions.IgnoreCase)]
    private static partial Regex IssueMarker();

    [GeneratedRegex(@"\.(pdf|cbz|cbr|epub)$", RegexOptions.IgnoreCase)]
    private static partial Regex FileExtension();

    [GeneratedRegex(@"\(prop\)|\[[^\]]*\]|\((digital|webrip|c2c)[^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex Junk();
}
