using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GibiOMatic.Core.Domain;

namespace GibiOMatic.Core.Text;

/// <summary>Heuristics over book titles and file names (ported from <c>reference/python/analyze.py</c>).</summary>
public static partial class TitleParser
{
    /// <summary>
    /// Series and number implied by a title such as "Deadly Class, Vol. 11: A Fond Farewell"
    /// (series "Deadly Class", number "11"). The name may be empty ("Vol. 3"). Null without a number.
    /// </summary>
    public static (string Name, string Number)? GuessSeries(string title) =>
        Parse(title) is { Number: { } number } parts ? (parts.Name, number) : null;

    /// <summary>The number written in the title ("Vol. 03" → "3", "#12.5" → "12.5"), if any.</summary>
    public static string? NumberInTitle(string title) => Parse(title).Number;

    /// <summary>
    /// Splits a title into series, numbers and subtitle. The title is cut into parts at " - " and
    /// ":" (Humble Bundle writes "The Boys Vol. 11 - Over the Hill…" where ComicVine writes ":");
    /// text before the first number is the series, text after it is the subtitle. When a volume
    /// number is followed by an issue number ("RoboCop Vol. 3 - Last Stand #5"), the text between
    /// them names a mini-series and joins the series ("RoboCop: Last Stand", #5). Format tags ("TP",
    /// "HC", "One-Shot") and download leftovers ("(v2)", "[Digital]", "(Free Issue)") are removed; a
    /// year in parentheses is kept as <see cref="TitleParts.SeriesYear"/>.
    /// </summary>
    public static TitleParts Parse(string title)
    {
        var text = FileExtension().Replace(title, string.Empty).Replace('_', ' ');
        int? seriesYear = null;
        if (SeriesYearRegex().Match(text) is { Success: true } year)
        {
            seriesYear = int.Parse(year.Groups[1].Value, CultureInfo.InvariantCulture);
            text = text.Remove(year.Index, year.Length);
        }

        text = Junk().Replace(text, " ");
        var formatTag = TradeTag().IsMatch(text);
        text = TradeTag().Replace(text, " ");
        text = OneShotTag().Replace(text, " ");
        text = Whitespace().Replace(text, " ").Trim();

        var segments = SegmentSeparator().Split(text).Select(TrimPart).Where(s => s.Length > 0).ToList();
        if (segments.Count == 0)
        {
            return new TitleParts { Name = title.Trim(), SeriesYear = seriesYear };
        }

        for (var k = 0; k < segments.Count; k++)
        {
            if (NumberMarker().Match(segments[k]) is not { Success: true } m)
            {
                continue;
            }

            var name = segments.Take(k).ToList();
            AddPart(name, segments[k][..m.Index]);
            var rest = new List<string>();
            AddPart(rest, segments[k][(m.Index + m.Length)..]);
            rest.AddRange(segments.Skip(k + 1));

            string? volume = null;
            string? issue = null;
            if (m.Groups["in"].Success)
            {
                issue = CleanNumber(m.Groups["in"].Value);
            }
            else
            {
                volume = CleanNumber(m.Groups["vn"].Success ? m.Groups["vn"].Value : m.Groups["cn"].Value);
                if (m.Groups["cw"].Value is { Length: > 0 } word && !word.StartsWith("tp", StringComparison.OrdinalIgnoreCase))
                {
                    // "Fathom - Kiani - Omnibus 1": the word is part of the volume's name.
                    if (name.Count == 0)
                    {
                        name.Add(word);
                    }
                    else
                    {
                        name[^1] = $"{name[^1]} {word}";
                    }
                }

                // A later issue number: the text in between names a mini-series of the series.
                for (var j = 0; j < rest.Count; j++)
                {
                    if (IssueMarker().Match(rest[j]) is not { Success: true } im)
                    {
                        continue;
                    }

                    issue = CleanNumber(im.Groups["in"].Value);
                    name.AddRange(rest.Take(j));
                    AddPart(name, rest[j][..im.Index]);
                    var after = new List<string>();
                    AddPart(after, rest[j][(im.Index + im.Length)..]);
                    after.AddRange(rest.Skip(j + 1));
                    rest = after;
                    break;
                }
            }

            var series = string.Join(": ", name);
            return new TitleParts
            {
                Name = series,
                Subtitle = rest.Count == 0 ? null : string.Join(": ", rest),
                VolumeNumber = volume,
                IssueNumber = issue,
                Kind = issue is not null ? BookKind.Issue : OmnibusWord().IsMatch(series) ? BookKind.Omnibus : BookKind.Tpb,
                SeriesYear = seriesYear,
            };
        }

        var plain = new TitleParts
        {
            Name = segments[0],
            Subtitle = segments.Count > 1 ? string.Join(": ", segments.Skip(1)) : null,
            SeriesYear = seriesYear,
        };
        return plain with { Kind = OmnibusWord().IsMatch(plain.FullName) ? BookKind.Omnibus : formatTag ? BookKind.Tpb : null };
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

    private static string TrimPart(string part) => part.Trim(' ', ',', ':', '-', '–', '—');

    private static void AddPart(List<string> parts, string part)
    {
        var trimmed = TrimPart(part);
        if (trimmed.Length > 0)
        {
            parts.Add(trimmed);
        }
    }

    private static string CleanNumber(string number)
    {
        if (WordNumbers.TryGetValue(number.ToLowerInvariant(), out var n))
        {
            return n;
        }

        var trimmed = number.TrimStart('0');
        return trimmed.Length == 0 || trimmed[0] == '.' ? "0" + trimmed : trimmed;
    }

    private static readonly Dictionary<string, string> WordNumbers = new(StringComparer.Ordinal)
    {
        ["one"] = "1", ["two"] = "2", ["three"] = "3", ["four"] = "4", ["five"] = "5", ["six"] = "6",
        ["seven"] = "7", ["eight"] = "8", ["nine"] = "9", ["ten"] = "10", ["eleven"] = "11", ["twelve"] = "12",
    };

    private const string IssuePattern = @"(?:#|\bIssue\s+)\s*(?<in>\d+(?:\.\d+)?)\b(?!\s*-\s*\d)";

    /// <summary>"Vol. 3", "vol11", "v12", "Volume 2", "Book One", "Omnibus 1", "TPB 3", "#5", "Issue 18".</summary>
    [GeneratedRegex(@"\b(?:Vol(?:ume)?\.?|v(?=\d))\s*(?<vn>\d+(?:\.\d+)?)\b"
                    + @"|\bBook\s+(?<vn>\d+|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)\b"
                    + @"|\b(?<cw>Omnibus|Compendium|TPB|TP)(?:\s+Edition)?\s*(?<cn>\d+)\b"
                    + "|" + IssuePattern, RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex NumberMarker();

    [GeneratedRegex(IssuePattern, RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex IssueMarker();

    /// <summary>Where a title is cut into parts: " - " (also en and em dashes) and ":".</summary>
    [GeneratedRegex(@"\s+[-–—]\s+|\s*:\s*")]
    private static partial Regex SegmentSeparator();

    [GeneratedRegex(@"\.(pdf|cbz|cbr|epub|azw3|mobi)$", RegexOptions.IgnoreCase)]
    private static partial Regex FileExtension();

    [GeneratedRegex(@"\((\d{4})\)")]
    private static partial Regex SeriesYearRegex();

    /// <summary>Download leftovers: "[Digital]", "(v2)", "(Free Issue)", "(digital-Empire)", "(prop)", "(c2c)".</summary>
    [GeneratedRegex(@"\[[^\]]*\]|\((?:v\d+|free issues?|digital[^)]*|webrip[^)]*|c2c[^)]*|prop)\)", RegexOptions.IgnoreCase)]
    private static partial Regex Junk();

    /// <summary>Collected-edition tags that ComicVine does not put in names: "TP", "TPB", "HC", "Hardcover", "OGN", "GN".</summary>
    [GeneratedRegex(@"\b(?:TPB?|HC|Hardcover|O?GN)\b(?!\s*\d)", RegexOptions.IgnoreCase)]
    private static partial Regex TradeTag();

    [GeneratedRegex(@"\bOne[- ]Shot\b", RegexOptions.IgnoreCase)]
    private static partial Regex OneShotTag();

    [GeneratedRegex(@"\b(?:omnibus|compendium)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OmnibusWord();

    [GeneratedRegex(@"Vol(?:ume)?\.?\s*0\d", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingZeroVolume();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"([a-z])(\d)")]
    private static partial Regex LetterDigitBoundary();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();
}
