using System.Text.RegularExpressions;

namespace GibiOMatic.Core.Text;

/// <summary>ISBN-10 / ISBN-13 checks.</summary>
public static partial class Isbn
{
    /// <summary>Removes hyphens and spaces and upper-cases a trailing "x".</summary>
    public static string Normalize(string value) =>
        new(value.Where(c => c is not ('-' or ' ')).Select(char.ToUpperInvariant).ToArray());

    /// <summary>True for a well-formed ISBN-10 or ISBN-13 whose check digit matches.</summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var v = Normalize(value);
        if (v.Length == 13 && v.All(char.IsAsciiDigit))
        {
            var sum = 0;
            for (var i = 0; i < 13; i++)
            {
                sum += (v[i] - '0') * (i % 2 == 0 ? 1 : 3);
            }

            return sum % 10 == 0;
        }

        if (v.Length == 10 && v[..9].All(char.IsAsciiDigit) && (char.IsAsciiDigit(v[9]) || v[9] == 'X'))
        {
            var sum = 0;
            for (var i = 0; i < 10; i++)
            {
                sum += (10 - i) * (v[i] == 'X' ? 10 : v[i] - '0');
            }

            return sum % 11 == 0;
        }

        return false;
    }

    /// <summary>"1607066017" → "9781607066019"; an ISBN-13 (or anything else) comes back as it is, normalized.</summary>
    public static string ToIsbn13(string value)
    {
        var v = Normalize(value);
        if (v.Length != 10 || !IsValid(v))
        {
            return v;
        }

        var body = "978" + v[..9];
        var sum = 0;
        for (var i = 0; i < 12; i++)
        {
            sum += (body[i] - '0') * (i % 2 == 0 ? 1 : 3);
        }

        return body + (char)('0' + (10 - sum % 10) % 10);
    }

    /// <summary>Finds the first valid ISBN inside free text such as "urn:isbn:978-1-60706-601-9".</summary>
    public static string? Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (Match m in IsbnCandidate().Matches(text))
        {
            var candidate = Normalize(m.Value);
            if (IsValid(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    [GeneratedRegex(@"(?<![\dX])(?:97[89][\d\- ]{10,14}|\d[\d\- ]{8,11}[\dXx])(?![\d])")]
    private static partial Regex IsbnCandidate();
}
