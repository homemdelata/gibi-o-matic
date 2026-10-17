using System.Text.RegularExpressions;
using GibiOMatic.Conversion;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Text;
using GibiOMatic.Library.Index;

namespace GibiOMatic.Library.Import;

/// <summary>
/// F1 rules to recognise the same book in different files: equal ISBN, or equal title and
/// number once normalised (F2 refines this with online sources).
/// </summary>
public static partial class BookIdentity
{
    public static string KeyOf(string title) => TitleParser.BookKey(title);

    /// <summary>Title for a new book: embedded title, else series and number, else the cleaned file name.</summary>
    public static string TitleFor(EmbeddedMetadata metadata, string fileName)
    {
        if (metadata.Title is { } title)
        {
            return title;
        }

        if (metadata.Series is { } series)
        {
            if (metadata.Number is not { } number)
            {
                return series;
            }

            return metadata.Kind == BookKind.Issue ? $"{series} #{number}" : $"{series}, Vol. {number}";
        }

        return TitleParser.TitleFromFileName(fileName);
    }

    /// <summary>Kind for a new book when the file does not state it.</summary>
    public static BookKind KindFor(EmbeddedMetadata metadata, string title)
    {
        if (metadata.Kind is { } kind)
        {
            return kind;
        }

        if (metadata.IsTextBook)
        {
            return BookKind.Book;
        }

        // Most of a bought comics collection is collected editions; single issues say so.
        return IssueMarker().IsMatch(title) ? BookKind.Issue : BookKind.Tpb;
    }

    /// <summary>The library book that is the same book as <paramref name="analysis"/>, if any.</summary>
    public static CatalogBook? FindSameBook(IEnumerable<CatalogBook> books, ImportAnalysis analysis)
    {
        var isbn = analysis.Metadata.Isbn;
        CatalogBook? byKey = null;
        foreach (var book in books)
        {
            if (isbn is not null && book.Metadata.Identifiers.Isbn is { } other && Isbn.Normalize(other) == isbn)
            {
                return book;
            }

            if (byKey is null && KeyOf(book.Metadata.Title) == analysis.BookKey)
            {
                byKey = book;
            }
        }

        return byKey;
    }

    public static bool IsSameBook(ImportAnalysis a, ImportAnalysis b) =>
        (a.Metadata.Isbn is not null && a.Metadata.Isbn == b.Metadata.Isbn) || a.BookKey == b.BookKey;

    /// <summary>Position of a format in the preference list (lower is better).</summary>
    public static int Rank(BookFormat format, IReadOnlyList<BookFormat> preference)
    {
        var index = -1;
        for (var i = 0; i < preference.Count; i++)
        {
            if (preference[i] == format)
            {
                index = i;
                break;
            }
        }

        return index < 0 ? preference.Count : index;
    }

    /// <summary>
    /// Suggested decision for a file that is the same book as one in the library: replace when the
    /// new file is a preferred format, or the same format while the current file is a conversion.
    /// </summary>
    public static ImportDecision SuggestForSameBook(BookFormat newFormat, bool newLooksConverted, LibraryMatch match, IReadOnlyList<BookFormat> preference)
    {
        if (match.MainFormat is not { } current)
        {
            return ImportDecision.KeepBoth;
        }

        var better = Rank(newFormat, preference) < Rank(current, preference)
                     || (newFormat == current && match.MainQuality == FileQuality.Converted && !newLooksConverted);
        return better ? ImportDecision.Replace : ImportDecision.KeepBoth;
    }

    [GeneratedRegex(@"\bIssue\b|#\s*\d", RegexOptions.IgnoreCase)]
    private static partial Regex IssueMarker();
}
