using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Text;

namespace GibiOMatic.Conversion.Epub;

/// <summary>Reads an EPUB's OPF package: Dublin Core metadata and the declared cover image.</summary>
internal static partial class EpubReader
{
    public static BookFileContent Read(string path)
    {
        var warnings = new List<string>();
        using var zip = ZipFile.OpenRead(path);

        var opfPath = FindOpfPath(zip);
        if (opfPath is null || Find(zip, opfPath) is not { } opfEntry)
        {
            warnings.Add("The EPUB has no OPF package.");
            return new BookFileContent(EmbeddedMetadata.Empty, null, warnings);
        }

        XDocument opf;
        using (var stream = opfEntry.Open())
        {
            opf = XDocument.Load(stream);
        }

        var package = new OpfPackage(opf, Path.GetDirectoryName(opfPath)?.Replace('\\', '/') ?? string.Empty);
        var metadata = ParseMetadata(package);

        byte[]? cover = null;
        var coverPath = FindCoverPath(zip, package);
        if (coverPath is not null && Find(zip, coverPath) is { } coverEntry)
        {
            using var stream = coverEntry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            cover = buffer.ToArray();
        }
        else
        {
            warnings.Add("No cover image found in the EPUB.");
        }

        return new BookFileContent(metadata, cover, warnings);
    }

    private static string? FindOpfPath(ZipArchive zip)
    {
        if (Find(zip, "META-INF/container.xml") is { } container)
        {
            using var stream = container.Open();
            var doc = XDocument.Load(stream);
            var fullPath = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value;
            if (!string.IsNullOrWhiteSpace(fullPath))
            {
                return fullPath;
            }
        }

        return zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".opf", StringComparison.OrdinalIgnoreCase))?.FullName;
    }

    internal static EmbeddedMetadata ParseMetadata(OpfPackage package)
    {
        var metadata = package.Metadata;
        IEnumerable<XElement> Dc(string name) => metadata.Elements().Where(e => e.Name.LocalName == name);
        string? Meta(string name) => metadata.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "meta" && (string?)e.Attribute("name") == name)?.Attribute("content")?.Value;
        string? Property(string property, string? refines = null) => metadata.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "meta" && (string?)e.Attribute("property") == property
                                 && (refines is null || (string?)e.Attribute("refines") == "#" + refines))?.Value;

        var credits = new List<Credit>();
        foreach (var creator in Dc("creator").Concat(Dc("contributor")))
        {
            var name = MetadataText.Clean(creator.Value);
            if (name is null)
            {
                continue;
            }

            // EPUB 2 puts the MARC role in opf:role; EPUB 3 refines the element with a meta.
            var id = (string?)creator.Attribute("id");
            var marc = creator.Attributes().FirstOrDefault(a => a.Name.LocalName == "role")?.Value
                       ?? (id is null ? null : Property("role", id));
            if (creator.Name.LocalName == "contributor" && marc is null or "bkp")
            {
                continue; // contributors without a creative role are usually tools ("calibre")
            }

            if (RoleOf(marc) is { } role)
            {
                credits.Add(new Credit(role, name));
            }
        }

        var series = Meta("calibre:series");
        var number = Meta("calibre:series_index");
        var collection = metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "meta" && (string?)e.Attribute("property") == "belongs-to-collection");
        if (series is null && collection is not null)
        {
            series = collection.Value;
            var collectionId = (string?)collection.Attribute("id");
            number = collectionId is null ? null : Property("group-position", collectionId);
        }

        var isbn = Dc("identifier").Select(i => Isbn.Find(i.Value)).FirstOrDefault(i => i is not null);

        var generator = Meta("generator") ?? string.Join(' ', Dc("contributor").Select(c => c.Value));
        var fixedLayout = Property("rendition:layout") == "pre-paginated" || Meta("fixed-layout") == "true";
        var spineCount = package.SpineItemIds.Count;
        var imageCount = package.Manifest.Count(i => i.MediaType.StartsWith("image/", StringComparison.Ordinal));

        return new EmbeddedMetadata
        {
            Title = MetadataText.Clean(Dc("title").FirstOrDefault()?.Value),
            Series = MetadataText.Clean(series),
            Number = NormalizeIndex(number),
            Date = MetadataText.ParseDate(Dc("date").FirstOrDefault()?.Value),
            Publisher = MetadataText.Clean(Dc("publisher").FirstOrDefault()?.Value),
            Credits = credits,
            Summary = MetadataText.Clean(StripTags(Dc("description").FirstOrDefault()?.Value)),
            Language = MetadataText.Clean(Dc("language").FirstOrDefault()?.Value),
            Isbn = isbn,
            Tags = Dc("subject").Select(s => MetadataText.Clean(s.Value)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            PageCount = fixedLayout || imageCount >= spineCount ? spineCount : null,
            LooksConverted = MetadataText.MentionsConverter(generator),
            // Comics have about one image per page; text books have far fewer images than chapters.
            IsTextBook = !fixedLayout && spineCount > 0 && imageCount < spineCount * 0.8,
        };
    }

    private static CreditRole? RoleOf(string? marc) => marc?.Trim().ToLowerInvariant() switch
    {
        null or "" or "aut" => CreditRole.Writer,
        "ill" or "art" or "pnc" => CreditRole.Artist,
        "ink" or "inker" => CreditRole.Inker,
        "clr" or "col" => CreditRole.Colorist,
        "ltr" => CreditRole.Letterer,
        "cov" => CreditRole.Cover,
        "edt" => CreditRole.Editor,
        "trl" => CreditRole.Translator,
        _ => CreditRole.Other,
    };

    /// <summary>Calibre writes "1.0" for volume 1.</summary>
    private static string? NormalizeIndex(string? index)
    {
        var clean = MetadataText.Clean(index);
        return clean is not null && clean.EndsWith(".0", StringComparison.Ordinal) ? clean[..^2] : clean;
    }

    private static string? StripTags(string? html) => html is null ? null : System.Net.WebUtility.HtmlDecode(Tags().Replace(html, " "));

    private static string? FindCoverPath(ZipArchive zip, OpfPackage package)
    {
        var images = package.Manifest.Where(i => i.MediaType.StartsWith("image/", StringComparison.Ordinal)).ToList();

        // EPUB 3 cover-image property, then EPUB 2 <meta name="cover">.
        var declared = images.FirstOrDefault(i => i.Properties.Split(' ').Contains("cover-image"));
        if (declared is null)
        {
            var coverId = package.Metadata.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "meta" && (string?)e.Attribute("name") == "cover")?.Attribute("content")?.Value;
            declared = images.FirstOrDefault(i => i.Id == coverId);
        }

        declared ??= images.FirstOrDefault(i => i.Id.Contains("cover", StringComparison.OrdinalIgnoreCase)
                                                || Path.GetFileName(i.Href).Contains("cover", StringComparison.OrdinalIgnoreCase));
        if (declared is not null)
        {
            return package.Resolve(declared.Href);
        }

        // Otherwise the first image referenced by the first page of the spine.
        var firstPage = package.SpineItemIds.Select(id => package.Manifest.FirstOrDefault(m => m.Id == id)).FirstOrDefault(m => m is not null);
        if (firstPage is not null && Find(zip, package.Resolve(firstPage.Href)) is { } pageEntry)
        {
            using var reader = new StreamReader(pageEntry.Open());
            var match = ImageReference().Match(reader.ReadToEnd());
            if (match.Success)
            {
                var pageDir = Path.GetDirectoryName(package.Resolve(firstPage.Href))?.Replace('\\', '/') ?? string.Empty;
                return OpfPackage.Combine(pageDir, match.Groups[1].Value);
            }
        }

        return images.Count > 0 ? package.Resolve(images[0].Href) : null;
    }

    private static ZipArchiveEntry? Find(ZipArchive zip, string path) =>
        zip.GetEntry(path) ?? zip.Entries.FirstOrDefault(e => e.FullName.Equals(path, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex("""(?:src|xlink:href|href)\s*=\s*["']([^"']+\.(?:jpe?g|png|gif|webp))["']""", RegexOptions.IgnoreCase)]
    private static partial Regex ImageReference();
}

