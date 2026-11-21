namespace GibiOMatic.Core.Settings;

/// <summary>
/// Assisted identification with the user's own Claude Code (SPEC §8.4): the <c>claude</c> command
/// line, the user's login and plan. Everything is off until the user turns it on.
/// </summary>
public sealed record ClaudeSettings
{
    /// <summary>The user turned it on (after reading what is sent to Anthropic).</summary>
    public bool Enabled { get; init; }

    /// <summary>The quick search: model and effort (Haiku, low by default).</summary>
    public ClaudeLevelSettings Quick { get; init; } = new("haiku", "low");

    /// <summary>The deep search: model and effort (Sonnet, medium by default).</summary>
    public ClaudeLevelSettings Deep { get; init; } = new("sonnet", "medium");

    /// <summary>A run for a whole series group: the quick level's model, with effort medium.</summary>
    public ClaudeLevelSettings Group { get; init; } = new("haiku", "medium");

    /// <summary>Claude may search the web, limited to <see cref="Sites"/>.</summary>
    public bool UseWeb { get; init; } = true;

    /// <summary>The sites web searches and page fetches are limited to (editable under Advanced).</summary>
    public IReadOnlyList<string> Sites { get; init; } = DefaultSites;

    /// <summary>When it runs: only when the user asks (default), or for every book that goes to the Review.</summary>
    public ClaudeRunMode When { get; init; } = ClaudeRunMode.OnRequest;

    /// <summary>The deep search runs by itself after a quick one that found only the series, a different cover, or a low confidence.</summary>
    public bool DeepAfterQuick { get; init; }

    /// <summary>Claude's answers may be approved without the user, under the conditions of SPEC §8.4. Off by default.</summary>
    public bool AutoApprove { get; init; }

    /// <summary>The minimum confidence for <see cref="AutoApprove"/>.</summary>
    public double AutoApproveMinimum { get; init; } = 0.95;

    public static IReadOnlyList<string> DefaultSites { get; } =
    [
        "comicvine.gamespot.com",
        "leagueofcomicgeeks.com",
        "comics.org",
        "mycomicshop.com",
        "amazon.com",
        "goodreads.com",
        "openlibrary.org",
        "imagecomics.com",
        "idwpublishing.com",
        "boom-studios.com",
        "dynamite.com",
        "darkhorse.com",
        "skybound.com",
        "aftershockcomics.com",
        "vaultcomics.com",
        "en.wikipedia.org",
    ];

    public static IReadOnlyList<string> Models { get; } = ["haiku", "sonnet", "opus"];

    public static IReadOnlyList<string> Efforts { get; } = ["low", "medium", "high"];
}

/// <param name="Model">A Claude Code model alias ("haiku", "sonnet", "opus") or a full model id.</param>
/// <param name="Effort">"low", "medium" or "high".</param>
public sealed record ClaudeLevelSettings(string Model, string Effort);

public enum ClaudeRunMode
{
    /// <summary>Only when the user asks, in the Review (default).</summary>
    OnRequest,

    /// <summary>Also for every book that goes to the Review after the identification of §8.2.</summary>
    Automatic,
}
