using System.Globalization;
using GibiOMatic.App.Resources;
using GibiOMatic.Metadata.Assisted;
using GibiOMatic.Metadata.Identification;

namespace GibiOMatic.App.ViewModels;

/// <summary>Texts of the assisted identification shared by the Review by book and by series (SPEC §8.4).</summary>
public static class ClaudeText
{
    public static string Level(AssistedLevel level) => level switch
    {
        AssistedLevel.Quick => Strings.Claude_LevelQuick,
        AssistedLevel.Deep => Strings.Claude_LevelDeep,
        _ => Strings.Claude_LevelGroup,
    };

    /// <summary>"Claude (haiku, quick search)".</summary>
    public static string Source(AssistedEvidence evidence) =>
        string.Format(CultureInfo.CurrentCulture, Strings.Review_ClaudeSource, evidence.Model ?? "?", Level(evidence.Level));

    /// <summary>Why a run gave nothing, for the user.</summary>
    public static string Failure(string? message) => message switch
    {
        "not-installed" => Strings.Claude_NotInstalled,
        "not-logged-in" => string.Format(CultureInfo.CurrentCulture, Strings.Claude_NotLoggedIn, string.Empty).Replace("  ", " ", StringComparison.Ordinal),
        "timeout" => Strings.Claude_Timeout,
        _ => string.Format(CultureInfo.CurrentCulture, Strings.Claude_Failed, message ?? "?"),
    };

    /// <summary>Where Claude said a field came from ("indicia page-004"); null when it did not say.</summary>
    public static string? FieldSource(AssistedEvidence evidence, ProposalField field)
    {
        string[] keys = field switch
        {
            ProposalField.Title => ["title"],
            ProposalField.Series => ["series"],
            ProposalField.Number => ["number"],
            ProposalField.Kind => ["kind"],
            ProposalField.Date => ["releaseDate", "date"],
            ProposalField.Publisher => ["publisher"],
            ProposalField.Writers or ProposalField.Artists or ProposalField.Inkers or ProposalField.Colorists or ProposalField.Letterers
                or ProposalField.CoverArtists => ["credits"],
            ProposalField.Isbn => ["isbn"],
            ProposalField.Summary => ["summary"],
            _ => [],
        };
        return keys.Select(k => evidence.FieldSources.FirstOrDefault(s => string.Equals(s.Key, k, StringComparison.OrdinalIgnoreCase)).Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }
}
