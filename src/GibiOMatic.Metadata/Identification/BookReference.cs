using System.Text.RegularExpressions;
using GibiOMatic.Core.Text;

namespace GibiOMatic.Metadata.Identification;

public enum BookReferenceKind
{
    /// <summary>Not recognised.</summary>
    Unknown,
    ComicVineIssue,
    ComicVineVolume,

    /// <summary>An ISBN typed or pasted, or the ISBN-10 of an Amazon print edition link.</summary>
    Isbn,

    /// <summary>An Amazon link of a Kindle edition: its ASIN ("B0…") is not an ISBN.</summary>
    AmazonKindle,

    /// <summary>A Goodreads link: it carries only Goodreads' own book number.</summary>
    Goodreads,
}

/// <summary>
/// What the user pasted in the Review to point at the right book: a ComicVine page, an Amazon page
/// (print editions have the ISBN-10 as their ASIN) or an ISBN. Neither Amazon nor Goodreads has an
/// API the app can use (RESEARCH.md), so only what the link itself says is used.
/// </summary>
public sealed partial record BookReference(BookReferenceKind Kind, int ComicVineId = 0, string? Isbn = null)
{
    public static BookReference Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new BookReference(BookReferenceKind.Unknown);
        }

        text = text.Trim();
        if (BookIdentifier.ParseComicVineLink(text) is { } cv)
        {
            return new BookReference(cv.IsIssue ? BookReferenceKind.ComicVineIssue : BookReferenceKind.ComicVineVolume, cv.Id);
        }

        if (AmazonAsin().Match(text) is { Success: true } amazon)
        {
            var asin = amazon.Groups[1].Value.ToUpperInvariant();
            return Core.Text.Isbn.IsValid(asin)
                ? new BookReference(BookReferenceKind.Isbn, Isbn: Core.Text.Isbn.ToIsbn13(asin))
                : new BookReference(BookReferenceKind.AmazonKindle);
        }

        if (GoodreadsLink().IsMatch(text))
        {
            return new BookReference(BookReferenceKind.Goodreads);
        }

        return Core.Text.Isbn.Find(text) is { } isbn
            ? new BookReference(BookReferenceKind.Isbn, Isbn: Core.Text.Isbn.ToIsbn13(isbn))
            : new BookReference(BookReferenceKind.Unknown);
    }

    [GeneratedRegex(@"amazon\.[a-z.]+/(?:.*?/)?(?:dp|gp/product|gp/aw/d|exec/obidos/ASIN|o/ASIN)/([A-Z0-9]{10})(?![A-Z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex AmazonAsin();

    [GeneratedRegex(@"goodreads\.com/", RegexOptions.IgnoreCase)]
    private static partial Regex GoodreadsLink();
}
