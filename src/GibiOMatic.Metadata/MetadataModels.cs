using GibiOMatic.Core.Domain;

namespace GibiOMatic.Metadata;

public static class SourceIds
{
    public const string OpenLibrary = "openlibrary";
    public const string ComicVine = "comicvine";
    public const string GoogleBooks = "googlebooks";

    public static IReadOnlyList<string> All { get; } = [OpenLibrary, ComicVine, GoogleBooks];
}

/// <summary>What one source says about a book. Every field is optional.</summary>
public sealed record MetadataRecord
{
    public required string Source { get; init; }
    public string? Title { get; init; }
    public string? SeriesName { get; init; }
    public string? Number { get; init; }
    public BookKind? Kind { get; init; }
    public PartialDate? Date { get; init; }
    public string? Publisher { get; init; }
    public string? Imprint { get; init; }
    public IReadOnlyList<Credit> Credits { get; init; } = [];
    public string? Summary { get; init; }
    public string? Language { get; init; }
    public string? Isbn { get; init; }
    public int? PageCount { get; init; }

    /// <summary>ComicVine issue id as written in <c>metadata.json</c> ("4000-937068").</summary>
    public string? ComicVineId { get; init; }

    public int? ComicVineVolumeId { get; init; }

    /// <summary>Number of issues of the ComicVine volume; 1 means a collected edition or a one-shot.</summary>
    public int? VolumeIssueCount { get; init; }

    /// <summary>A book that stands alone (no other book in its series).</summary>
    public bool IsOneShot { get; init; }

    /// <summary>False while only the search summary is known (credits and synopsis come with the details).</summary>
    public bool IsDetailed { get; init; } = true;

    public string? SourceUrl { get; init; }
}

/// <summary>What made a candidate match; shown as "94% · título + número + editora".</summary>
public enum MatchReason
{
    Isbn,
    Title,
    Number,
    Publisher,
    Year,
    Kind,
    SecondSource,
}

/// <summary>A possible identification of a book, with the confidence of the match (0–1).</summary>
public sealed record MetadataCandidate(MetadataRecord Record, double Confidence, IReadOnlyList<MatchReason> Reasons)
{
    /// <summary>The file's ISBN matches this record.</summary>
    public bool IsbnMatch => Reasons.Contains(MatchReason.Isbn);

    /// <summary>Another source independently agrees on title, number and publisher.</summary>
    public bool ConfirmedBySecondSource => Reasons.Contains(MatchReason.SecondSource);
}
