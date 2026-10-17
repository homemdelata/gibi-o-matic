using System.Text;
using System.Xml.Linq;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Text;
using SkiaSharp;

namespace GibiOMatic.Conversion.Pdf;

/// <summary>Reads a PDF's Info dictionary and XMP packet, and renders page 1 as the cover.</summary>
internal static class PdfReader
{
    /// <summary>Height of the rendered first page; the cover is scaled down from it.</summary>
    private const int CoverRenderHeight = 1600;

    // The XMP packet is usually uncompressed and near the start or the end of the file.
    private const int XmpScanBytes = 4 * 1024 * 1024;

    public static BookFileContent Read(string path)
    {
        var warnings = new List<string>();
        using var doc = PdfDocument.Open(path);

        var xmp = EmbeddedMetadata.Empty;
        try
        {
            xmp = ReadXmp(path) ?? EmbeddedMetadata.Empty;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException)
        {
            warnings.Add($"XMP metadata could not be read: {ex.Message}");
        }

        var producer = $"{doc.GetInfo("Producer")} {doc.GetInfo("Creator")}";
        var authors = MetadataText.Credits(CreditRole.Writer, doc.GetInfo("Author")).ToList();
        var pageCount = doc.PageCount;

        // The Info dictionary has no publication date: CreationDate is when the file was made.
        var metadata = xmp with
        {
            Title = xmp.Title ?? MetadataText.Clean(doc.GetInfo("Title")),
            Credits = xmp.Credits.Count > 0 ? xmp.Credits : authors,
            Summary = xmp.Summary ?? MetadataText.Clean(doc.GetInfo("Subject")),
            Isbn = xmp.Isbn ?? Isbn.Find(doc.GetInfo("Keywords")) ?? Isbn.Find(doc.GetInfo("Subject")),
            PageCount = pageCount,
            LooksConverted = MetadataText.MentionsConverter(producer),
        };

        byte[]? cover = null;
        if (pageCount > 0)
        {
            using var page = doc.RenderPage(0, CoverRenderHeight);
            using var image = SKImage.FromBitmap(page);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
            cover = data.ToArray();
        }
        else
        {
            warnings.Add("The PDF has no pages.");
        }

        return new BookFileContent(metadata, cover, warnings);
    }

    internal static EmbeddedMetadata? ReadXmp(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var packet = FindPacket(stream, 0) ?? (stream.Length > XmpScanBytes ? FindPacket(stream, stream.Length - XmpScanBytes) : null);
        return packet is null ? null : ParseXmp(XDocument.Parse(packet));
    }

    private static string? FindPacket(Stream stream, long offset)
    {
        stream.Position = Math.Max(0, offset);
        var buffer = new byte[(int)Math.Min(XmpScanBytes, stream.Length - stream.Position)];
        stream.ReadExactly(buffer);

        var start = buffer.AsSpan().IndexOf("<x:xmpmeta"u8);
        if (start < 0)
        {
            return null;
        }

        var endTag = "</x:xmpmeta>"u8;
        var end = buffer.AsSpan(start).IndexOf(endTag);
        return end < 0 ? null : Encoding.UTF8.GetString(buffer, start, end + endTag.Length);
    }

    internal static EmbeddedMetadata ParseXmp(XDocument doc)
    {
        // Values live in rdf:Alt / rdf:Seq / rdf:Bag lists, or directly in the element.
        IEnumerable<string> Values(string prefixUri, string name) => doc.Descendants()
            .Where(e => e.Name.LocalName == name && e.Name.NamespaceName == prefixUri)
            .SelectMany(e => e.Descendants().Any(d => d.Name.LocalName == "li")
                ? e.Descendants().Where(d => d.Name.LocalName == "li").Select(d => d.Value)
                : [e.Value])
            .Select(v => MetadataText.Clean(v))
            .OfType<string>();

        const string dc = "http://purl.org/dc/elements/1.1/";
        const string prism = "http://prismstandard.org/namespaces/basic/2.0/";
        const string prism3 = "http://prismstandard.org/namespaces/basic/3.0/";

        var isbn = Values(prism, "isbn").Concat(Values(prism3, "isbn")).Concat(Values(dc, "identifier"))
            .Select(Isbn.Find).FirstOrDefault(i => i is not null);

        return new EmbeddedMetadata
        {
            Title = Values(dc, "title").FirstOrDefault(),
            Credits = Values(dc, "creator").Select(n => new Credit(CreditRole.Writer, n)).ToList(),
            Publisher = Values(dc, "publisher").FirstOrDefault(),
            Summary = Values(dc, "description").FirstOrDefault(),
            Language = Values(dc, "language").FirstOrDefault(l => l != "x-default"),
            Isbn = isbn,
            Tags = Values(dc, "subject").Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }
}