internal sealed record ManifestItem(string Id, string Href, string MediaType, string Properties);

internal sealed class OpfPackage
{
    public OpfPackage(XDocument opf, string baseDirectory)
    {
        BaseDirectory = baseDirectory;
        var root = opf.Root ?? throw new InvalidDataException("Empty OPF.");
        Metadata = root.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata") ?? new XElement("metadata");
        Manifest = (root.Elements().FirstOrDefault(e => e.Name.LocalName == "manifest")?.Elements() ?? [])
            .Where(e => e.Name.LocalName == "item")
            .Select(e => new ManifestItem((string?)e.Attribute("id") ?? string.Empty, (string?)e.Attribute("href") ?? string.Empty,
                (string?)e.Attribute("media-type") ?? string.Empty, (string?)e.Attribute("properties") ?? string.Empty))
            .ToList();
        SpineItemIds = (root.Elements().FirstOrDefault(e => e.Name.LocalName == "spine")?.Elements() ?? [])
            .Where(e => e.Name.LocalName == "itemref")
            .Select(e => (string?)e.Attribute("idref"))
            .OfType<string>()
            .ToList();
    }

    public string BaseDirectory { get; }
    public XElement Metadata { get; }
    public IReadOnlyList<ManifestItem> Manifest { get; }
    public IReadOnlyList<string> SpineItemIds { get; }

    public string Resolve(string href) => Combine(BaseDirectory, href);

    /// <summary>Joins a zip directory and a relative, possibly URL-encoded href ("../img/p%201.jpg").</summary>
    public static string Combine(string directory, string href)
    {
        var parts = new List<string>(directory.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var part in Uri.UnescapeDataString(href.Split('#')[0]).Split('/'))
        {
            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (part is not ("." or ""))
            {
                parts.Add(part);
            }
        }

        return string.Join('/', parts);
    }
}
