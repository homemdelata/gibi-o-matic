using System.Globalization;
using GibiOMatic.Core.Domain;

namespace GibiOMatic.Destinations;

/// <summary>
/// Default names of the generated copies (SPEC §9.2, §9.3), used by the Editor preview now and by
/// the destinations in F3/F4.
/// </summary>
public static class OutputNames
{
    public const string DefaultOneShotsFolder = "_oneshots";

    /// <summary>
    /// Komga: <c>{Série}/{Série} v{Número:00} ({Ano}).cbz</c>, or
    /// <c>{Pasta de one-shots}/{Título} ({Ano}).cbz</c> for one-shots and books without series.
    /// </summary>
    public static string Komga(BookMetadata book, string? oneShotsFolder = DefaultOneShotsFolder)
    {
        var year = book.Date is { } d ? $" ({d.Year.ToString(CultureInfo.InvariantCulture)})" : string.Empty;
        if (book.OneShot == true || book.Series is null)
        {
            var name = $"{Safe(book.Title)}{year}.cbz";
            return string.IsNullOrEmpty(oneShotsFolder) ? $"{Safe(book.Title)}/{name}" : $"{oneShotsFolder}/{name}";
        }

        var series = Safe(book.Series.Name);
        return $"{series}/{series}{Number(book)}{year}.cbz";
    }

    /// <summary>Kindle: <c>{Série} v{Número:00} ({Ano}).azw3</c>, or <c>{Título} ({Ano}).azw3</c>.</summary>
    public static string Kindle(BookMetadata book)
    {
        var year = book.Date is { } d ? $" ({d.Year.ToString(CultureInfo.InvariantCulture)})" : string.Empty;
        return book.OneShot == true || book.Series is null
            ? $"{Safe(book.Title)}{year}.azw3"
            : $"{Safe(book.Series.Name)}{Number(book)}{year}.azw3";
    }

    /// <summary>" v01" for TPBs and others, " #001" for single issues; empty without a number.</summary>
    private static string Number(BookMetadata book)
    {
        if (book.Series?.Number is not { Length: > 0 } number)
        {
            return string.Empty;
        }

        var whole = decimal.TryParse(number, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) && n == Math.Floor(n);
        return book.Kind == BookKind.Issue
            ? " #" + (whole ? ((int)n).ToString("000", CultureInfo.InvariantCulture) : number)
            : " v" + (whole ? ((int)n).ToString("00", CultureInfo.InvariantCulture) : number);
    }

    private static string Safe(string text)
    {
        var chars = text.Select(c => c switch
        {
            ':' => '-',
            '/' or '\\' or '<' or '>' or '"' or '|' or '?' or '*' => ' ',
            _ => c,
        }).ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
    }
}
