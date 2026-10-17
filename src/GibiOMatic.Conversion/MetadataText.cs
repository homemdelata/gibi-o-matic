using System.Globalization;
using System.Text.RegularExpressions;
using GibiOMatic.Core.Domain;

namespace GibiOMatic.Conversion;

/// <summary>Parsing helpers shared by the embedded-metadata readers.</summary>
internal static partial class MetadataText
{
    /// <summary>"Brian K. Vaughan, Fiona Staples" or "A; B" or "A &amp; B" → names.</summary>
    public static IEnumerable<string> SplitNames(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : NameSeparator().Split(value).Select(n => Clean(n)).OfType<string>();

    public static IEnumerable<Credit> Credits(CreditRole role, string? names) =>
        SplitNames(names).Select(n => new Credit(role, n));

    /// <summary>Trimmed text with inner whitespace collapsed, or <c>null</c> when empty.</summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Whitespace().Replace(value, " ").Trim();
    }

    /// <summary>"2012", "2012-10", "2012-10-10" or an ISO timestamp → partial date.</summary>
    public static PartialDate? ParseDate(string? value)
    {
        var m = IsoDate().Match(value ?? string.Empty);
        if (!m.Success)
        {
            return null;
        }

        var year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int? month = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : null;
        int? day = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : null;
        return ToDate(year, month, day);
    }

    public static PartialDate? ToDate(int? year, int? month, int? day)
    {
        if (year is not (>= 1000 and <= 9999))
        {
            return null;
        }

        if (month is not (>= 1 and <= 12))
        {
            return new PartialDate(year.Value);
        }

        return day is >= 1 && day <= DateTime.DaysInMonth(year.Value, month.Value)
            ? new PartialDate(year.Value, month, day)
            : new PartialDate(year.Value, month);
    }

    public static int? ParseInt(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>ComicInfo <c>Format</c> ("TPB", "Omnibus", "Issue"…) → kind.</summary>
    public static BookKind? KindFromFormat(string? format)
    {
        var f = format?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(f))
        {
            return null;
        }

        if (f.Contains("omnibus"))
        {
            return BookKind.Omnibus;
        }

        if (f.Contains("art book") || f.Contains("artbook"))
        {
            return BookKind.Artbook;
        }

        if (f is "tpb" or "trade paperback" or "graphic novel" or "hardcover" or "hc" or "deluxe" or "collection" or "trade")
        {
            return BookKind.Tpb;
        }

        return f is "issue" or "series" or "single issue" or "comic" ? BookKind.Issue : null;
    }

    public static bool MentionsConverter(string? producer) =>
        producer is not null && producer.Contains("calibre", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\s*(?:[,;&]|\band\b)\s*")]
    private static partial Regex NameSeparator();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^\s*(\d{4})(?:-(\d{1,2})(?:-(\d{1,2}))?)?")]
    private static partial Regex IsoDate();
}
