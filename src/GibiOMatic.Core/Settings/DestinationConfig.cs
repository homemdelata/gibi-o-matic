namespace GibiOMatic.Core.Settings;

/// <summary>"Quais livros" (SPEC §9.1).</summary>
public enum DestinationScope
{
    AllBooks,
    ChosenBooks,
}

/// <summary>"Quando" (SPEC §9.1).</summary>
public enum DestinationTiming
{
    /// <summary>Copies follow the library as books are approved or edited.</summary>
    Automatic,

    /// <summary>Only when the user asks ("Atualizar agora").</summary>
    Manual,
}

/// <summary>
/// A destination as saved in <c>settings.json</c>. Destinations are per machine (SPEC §9.1): paths
/// like <c>Z:\</c> and a connected Kindle are local. Secrets (the Komga API key) live in the
/// secret store, under <see cref="SecretName"/>.
/// </summary>
public sealed record DestinationConfig
{
    public required string Id { get; init; }

    /// <summary>Type id: "komga" or "kindle".</summary>
    public required string Type { get; init; }

    public required string Name { get; init; }

    public DestinationScope Which { get; init; } = DestinationScope.AllBooks;
    public DestinationTiming When { get; init; } = DestinationTiming.Automatic;

    /// <summary>Books not reviewed yet also go (off by default: decision of 2026-10-17).</summary>
    public bool IncludeUnreviewed { get; init; }

    /// <summary>"Incluir livros que não são quadrinhos": text EPUBs go as they are.</summary>
    public bool IncludeTextBooks { get; init; }

    /// <summary>
    /// Folder the copies are written to: the Komga library root (e.g. <c>Z:\</c>), or for a Kindle the
    /// local folder where copies wait for the device.
    /// </summary>
    public string? Folder { get; init; }

    /// <summary>Komga's "One-Shots directory"; empty = one-shots stay in a folder of their own.</summary>
    public string? OneShotsFolder { get; init; } = "_oneshots";

    /// <summary>Name template of books in a series (advanced); <c>null</c> = default.</summary>
    public string? SeriesTemplate { get; init; }

    /// <summary>Name template of one-shots (advanced); <c>null</c> = default.</summary>
    public string? OneShotTemplate { get; init; }

    /// <summary>Komga server address, optional: to start a scan after changes and check the one-shots folder.</summary>
    public string? ServerUrl { get; init; }

    public string? KomgaLibraryId { get; init; }

    /// <summary>Kindle: KCC device profile (<c>KCS</c> = Colorsoft, <c>KPW5</c> = Paperwhite…).</summary>
    public string? KindleProfile { get; init; }

    /// <summary>Kindle: keep the colours (Colorsoft); off = grey pages.</summary>
    public bool KindleColor { get; init; } = true;

    /// <summary>Kindle: right-to-left reading (manga).</summary>
    public bool KindleManga { get; init; }

    /// <summary>Kindle: double pages (KCC <c>-r</c>: 0 split, 1 rotate, 2 both); null = KCC's default.</summary>
    public int? KindleSplitter { get; init; }

    /// <summary>Kindle: white margins (KCC <c>-c</c>: 0 keep, 1 crop, 2 crop with page numbers); null = KCC's default.</summary>
    public int? KindleCropping { get; init; }

    /// <summary>Kindle: more KCC options (advanced).</summary>
    public string? KccArguments { get; init; }

    /// <summary>Kindle: the KCC command-line tool; null = found on its own (downloaded by the app or on the PATH).</summary>
    public string? KccPath { get; init; }

    /// <summary>Books chosen by hand when <see cref="Which"/> is <see cref="DestinationScope.ChosenBooks"/>.</summary>
    public IReadOnlyList<string> ChosenBookIds { get; init; } = [];

    public string SecretName => $"destination-{Id.ToLowerInvariant()}";
}
