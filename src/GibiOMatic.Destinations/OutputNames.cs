using System.Globalization;
using System.Text.RegularExpressions;
using GibiOMatic.Core.Domain;

namespace GibiOMatic.Destinations;

/// <summary>
/// Names of the generated copies (SPEC §9.2, §9.3). Templates use <c>{series}</c>, <c>{num}</c>
/// ("v01" for collected editions, "#001" for single issues), <c>{number}</c>, <c>{year}</c>,
/// <c>{title}</c>, <c>{publisher}</c> and <c>{oneshots}</c>; parts left empty disappear, so a book
/// without a year gets no "()".
/// </summary>
public static partial class OutputNames
{
    public const string DefaultOneShotsFolder = "_oneshots";

    /// <summary>Komga: <c>{Série}/{Série} v{Número:00} ({Ano})</c>, no publisher folder (SPEC §9.2).</summary>
    public const string DefaultSeriesTemplate = "{series}/{series} {num} ({year})";

    /// <summary>One-shots go to the one-shots folder: <c>{Pasta de one-shots}/{Título} ({Ano})</c>.</summary>
    public const string DefaultOneShotTemplate = "{oneshots}/{title} ({year})";

    /// <summary>
    /// Path of a book in a Komga library, relative to its root, without extension.
    /// With no one-shots folder, a one-shot stays in a folder named after it.
    /// </summary>
    public static string KomgaPath(BookMetadata book, string? oneShotsFolder = DefaultOneShotsFolder,
        string? seriesTemplate = null, string? oneShotTemplate = null)
    {
        var isOneShot = book.OneShot == true || book.Series is null;
        var template = isOneShot
            ? oneShotTemplate ?? (string.IsNullOrWhiteSpace(oneShotsFolder) ? "{title}/{title} ({year})" : DefaultOneShotTemplate)
            : seriesTemplate ?? DefaultSeriesTemplate;
        return Render(template, book, oneShotsFolder);
    }

    /// <summary>Komga with the default templates and extension, for the Editor preview.</summary>
    public static string Komga(BookMetadata book, string? oneShotsFolder = DefaultOneShotsFolder) =>
        KomgaPath(book, oneShotsFolder) + ".cbz";

    /// <summary>
    /// Kindle: <c>{Série} v{Número:00} ({Ano}).mobi</c>, or <c>{Título} ({Ano}).mobi</c>. The KCC writes a
    /// MOBI that is also an AZW3 and keeps the <c>.mobi</c> extension for compatibility.
    /// </summary>
    public static string Kindle(BookMetadata book) =>
        Render(book.OneShot == true || book.Series is null ? "{title} ({year})" : "{series} {num} ({year})", book, null) + ".mobi";

    public static string Render(string template, BookMetadata book, string? oneShotsFolder)
    {
        var segments = template.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(segment =>
        {
            var text = Token().Replace(segment, m => m.Groups[1].Value.ToLowerInvariant() switch
            {
                "series" => book.Series?.Name ?? book.Title,
                "num" => Number(book),
                "number" => book.Series?.Number ?? string.Empty,
                "year" => book.Date?.Year.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                "title" => book.Title,
                "publisher" => book.Publisher ?? string.Empty,
                "oneshots" => oneShotsFolder ?? string.Empty,
                _ => m.Value,
            });

            // Empty parts leave "()" or "[]" and double spaces behind.
            text = EmptyBrackets().Replace(text, string.Empty);
            return Safe(text);
        }).Where(s => s.Length > 0);
        return string.Join('/', segments);
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
            ? "#" + (whole ? ((int)n).ToString("000", CultureInfo.InvariantCulture) : number)
            : "v" + (whole ? ((int)n).ToString("00", CultureInfo.InvariantCulture) : number);
    }

    /// <summary>A name valid on Windows and Linux.</summary>
    private static string Safe(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(c switch
            {
                ':' => " -",
                '/' or '\\' or '<' or '>' or '"' or '|' or '?' or '*' => " ",
                _ when char.IsControl(c) => " ",
                _ => c.ToString(),
            });
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.');
    }

    [GeneratedRegex(@"\{(\w+)(?::[^}]*)?\}")]
    private static partial Regex Token();

    [GeneratedRegex(@"\(\s*\)|\[\s*\]")]
    private static partial Regex EmptyBrackets();
}
