namespace GibiOMatic.Core.Settings;

/// <summary>Metadata sources and auto-approval (SPEC §8, §11).</summary>
public sealed record MetadataSettings
{
    /// <summary>Sources in priority order, with on/off.</summary>
    public IReadOnlyList<SourceSetting> Sources { get; init; } = DefaultSources;

    /// <summary>"Always review": never approve automatically.</summary>
    public bool AlwaysReview { get; init; }

    public static IReadOnlyList<SourceSetting> DefaultSources { get; } =
    [
        new("openlibrary", true),
        new("comicvine", true),
        new("googlebooks", false),
    ];

    public bool IsEnabled(string sourceId) => Sources.Any(s => s.Id == sourceId && s.Enabled);

    /// <summary>Every known source exactly once, in the saved order, unknown ids dropped.</summary>
    public MetadataSettings Normalized()
    {
        var known = DefaultSources.Select(s => s.Id).ToHashSet();
        var sources = (Sources ?? [])
            .Where(s => s is not null && known.Contains(s.Id))
            .Concat(DefaultSources)
            .DistinctBy(s => s.Id)
            .ToList();
        return sources.SequenceEqual(Sources ?? []) ? this : this with { Sources = sources };
    }
}

public sealed record SourceSetting(string Id, bool Enabled);
