using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using GibiOMatic.Core.Domain;

namespace GibiOMatic.Metadata;

/// <summary>Tolerant readers for API JSON, where fields may be missing, null, "" or the wrong type.</summary>
internal static partial class Json
{
    public static JsonElement? Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v : null;

    public static string? Str(this JsonElement e, string name) => e.Prop(name) switch
    {
        { ValueKind: JsonValueKind.String } v => Clean(v.GetString()),
        { ValueKind: JsonValueKind.Number } v => v.GetRawText(),
        _ => null,
    };

    public static int? Int(this JsonElement e, string name) => e.Prop(name) switch
    {
        { ValueKind: JsonValueKind.Number } v when v.TryGetInt32(out var n) => n,
        { ValueKind: JsonValueKind.String } v when int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        _ => null,
    };

    public static IEnumerable<JsonElement> Items(this JsonElement e, string name) =>
        e.Prop(name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    public static string? Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : Whitespace().Replace(text, " ").Trim();

    /// <summary>"2022", "2022-06", "2022-06-01", "June 2022", "Jun 1, 2022" → partial date.</summary>
    public static PartialDate? Date(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var iso = IsoDate().Match(text);
        if (iso.Success)
        {
            var year = int.Parse(iso.Groups[1].Value, CultureInfo.InvariantCulture);
            int? month = iso.Groups[2].Success ? int.Parse(iso.Groups[2].Value, CultureInfo.InvariantCulture) : null;
            int? day = iso.Groups[3].Success ? int.Parse(iso.Groups[3].Value, CultureInfo.InvariantCulture) : null;
            return Valid(year, month, day);
        }

        // Open Library writes "June 1, 2022" or "June 2022".
        string[] withDay = ["MMMM d, yyyy", "MMM d, yyyy", "d MMMM yyyy", "d MMM yyyy"];
        string[] withMonth = ["MMMM yyyy", "MMM yyyy"];
        if (DateTime.TryParseExact(text.Trim(), withDay, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withDayValue))
        {
            return new PartialDate(withDayValue.Year, withDayValue.Month, withDayValue.Day);
        }

        if (DateTime.TryParseExact(text.Trim(), withMonth, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withMonthValue))
        {
            return new PartialDate(withMonthValue.Year, withMonthValue.Month);
        }

        var year4 = Year().Match(text);
        return year4.Success ? new PartialDate(int.Parse(year4.Value, CultureInfo.InvariantCulture)) : null;
    }

    private static PartialDate? Valid(int year, int? month, int? day)
    {
        if (year is < 1800 or > 2200)
        {
            return null;
        }

        if (month is not (>= 1 and <= 12))
        {
            return new PartialDate(year);
        }

        return day is >= 1 && day <= DateTime.DaysInMonth(year, month.Value) ? new PartialDate(year, month, day) : new PartialDate(year, month);
    }

    /// <summary>Plain text from the HTML descriptions of ComicVine.</summary>
    public static string? HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var text = Regex.Replace(html, @"<(br|/p|/li|/h\d)\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\n\s*\n+", "\n\n");
        return Clean2(text);

        static string? Clean2(string t) => string.IsNullOrWhiteSpace(t) ? null : t.Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^\s*(\d{4})(?:-(\d{1,2})(?:-(\d{1,2}))?)?\s*$")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"\b(1[89]\d\d|2[01]\d\d)\b")]
    private static partial Regex Year();
}
