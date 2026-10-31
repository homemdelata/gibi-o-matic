using System.Text;
using System.Text.RegularExpressions;
using GibiOMatic.Core.Text;

namespace GibiOMatic.Metadata.Identification;

/// <summary>Comparisons tolerant to case, accents, punctuation and glued words.</summary>
public static partial class TextMatch
{
    // Publishers that only release translations; their volumes are other-language editions
    // (e.g. the German House of Slaughter by Splitter, Deadly Class by Urban Comics or Cross Cult).
    private static readonly HashSet<string> ForeignPublishers = new(
        ["urban comics", "cross cult", "splitter", "panini comics brasil", "panini brasil", "panini verlag", "panini france", "panini espana",
         "planeta comic", "planeta deagostini", "norma editorial", "ecc ediciones", "dargaud", "glenat", "delcourt", "kana", "carlsen",
         "egmont", "mythos editora", "devir", "pipoca nanquim", "saldapress", "salda press", "bao publishing", "j-pop", "hachette",
         "les humanoides associes", "editorial televisa", "levoir", "g floy", "akileos", "dupuis", "le lombard", "casterman",
         "fibra", "egmont polska", "egmont ehapa", "egmont ehapa verlag", "egmont comics", "extrem erfolgreich enterprises",
         "de vliegende hollander", "moztros producciones", "juniorpress bv", "soleil", "editions glenat", "glenat spain",
         "carlsen verlag", "carlsen comics dk", "tokyopop gmbh", "milky way ediciones", "atlantic forlags ab", "meriberica liber",
         "editorial ivrea", "ankama", "abril", "play press", "taifu comics", "pika edition", "magic press", "ediperiodici",
         "sergio bonelli editore", "hjemmet", "semic s a", "tm semic", "catalan communications", "verlag thomas tilsner",
         "editions trip comix", "jbc", "edizioni star comics", "edizioni bd", "mandragora", "walt disney company italia",
         "lucha comics", "letrablanka", "nasha idea", "zanir", "marfil comics", "gredown",
         // Japanese editions (the English ones are Viz, Kodansha Comics USA, Yen Press…)
         "shueisha", "kodansha", "shogakukan", "kadokawa shoten", "akita shoten", "hakusensha", "houbunsha", "ichijinsha",
         "fujimi shobo", "ascii media works", "media factory", "enterbrain", "takeshobo", "tokuma shoten", "nihon bungeisha",
         "shonen gahosha", "tong li publishing co"],
        StringComparer.Ordinal);

    /// <summary>Lower case, no accents, letters and digits separated by single spaces.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lower = TitleParser.RemoveDiacritics(text).ToLowerInvariant().Replace("&", " and ");
        return string.Join(' ', NonWord().Split(lower).Where(w => w.Length > 0));
    }

    /// <summary>
    /// Sørensen–Dice similarity of letter pairs, ignoring spaces, from 0 to 1. Spaces are dropped so
    /// "houseofslaughter" and "House of Slaughter" are equal.
    /// </summary>
    public static double Similarity(string? a, string? b)
    {
        var x = Normalize(a).Replace(" ", string.Empty, StringComparison.Ordinal);
        var y = Normalize(b).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (x.Length == 0 || y.Length == 0)
        {
            return 0;
        }

        if (x == y)
        {
            return 1;
        }

        if (x.Length < 2 || y.Length < 2)
        {
            return 0;
        }

        var pairs = new Dictionary<int, int>();
        for (var i = 0; i < x.Length - 1; i++)
        {
            var key = (x[i] << 16) | x[i + 1];
            pairs[key] = pairs.GetValueOrDefault(key) + 1;
        }

        var common = 0;
        for (var i = 0; i < y.Length - 1; i++)
        {
            var key = (y[i] << 16) | y[i + 1];
            if (pairs.TryGetValue(key, out var count) && count > 0)
            {
                pairs[key] = count - 1;
                common++;
            }
        }

        return 2.0 * common / (x.Length - 1 + y.Length - 1);
    }

    /// <summary>"Image Comics" ≈ "Image", "BOOM! Studios" ≈ "Boom", "IDW Publishing" ≈ "IDW".</summary>
    public static string NormalizePublisher(string? publisher)
    {
        var words = Normalize(publisher).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w is not ("comics" or "comic" or "publishing" or "publications" or "studios" or "studio" or "entertainment"
                or "inc" or "llc" or "ltd" or "press" or "books" or "the" or "group" or "media"));
        return string.Join(' ', words);
    }

    public static bool SamePublisher(string? a, string? b)
    {
        var x = NormalizePublisher(a);
        var y = NormalizePublisher(b);
        return x.Length > 0 && (x == y || x.StartsWith(y + " ", StringComparison.Ordinal) || y.StartsWith(x + " ", StringComparison.Ordinal));
    }

    public static bool IsForeignPublisher(string? publisher)
    {
        var name = Normalize(publisher);
        return name.Length > 0 && (ForeignPublishers.Contains(name) || ForeignPublishers.Contains(NormalizePublisher(publisher)));
    }

    /// <summary>Numbers are compared by value: "01" = "1", "1.0" = "1".</summary>
    public static bool SameNumber(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        return decimal.TryParse(a, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var x)
               && decimal.TryParse(b, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var y)
            ? x == y
            : string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Number of a collected edition written in an issue name: "Volume 11", "Vol. 1", "Book Two", "TPB 3",
    /// "Part One: …"; not "The Kingdom, Part 2".
    /// </summary>
    public static string? CollectedNumber(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var m = CollectedNumberRegex().Match(name);
        if (!m.Success)
        {
            return null;
        }

        var word = m.Groups[1].Value.ToLowerInvariant();
        return WordNumbers.TryGetValue(word, out var n) ? n : word.TrimStart('0') is { Length: > 0 } t ? t : "0";
    }

    private static readonly Dictionary<string, string> WordNumbers = new(StringComparer.Ordinal)
    {
        ["one"] = "1", ["two"] = "2", ["three"] = "3", ["four"] = "4", ["five"] = "5", ["six"] = "6",
        ["seven"] = "7", ["eight"] = "8", ["nine"] = "9", ["ten"] = "10", ["eleven"] = "11", ["twelve"] = "12",
    };

    public static string Join(params string?[] parts)
    {
        var sb = new StringBuilder();
        foreach (var part in parts.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(part);
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonWord();

    // "Part" only opens a name ("Part One: …"): "The Kingdom, Part 2" is a chapter of a story.
    [GeneratedRegex(@"(?:^\s*part|\b(?:vol(?:ume)?|book|tpb))\.?\s*(\d+|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CollectedNumberRegex();
}
