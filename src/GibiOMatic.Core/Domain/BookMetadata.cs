using System.Text.Json.Serialization;

namespace GibiOMatic.Core.Domain;

/// <summary>
/// Contents of a book's <c>metadata.json</c> (SPEC §4.3). This file is the source of truth;
/// the SQLite index is only a cache built from it.
/// </summary>
public sealed record BookMetadata
{
    public const int CurrentSchema = 1;

    public int Schema { get; init; } = CurrentSchema;
    public required string Id { get; init; }
    public BookKind Kind { get; init; } = BookKind.Book;
    public required string Title { get; init; }
    public SeriesInfo? Series { get; init; }
    public PartialDate? Date { get; init; }
    public string? Publisher { get; init; }
    public string? Imprint { get; init; }
    public IReadOnlyList<Credit> Credits { get; init; } = [];
    public string? Summary { get; init; }
    public string? Language { get; init; }
    public BookIdentifiers Identifiers { get; init; } = new();
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// A stand-alone book (graphic novel, a series of one book): destinations such as Komga put it
    /// in their one-shots folder (SPEC §9.2). <c>null</c> = not decided.
    /// </summary>
    public bool? OneShot { get; init; }
    public IReadOnlyList<BookFile> Files { get; init; } = [];
    public ReviewInfo Review { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public enum BookKind
{
    Issue,
    Tpb,
    Omnibus,
    Artbook,
    Book,
}

public sealed record SeriesInfo(string Name, string? Number = null, string? Volume = null);

public sealed record PartialDate(int Year, int? Month = null, int? Day = null);

public sealed record Credit(CreditRole Role, string Name);

public enum CreditRole
{
    Writer,
    Artist,
    Inker,
    Colorist,
    Letterer,
    Cover,
    Editor,
    Translator,
    Other,
}

public sealed record BookIdentifiers
{
    public string? Isbn { get; init; }

    [JsonPropertyName("comicvine")]
    public string? ComicVine { get; init; }
}

public sealed record BookFile
{
    public FileRole Role { get; init; } = FileRole.Original;
    public required string Name { get; init; }
    public string? DownloadedAs { get; init; }
    public BookFormat Format { get; init; }
    public required string Sha256 { get; init; }
    public long Bytes { get; init; }

    /// <summary>Number of pages counted in the file at import (null for books imported before this existed).</summary>
    public int? Pages { get; init; }

    public string? Source { get; init; }
    public FileQuality Quality { get; init; } = FileQuality.Unknown;
}

public enum FileRole
{
    /// <summary>The main original of the book.</summary>
    Original,

    /// <summary>The same book in another format, kept alongside the main original.</summary>
    Extra,
}

public enum BookFormat
{
    Pdf,
    Cbz,
    Cbr,
    Epub,
}

public enum FileQuality
{
    Original,
    Converted,
    Unknown,
}

public sealed record ReviewInfo
{
    public ReviewStatus Status { get; init; } = ReviewStatus.Pending;
    public DateTimeOffset? ApprovedAt { get; init; }
    public MatchedBy? MatchedBy { get; init; }
    public double? Confidence { get; init; }
}

public enum ReviewStatus
{
    Pending,
    Approved,
}

public enum MatchedBy
{
    Isbn,

    [JsonStringEnumMemberName("comicvine-volume")]
    ComicVineVolume,

    Manual,

    /// <summary>Approved by the safe auto-approval rules (SPEC §8.2).</summary>
    Auto,
}
