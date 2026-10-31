using GibiOMatic.Core.Domain;

namespace GibiOMatic.Core.Text;

/// <summary>
/// What a book title says, piece by piece (<see cref="TitleParser.Parse"/>). Text the parser does
/// not recognise is never dropped: it stays in <see cref="Name"/> or in <see cref="Subtitle"/>.
/// </summary>
public sealed record TitleParts
{
    /// <summary>
    /// The series (text before the number) or, without a number, the first part of the title:
    /// "The Boys" in "The Boys Vol. 11 - Over the Hill…", "RoboCop: Last Stand" in
    /// "RoboCop Vol. 3 - Last Stand #5", "Hellboy" in "Hellboy - Krampusnacht". May be empty ("Vol. 3").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>Text after the number, or after the first part: "Over the Hill…", "Krampusnacht".</summary>
    public string? Subtitle { get; init; }

    /// <summary>"Vol. 11", "Book One", "Omnibus 1" → "11", "1", "1".</summary>
    public string? VolumeNumber { get; init; }

    /// <summary>"#5", "Issue 18" → "5", "18".</summary>
    public string? IssueNumber { get; init; }

    /// <summary>Issue when there is an issue number; TPB or omnibus for a volume number or a format tag ("TP", "HC").</summary>
    public BookKind? Kind { get; init; }

    /// <summary>The year that tells apart series with the same name: "RoboCop (2003) Vol. 1" → 2003.</summary>
    public int? SeriesYear { get; init; }

    /// <summary>The number of the book in its series: the issue number, else the volume number.</summary>
    public string? Number => IssueNumber ?? VolumeNumber;

    /// <summary>Name and subtitle together, as ComicVine writes them: "Hellboy: Krampusnacht".</summary>
    public string FullName => Subtitle is null ? Name : Name.Length == 0 ? Subtitle : $"{Name}: {Subtitle}";
}
