using System.Globalization;
using System.Xml.Linq;

namespace GibiOMatic.App.Tests;

/// <summary>
/// F6 accessibility review, kept as tests: text contrast of both themes (WCAG AA) and a name for
/// every button a screen reader would otherwise announce as just "button".
/// </summary>
public sealed class AccessibilityTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string AppFolder()
    {
        var folder = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(folder, "GibiOMatic.sln")))
        {
            folder = Path.GetDirectoryName(folder) ?? throw new InvalidOperationException("Repository not found.");
        }

        return Path.Combine(folder, "src", "GibiOMatic.App");
    }

    private static Dictionary<string, string> Colors(string theme)
    {
        var tokens = XDocument.Load(Path.Combine(AppFolder(), "Themes", "Tokens.axaml"));
        var dictionary = tokens.Descendants(Avalonia + "ResourceDictionary").Single(d => (string?)d.Attribute(Xaml + "Key") == theme);
        return dictionary.Elements(Avalonia + "Color").ToDictionary(c => (string)c.Attribute(Xaml + "Key")!, c => c.Value.Trim());
    }

    private static double Luminance(string hex)
    {
        var rgb = hex.TrimStart('#');
        rgb = rgb.Length == 8 ? rgb[2..] : rgb; // #AARRGGBB: the tokens used for text are opaque
        double Channel(int i)
        {
            var c = int.Parse(rgb.Substring(i * 2, 2), NumberStyles.HexNumber) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(0) + 0.7152 * Channel(1) + 0.0722 * Channel(2);
    }

    private static double Contrast(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Text_has_aa_contrast_on_every_background(string theme)
    {
        var colors = Colors(theme);
        string[] texts = ["TextColor", "TextSecondaryColor", "TextMutedColor", "TextFaintColor", "AccentTextColor", "OkTextColor", "WarnTextColor", "BadTextColor"];
        string[] backgrounds = ["BgColor", "SurfaceColor", "CardColor", "InputColor", "HoverColor"];

        var failures = (from text in texts
                        from background in backgrounds
                        let ratio = Contrast(colors[text], colors[background])
                        where ratio < 4.5
                        select $"{text} on {background}: {ratio:0.00}").ToList();
        // Text on the accent (primary buttons, badges).
        if (Contrast(colors["OnAccentColor"], colors["AccentColor"]) is var onAccent && onAccent < 4.5)
        {
            failures.Add($"OnAccentColor on AccentColor: {onAccent:0.00}");
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void Both_themes_define_the_same_colors()
    {
        Assert.Equal(Colors("Dark").Keys.Order(), Colors("Light").Keys.Order());
    }

    [Fact]
    public void Buttons_without_text_have_an_accessible_name()
    {
        var missing = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(AppFolder(), "Views"), "*.axaml"))
        {
            var view = XDocument.Load(file);
            foreach (var button in view.Descendants().Where(e => e.Name.LocalName is "Button" or "ToggleButton" or "RepeatButton"))
            {
                var named = button.Attributes().Any(a => a.Name.LocalName is "Content" or "AutomationProperties.Name");
                var hasText = button.Descendants().Any(e => e.Name.LocalName is "TextBlock" or "SelectableTextBlock" or "Run");
                if (!named && !hasText)
                {
                    missing.Add($"{Path.GetFileName(file)}: {(string?)button.Attribute("Command") ?? button.ToString()[..Math.Min(80, button.ToString().Length)]}");
                }
            }
        }

        Assert.Empty(missing);
    }
}
