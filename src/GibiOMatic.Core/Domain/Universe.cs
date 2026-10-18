namespace GibiOMatic.Core.Domain;

/// <summary>Universe names (<see cref="BookMetadata.Universes"/>): cleaned and compared without case.</summary>
public static class Universe
{
    /// <summary>
    /// Trimmed, with inner spaces collapsed. Commas separate universes in ComicInfo, so a name
    /// cannot hold one; empty names are dropped (null).
    /// </summary>
    public static string? Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var cleaned = string.Join(' ', name.Replace(',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>"Massive-Verse, Energon Universe" → both names, without repeats.</summary>
    public static IReadOnlyList<string> Split(string? text) =>
        text is null ? [] : Distinct(text.Split(',').Select(Clean).OfType<string>());

    /// <summary>The names in their order, the first spelling of each kept.</summary>
    public static IReadOnlyList<string> Distinct(IEnumerable<string> names) =>
        names.Select(Clean).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static bool Contains(BookMetadata book, string name) => book.Universes.Contains(name, StringComparer.OrdinalIgnoreCase);
}
