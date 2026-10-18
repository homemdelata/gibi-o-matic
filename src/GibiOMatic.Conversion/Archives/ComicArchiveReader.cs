using System.Xml.Linq;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Text;
using SharpCompress.Archives;

namespace GibiOMatic.Conversion.Archives;

/// <summary>Reads CBZ (ZIP) and CBR (RAR) comic archives: ComicInfo.xml and the cover page.</summary>
internal static class ComicArchiveReader
{
    public static BookFileContent Read(string path)
    {
        var warnings = new List<string>();
        using var archive = ArchiveFactory.Open(path);
        var entries = archive.Entries.Where(e => !e.IsDirectory && !IsHidden(e.Key)).ToList();

        var metadata = EmbeddedMetadata.Empty;
        var comicInfo = entries
            .Where(e => Path.GetFileName(e.Key)?.Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase) == true)
            .OrderBy(e => e.Key!.Count(c => c is '/' or '\\'))
            .FirstOrDefault();
        if (comicInfo is not null)
        {
            try
            {
                using var stream = comicInfo.OpenEntryStream();
                metadata = ParseComicInfo(XDocument.Load(stream));
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or InvalidDataException or IOException)
            {
                warnings.Add($"ComicInfo.xml could not be read: {ex.Message}");
            }
        }

        var pages = entries.Where(e => ImageFiles.IsImage(e.Key)).OrderBy(e => e.Key, NaturalStringComparer.Instance).ToList();
        if (metadata.PageCount is null && pages.Count > 0)
        {
            metadata = metadata with { PageCount = pages.Count };
        }

        byte[]? cover = null;
        if (pages.Count > 0)
        {
            using var stream = pages[0].OpenEntryStream();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            cover = buffer.ToArray();
        }
        else
        {
            warnings.Add("The archive has no images.");
        }

        return new BookFileContent(metadata, cover, warnings);
    }

    internal static EmbeddedMetadata ParseComicInfo(XDocument doc)
    {
        var root = doc.Root;
        string? Text(string name) => MetadataText.Clean(root?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value);

        var credits = new List<Credit>();
        credits.AddRange(MetadataText.Credits(CreditRole.Writer, Text("Writer")));
        credits.AddRange(MetadataText.Credits(CreditRole.Artist, Text("Penciller")));
        credits.AddRange(MetadataText.Credits(CreditRole.Inker, Text("Inker")));
        credits.AddRange(MetadataText.Credits(CreditRole.Colorist, Text("Colorist")));
        credits.AddRange(MetadataText.Credits(CreditRole.Letterer, Text("Letterer")));
        credits.AddRange(MetadataText.Credits(CreditRole.Cover, Text("CoverArtist")));
        credits.AddRange(MetadataText.Credits(CreditRole.Editor, Text("Editor")));
        credits.AddRange(MetadataText.Credits(CreditRole.Translator, Text("Translator")));

        var tags = MetadataText.SplitNames(Text("Genre"))
            .Concat(MetadataText.SplitNames(Text("Tags")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new EmbeddedMetadata
        {
            Title = Text("Title"),
            Series = Text("Series"),
            Number = Text("Number"),
            Volume = Text("Volume"),
            Date = MetadataText.ToDate(MetadataText.ParseInt(Text("Year")), MetadataText.ParseInt(Text("Month")), MetadataText.ParseInt(Text("Day"))),
            Publisher = Text("Publisher"),
            Imprint = Text("Imprint"),
            Credits = credits,
            Summary = Text("Summary"),
            Language = Text("LanguageISO"),
            Isbn = Isbn.Find(Text("GTIN")),
            Tags = tags,
            Universes = Universe.Split(Text("SeriesGroup")),
            PageCount = MetadataText.ParseInt(Text("PageCount")) is > 0 and var pages ? pages : null,
            Kind = MetadataText.KindFromFormat(Text("Format")),
        };
    }

    private static bool IsHidden(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return true;
        }

        var normalized = key.Replace('\\', '/');
        return normalized.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)
               || normalized.Split('/').Any(part => part.StartsWith('.'));
    }
}

internal static class ImageFiles
{
    private static readonly HashSet<string> Extensions =
        new([".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"], StringComparer.OrdinalIgnoreCase);

    public static bool IsImage(string? path) => path is not null && Extensions.Contains(Path.GetExtension(path));
}
