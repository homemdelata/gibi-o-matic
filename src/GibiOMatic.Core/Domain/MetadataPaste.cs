using GibiOMatic.Core.Text;

namespace GibiOMatic.Core.Domain;

/// <summary>A group of fields "Paste metadata" can copy from one book to another (SPEC §7).</summary>
public enum PasteField
{
    Title,

    /// <summary>The series' name and volume; the number stays the target's.</summary>
    Series,
    Date,
    Kind,

    /// <summary>Publisher and imprint.</summary>
    Publisher,
    Credits,
    Language,
    Universes,
    OneShot,
    Summary,
}

/// <summary>
/// Copies one book's metadata onto another (SPEC §7, "Copy and paste metadata"): typically a volume
/// the sources don't find, from the volume before it.
/// </summary>
public static class MetadataPaste
{
    /// <summary>What usually stays the same across the volumes of a series.</summary>
    public static IReadOnlyList<PasteField> DefaultFields { get; } =
    [
        PasteField.Series, PasteField.Kind, PasteField.Publisher, PasteField.Credits,
        PasteField.Language, PasteField.Universes, PasteField.OneShot,
    ];

    /// <summary>
    /// The target with the chosen fields of the source. A value the source has empty leaves the
    /// target's alone; credits and universes, when the source has them, replace the target's. The
    /// series number, the identifiers, the files, the tags and the review are always the target's.
    /// </summary>
    public static BookMetadata Apply(BookMetadata source, BookMetadata target, IReadOnlyCollection<PasteField> fields)
    {
        var result = target;
        if (fields.Contains(PasteField.Title) && !string.IsNullOrWhiteSpace(source.Title))
        {
            result = result with { Title = source.Title };
        }

        if (fields.Contains(PasteField.Series) && source.Series is { } series && !string.IsNullOrWhiteSpace(series.Name))
        {
            result = result with { Series = new SeriesInfo(series.Name, NumberOf(target), series.Volume ?? target.Series?.Volume) };
        }

        if (fields.Contains(PasteField.Date) && source.Date is { } date)
        {
            result = result with { Date = date };
        }

        if (fields.Contains(PasteField.Kind))
        {
            result = result with { Kind = source.Kind };
        }

        if (fields.Contains(PasteField.Publisher))
        {
            result = result with { Publisher = source.Publisher ?? target.Publisher, Imprint = source.Imprint ?? target.Imprint };
        }

        if (fields.Contains(PasteField.Credits) && source.Credits.Count > 0)
        {
            result = result with { Credits = source.Credits };
        }

        if (fields.Contains(PasteField.Language) && !string.IsNullOrWhiteSpace(source.Language))
        {
            result = result with { Language = source.Language };
        }

        if (fields.Contains(PasteField.Universes) && source.Universes.Count > 0)
        {
            result = result with { Universes = source.Universes };
        }

        if (fields.Contains(PasteField.OneShot) && source.OneShot is { } oneShot)
        {
            result = result with { OneShot = oneShot };
        }

        if (fields.Contains(PasteField.Summary) && !string.IsNullOrWhiteSpace(source.Summary))
        {
            result = result with { Summary = source.Summary };
        }

        return result;
    }

    /// <summary>The target's own number: the one it has, or the one its title, then its file name, implies.</summary>
    private static string? NumberOf(BookMetadata target)
    {
        if (!string.IsNullOrWhiteSpace(target.Series?.Number))
        {
            return target.Series.Number;
        }

        if (TitleParser.NumberInTitle(target.Title) is { } inTitle)
        {
            return inTitle;
        }

        var file = target.Files.FirstOrDefault(f => f.Role == FileRole.Original) ?? target.Files.FirstOrDefault();
        return file is null ? null : TitleParser.NumberInTitle(TitleParser.TitleFromFileName(file.DownloadedAs ?? file.Name));
    }
}
