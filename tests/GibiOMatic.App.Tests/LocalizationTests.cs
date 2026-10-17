using System.Collections;
using System.Globalization;
using System.Resources;
using GibiOMatic.App.Resources;

namespace GibiOMatic.App.Tests;

public sealed class LocalizationTests
{
    private static HashSet<string> KeysOf(CultureInfo culture)
    {
        var set = Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
                  ?? throw new InvalidOperationException($"No resources for {culture.Name}");
        return set.Cast<DictionaryEntry>().Select(e => (string)e.Key).ToHashSet();
    }

    [Fact]
    public void English_translates_every_portuguese_string()
    {
        var portuguese = KeysOf(CultureInfo.InvariantCulture);
        var english = KeysOf(CultureInfo.GetCultureInfo("en"));

        Assert.NotEmpty(portuguese);
        Assert.Empty(portuguese.Except(english));
        Assert.Empty(english.Except(portuguese));
    }

    [Fact]
    public void Portuguese_is_the_default_and_english_is_used_for_en()
    {
        Assert.Equal("Configurações", Strings.ResourceManager.GetString(nameof(Strings.Nav_Settings), CultureInfo.GetCultureInfo("pt-BR")));
        Assert.Equal("Settings", Strings.ResourceManager.GetString(nameof(Strings.Nav_Settings), CultureInfo.GetCultureInfo("en")));
        Assert.Equal("Settings", Strings.ResourceManager.GetString(nameof(Strings.Nav_Settings), CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void Format_strings_have_the_same_placeholders_in_both_languages()
    {
        var english = CultureInfo.GetCultureInfo("en");
        foreach (var key in KeysOf(CultureInfo.InvariantCulture))
        {
            var pt = Strings.ResourceManager.GetString(key, CultureInfo.InvariantCulture)!;
            var en = Strings.ResourceManager.GetString(key, english)!;
            Assert.True(Placeholders(pt).SetEquals(Placeholders(en)), $"Placeholders differ for {key}");
        }

        static HashSet<string> Placeholders(string s) =>
            System.Text.RegularExpressions.Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).ToHashSet();
    }
}
