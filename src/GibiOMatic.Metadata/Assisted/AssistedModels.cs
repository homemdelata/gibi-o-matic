using GibiOMatic.Metadata.Identification;

namespace GibiOMatic.Metadata.Assisted;

/// <summary>The kinds of run of the assisted identification (SPEC §8.4).</summary>
public enum AssistedLevel
{
    /// <summary>ComicVine, the cover and the inner pages; the web only to confirm.</summary>
    Quick,

    /// <summary>A bigger budget; the web fills what ComicVine lacks; the full record with the source of each field.</summary>
    Deep,

    /// <summary>One run for a whole group of the Review by series.</summary>
    Group,
}

/// <summary>What Claude said about a candidate, kept with it and shown in the Review.</summary>
public sealed record AssistedEvidence
{
    public AssistedLevel Level { get; init; }

    /// <summary>The model that answered ("haiku").</summary>
    public string? Model { get; init; }

    public IReadOnlyList<string> Evidence { get; init; } = [];

    public string? Doubts { get; init; }

    /// <summary>Where each field came from: "isbn" → "credits page page-004".</summary>
    public IReadOnlyDictionary<string, string> FieldSources { get; init; } = new Dictionary<string, string>();

    /// <summary>The app compared the book's cover with the record's: true, false, or null when it could not.</summary>
    public bool? CoverMatches { get; init; }

    /// <summary>Claude's explanation of a different cover (variant, limited edition…).</summary>
    public string? CoverExplanation { get; init; }

    /// <summary>ComicVine has the series but no record of this edition: the record was built from the answer.</summary>
    public bool EditionMissing { get; init; }

    /// <summary>The model's own confidence, before the app's checks.</summary>
    public double ModelConfidence { get; init; }

    /// <summary>Fields the Review leaves unticked: credits read from the credits page mix the publisher's staff in.</summary>
    public IReadOnlyList<ProposalField> Unticked { get; init; } = [];

    /// <summary>The <see cref="AssistedPrompts.Version"/> that asked.</summary>
    public int PromptVersion { get; init; }

    public DateTimeOffset At { get; init; }
}

/// <summary>Claude's answer for one book (the <c>structured_output</c> of <see cref="AssistedPrompts.BookSchema"/>).</summary>
public sealed record BookAnswer
{
    public bool Identified { get; init; }
    public bool? IsComic { get; init; }
    public string? Title { get; init; }
    public string? Series { get; init; }
    public string? Number { get; init; }
    public string? Subtitle { get; init; }
    public string? Kind { get; init; }
    public string? Publisher { get; init; }
    public string? Imprint { get; init; }
    public string? ReleaseDate { get; init; }
    public int? PageCount { get; init; }
    public string? Isbn { get; init; }
    public IReadOnlyList<string> OtherIsbns { get; init; } = [];
    public string? Collects { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<AnswerCredit> Credits { get; init; } = [];

    /// <summary>The ComicVine record of the edition (…/4000-…/), or of the series when only it exists (…/4050-…/).</summary>
    public string? ComicVineUrl { get; init; }

    public string? ComicVineSeriesUrl { get; init; }
    public bool? CoverMatches { get; init; }
    public string? CoverExplanation { get; init; }
    public IReadOnlyDictionary<string, string> Sources { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> SitesThatFailed { get; init; } = [];
    public double Confidence { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public string? Doubts { get; init; }
}

public sealed record AnswerCredit(string Role, string Name);

/// <summary>Claude's answer for a group of the Review by series.</summary>
public sealed record GroupAnswer
{
    public GroupAnswerSeries Series { get; init; } = new();

    /// <summary>How ComicVine catalogues the series' collected editions, in a sentence or two.</summary>
    public string? Pattern { get; init; }

    public IReadOnlyList<GroupAnswerBook> Books { get; init; } = [];
    public IReadOnlyList<GroupAnswerOutsider> NotInGroup { get; init; } = [];
    public IReadOnlyList<string> MissingNumbers { get; init; } = [];
    public IReadOnlyList<string> RepeatedNumbers { get; init; } = [];
    public string? Notes { get; init; }
}

public sealed record GroupAnswerSeries
{
    public string? Name { get; init; }
    public string? Publisher { get; init; }
    public int? StartYear { get; init; }
    public string? ComicVineVolumeUrl { get; init; }
}

public sealed record GroupAnswerBook
{
    public string Id { get; init; } = string.Empty;
    public bool Belongs { get; init; } = true;
    public string? Number { get; init; }
    public string? Title { get; init; }
    public string? Kind { get; init; }
    public string? ReleaseDate { get; init; }
    public string? ComicVineIssueUrl { get; init; }
    public bool? CoverMatches { get; init; }
    public double Confidence { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public string? Doubts { get; init; }
}

public sealed record GroupAnswerOutsider
{
    public string Id { get; init; } = string.Empty;
    public string? Reason { get; init; }
    public string? SuggestedSeries { get; init; }
}

/// <summary>What one run of <c>claude -p</c> gave back.</summary>
/// <param name="Output">The <c>structured_output</c>, as JSON text; null when there is none.</param>
/// <param name="Error">Why the run failed, in a few words; null when it worked.</param>
public sealed record ClaudeRunResult(string? Output, string? Error, int Turns, TimeSpan Duration)
{
    public bool Succeeded => Error is null && Output is not null;

    public static ClaudeRunResult Failed(string error, TimeSpan duration) => new(null, error, 0, duration);
}
