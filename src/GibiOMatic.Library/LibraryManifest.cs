namespace GibiOMatic.Library;

/// <summary>
/// Contents of <c>gibiomatic-library.json</c>. Holds only what belongs to the collection
/// itself, independent of the machine (SPEC §11).
/// </summary>
public sealed record LibraryManifest
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public required string Id { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
