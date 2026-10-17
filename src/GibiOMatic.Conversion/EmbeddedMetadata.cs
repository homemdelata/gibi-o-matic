using GibiOMatic.Core.Domain;

namespace GibiOMatic.Conversion;

/// <summary>
/// Metadata found inside a book file (ComicInfo.xml, EPUB OPF, PDF Info/XMP). Every field is
/// optional: it only seeds the book's <c>metadata.json</c> on import.
/// </summary>
public sealed record EmbeddedMetadata
{
    public static EmbeddedMetadata Empty { get; } = new();

    public string? Title { get; init; }
    public string? Series { get; init; }
    public string? Number { get; init; }
    public string? Volume { get; init; }
    public PartialDate? Date { get; init; }
    public string? Publisher { get; init; }
    public string? Imprint { get; init; }
    public IReadOnlyList<Credit> Credits { get; init; } = [];
    public string? Summary { get; init; }
    public string? Language { get; init; }
    public string? Isbn { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public int? PageCount { get; init; }

    /// <summary>Kind stated by the file itself (e.g. ComicInfo <c>Format</c>), if any.</summary>
    public BookKind? Kind { get; init; }

    /// <summary>The file was produced by a converter (e.g. Calibre), not by the publisher.</summary>
    public bool LooksConverted { get; init; }

    /// <summary>A reflowable text book rather than a comic (EPUB with few images).</summary>
    public bool IsTextBook { get; init; }
}

/// <summary>What could be read from a book file.</summary>
/// <param name="Cover">Encoded cover image (JPEG/PNG/WebP…), or <c>null</c>.</param>
/// <param name="Warnings">Non-fatal problems (unreadable metadata, no cover…).</param>
public sealed record BookFileContent(EmbeddedMetadata Metadata, byte[]? Cover, IReadOnlyList<string> Warnings);
