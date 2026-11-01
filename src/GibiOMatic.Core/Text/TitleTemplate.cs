using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GibiOMatic.Core.Text;

/// <summary>What a title template is filled with (SPEC §7.1); any of them may be missing.</summary>
public sealed record TitleFields(string? Series, string? Number, string? Name, int? Year);

/// <summary>
/// The title format of the Review by series (SPEC §7.1): <c>{Series}</c>, <c>{Number}</c>
/// (<c>{Number:00}</c> pads it), <c>{Name}</c> and <c>{Year}</c>. A field that is empty is dropped
/// together with the text that leads to it, so <c>{Series} Vol. {Number}: {Name}</c> gives
/// "Deadly Class Vol. 3" without a name and "Plutona: The End" without a number.
/// </summary>
public static partial class TitleTemplate
{
    public const string Default = "{Series} Vol. {Number}: {Name}";

    public static string Render(string template, TitleFields fields)
    {
        var sb = new StringBuilder();
        var position = 0;
        var dropCloser = false;
        foreach (Match m in Field().Matches(template))
        {
            var lead = template[position..m.Index];
            position = m.Index + m.Length;
            if (dropCloser)
            {
                lead = TrimCloser(lead);
            }

            var value = ValueOf(m.Groups["name"].Value, m.Groups["format"].Value, fields);
            dropCloser = false;
            if (value is null)
            {
                // The lead belongs to the field; a bracket it opens closes after the field.
                dropCloser = lead.Count(c => c is '(' or '[') > lead.Count(c => c is ')' or ']');
                continue;
            }

            if (sb.Length == 0)
            {
                lead = lead.TrimStart(' ', ',', ':', '-', '–', '—', '.');
            }

            sb.Append(lead).Append(value);
        }

        var tail = template[position..];
        sb.Append(dropCloser ? TrimCloser(tail) : tail);
        return Spaces().Replace(sb.ToString(), " ").Trim().TrimEnd(',', ':', '-', '–', '—').Trim();
    }

    /// <summary>The fields a template uses that it does not know ("{Volume}"), to warn while typing.</summary>
    public static IReadOnlyList<string> UnknownFields(string template) =>
        Field().Matches(template).Select(m => m.Groups["name"].Value)
            .Where(n => !Known.Contains(n, StringComparer.OrdinalIgnoreCase)).Distinct().ToList();

    private static readonly string[] Known = ["Series", "Number", "Name", "Year"];

    private static string? ValueOf(string name, string format, TitleFields fields)
    {
        var value = name.ToLowerInvariant() switch
        {
            "series" => fields.Series,
            "number" => Pad(fields.Number, format),
            "name" => fields.Name,
            "year" => fields.Year?.ToString(CultureInfo.InvariantCulture),
            _ => "{" + name + "}",
        };
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>"3" with "00" → "03"; "3.5" → "03.5"; a number that is not one stays as it is.</summary>
    private static string? Pad(string? number, string format)
    {
        if (number is null || format.Length == 0 || !format.All(c => c == '0'))
        {
            return number;
        }

        var dot = number.IndexOf('.');
        var whole = dot < 0 ? number : number[..dot];
        return whole.Length > 0 && whole.All(char.IsAsciiDigit) ? whole.PadLeft(format.Length, '0') + (dot < 0 ? string.Empty : number[dot..]) : number;
    }

    private static string TrimCloser(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.Length > 0 && trimmed[0] is ')' or ']' ? trimmed[1..] : text;
    }

    [GeneratedRegex(@"\{(?<name>\w+)(?::(?<format>[^}]*))?\}")]
    private static partial Regex Field();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}
