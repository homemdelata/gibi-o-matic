using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GibiOMatic.Core.Text;

/// <summary>Heuristics over book titles and file names (ported from <c>reference/python/analyze.py</c>).</summary>
public static partial class TitleParser
{
    /// <summary>
    /// Series and number implied by a title such as "Deadly Class, Vol. 11: A Fond Farewell"
    /// (series "Deadly Class", number "11"). The name may be empty ("Vol. 3").
    /// </summary>
    public static (string Name, string Number)? GuessSeries(string title)
    {
        var m = SeriesInTitle().Match(title);
        return m.Success ? (m.Groups[1].Value.Trim(' ', ',', ':', '-'), m.Groups[2].Value) : null;
    }

    /// <summary>The number written in the title ("Vol. 03" → "3", "#12.5" → "12.5"), if any.</summary>
    public static string? NumberInTitle(string title)
    {
        var m = NumberRegex().Match(title);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>True when the title writes a volume number with a leading zero ("Vol. 01").</summary>
    public static bool HasLeadingZeroVolume(string title) => LeadingZeroVolume().IsMatch(title);

    /// <summary>
    /// Turns a downloaded file name into a readable title without guessing words:
    /// "Saga_Vol1_Image.pdf" → "Saga Vol1 Image".
    /// </summary>
    public static string TitleFromFileName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var text = stem.Replace('_', ' ');
        if (!text.Contains(' '))
        {
            text = text.Replace('.', ' ');
        }

        text = Whitespace().Replace(text, " ").Trim();
        return text.Length == 0 ? stem : text;
    }

    /// <summary>
    /// Key used to recognise the same book across formats in F1: lower case, no accents or
    /// punctuation, "vol"/"issue"/"#" markers dropped and numbers without leading zeros.
    /// "Saga, Vol. 01" and "saga_vol1" both become "saga 1".
    /// </summary>
    public static string BookKey(string title)
    {
        var text = RemoveDiacritics(title).ToLowerInvariant();
        text = LetterDigitBoundary().Replace(text, "$1 $2");
        text = NonAlphanumeric().Replace(text, " ");
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !NumberMarkers.Contains(w))
            .Select(w => w.All(char.IsAsciiDigit) ? w.TrimStart('0') is { Length: > 0 } n ? n : "0" : w);
        return string.Join(' ', words);
    }

    private static readonly HashSet<string> NumberMarkers = ["vol", "volume", "book", "issue", "tpb", "no", "nr", "the"];

    public static string RemoveDiacritics(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex(@"^(.*?)(?:,|:|\s-)?\s*(?:Vol(?:ume)?\.?|Book|Issue|#)\s*0*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesInTitle();

    [GeneratedRegex(@"(?:\bVol(?:ume)?\.?|\bBook|\bIssue|\bPart|\bTPB|#)\s*0*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"Vol(?:ume)?\.?\s*0\d", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingZeroVolume();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"([a-z])(\d)")]
    private static partial Regex LetterDigitBoundary();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();
}
