using System.IO.Compression;
using System.Security;
using System.Text;
using SkiaSharp;

namespace GibiOMatic.Testing;

/// <summary>
/// Writes minimal but valid PDF, CBZ, CBR and EPUB files for tests. Pages are solid colours,
/// so a test can tell which page became the cover.
/// </summary>
public static class SyntheticBooks
{
    public static readonly SKColor CoverColor = new(0xC0, 0x30, 0x30);
    public static readonly SKColor PageColor = new(0x30, 0x60, 0xC0);

    /// <summary>A CBZ whose first page (in natural order) is red and the others blue.</summary>
    public static string Cbz(string path, int pages = 3, string? comicInfoXml = null, bool shuffledNames = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in ComicPages(pages, shuffledNames))
        {
            var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(bytes);
        }

        if (comicInfoXml is not null)
        {
            var entry = zip.CreateEntry("ComicInfo.xml");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(comicInfoXml);
        }

        return path;
    }

    /// <summary>A CBR (RAR 4, stored) with the same pages as <see cref="Cbz"/>.</summary>
    public static string Cbr(string path, int pages = 3, string? comicInfoXml = null)
    {
        var entries = ComicPages(pages, shuffledNames: false).ToList();
        if (comicInfoXml is not null)
        {
            entries.Add(("ComicInfo.xml", Encoding.UTF8.GetBytes(comicInfoXml)));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, RarWriter.Store(entries));
        return path;
    }

    public static string ComicInfo(
        string? title = null, string? series = null, string? number = null, int? year = null, int? month = null,
        string? writer = null, string? penciller = null, string? publisher = null, string? gtin = null, string? format = null)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ComicInfo xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n");
        void Add(string name, object? value)
        {
            if (value is not null)
            {
                sb.Append($"  <{name}>{SecurityElement.Escape(value.ToString())}</{name}>\n");
            }
        }

        Add("Title", title);
        Add("Series", series);
        Add("Number", number);
        Add("Year", year);
        Add("Month", month);
        Add("Writer", writer);
        Add("Penciller", penciller);
        Add("Publisher", publisher);
        Add("GTIN", gtin);
        Add("Format", format);
        sb.Append("</ComicInfo>\n");
        return sb.ToString();
    }

    public sealed record EpubOptions
    {
        public string? Title { get; init; } = "Synthetic Epub";
        public IReadOnlyList<(string Name, string? Role)> Creators { get; init; } = [("Ann Writer", "aut")];
        public string? Publisher { get; init; }
        public string? Date { get; init; }
        public string? Isbn { get; init; }
        public string? CalibreSeries { get; init; }
        public string? CalibreSeriesIndex { get; init; }
        public bool FixedLayout { get; init; } = true;
        public bool DeclareCover { get; init; } = true;
        public int Pages { get; init; } = 3;

        /// <summary>Text-only chapters (a novel) instead of one image per page.</summary>
        public bool TextOnly { get; init; }

        public string? Generator { get; init; }
    }

    public static string Epub(string path, EpubOptions? options = null)
    {
        options ??= new EpubOptions();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        void Text(string name, string content, CompressionLevel level = CompressionLevel.Optimal)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name, level).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        void Bytes(string name, byte[] content)
        {
            using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
            stream.Write(content);
        }

        Text("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
        Text("META-INF/container.xml", """
            <?xml version="1.0"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
            </container>
            """);

        var manifest = new StringBuilder();
        var spine = new StringBuilder();
        for (var i = 1; i <= options.Pages; i++)
        {
            var name = $"p{i:D4}";
            if (options.TextOnly)
            {
                Text($"OEBPS/{name}.xhtml", $"<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>Chapter {i}</p></body></html>");
            }
            else
            {
                var color = i == 1 ? CoverColor : PageColor;
                Bytes($"OEBPS/img/{name}.jpg", SyntheticImages.Jpeg(200, 300, color));
                Text($"OEBPS/{name}.xhtml", $"<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><img src=\"img/{name}.jpg\" alt=\"{i}\"/></body></html>");
                var cover = i == 1 && options.DeclareCover ? " properties=\"cover-image\"" : string.Empty;
                manifest.Append($"<item id=\"i{name}\" href=\"img/{name}.jpg\" media-type=\"image/jpeg\"{cover}/>\n");
            }

            manifest.Append($"<item id=\"x{name}\" href=\"{name}.xhtml\" media-type=\"application/xhtml+xml\"/>\n");
            spine.Append($"<itemref idref=\"x{name}\"/>\n");
        }

        var meta = new StringBuilder();
        if (options.Title is not null)
        {
            meta.Append($"<dc:title>{SecurityElement.Escape(options.Title)}</dc:title>\n");
        }

        var n = 0;
        foreach (var (creator, role) in options.Creators)
        {
            n++;
            meta.Append($"<dc:creator id=\"c{n}\">{SecurityElement.Escape(creator)}</dc:creator>\n");
            if (role is not null)
            {
                meta.Append($"<meta refines=\"#c{n}\" property=\"role\" scheme=\"marc:relators\">{role}</meta>\n");
            }
        }

        if (options.Publisher is not null)
        {
            meta.Append($"<dc:publisher>{SecurityElement.Escape(options.Publisher)}</dc:publisher>\n");
        }

        if (options.Date is not null)
        {
            meta.Append($"<dc:date>{options.Date}</dc:date>\n");
        }

        meta.Append($"<dc:identifier id=\"bookid\">{(options.Isbn is null ? "urn:uuid:00000000-0000-0000-0000-000000000001" : "urn:isbn:" + options.Isbn)}</dc:identifier>\n");
        if (options.CalibreSeries is not null)
        {
            meta.Append($"<meta name=\"calibre:series\" content=\"{SecurityElement.Escape(options.CalibreSeries)}\"/>\n");
            meta.Append($"<meta name=\"calibre:series_index\" content=\"{options.CalibreSeriesIndex ?? "1.0"}\"/>\n");
        }

        if (options.FixedLayout)
        {
            meta.Append("<meta property=\"rendition:layout\">pre-paginated</meta>\n");
        }

        if (options.Generator is not null)
        {
            meta.Append($"<meta name=\"generator\" content=\"{SecurityElement.Escape(options.Generator)}\"/>\n");
        }

        Text("OEBPS/content.opf", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid">
            <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
            {meta}</metadata>
            <manifest>
            {manifest}</manifest>
            <spine>
            {spine}</spine>
            </package>
            """);
        return path;
    }

    public sealed record PdfOptions
    {
        public int Pages { get; init; } = 2;
        public string? Title { get; init; }
        public string? Author { get; init; }
        public string? Producer { get; init; }
        public string? Keywords { get; init; }

        /// <summary>An uncompressed XMP packet attached to the catalog.</summary>
        public string? Xmp { get; init; }
    }

    /// <summary>A PDF whose pages are full-page JPEG images (page 1 red, the others blue).</summary>
    public static string Pdf(string path, PdfOptions? options = null)
    {
        options ??= new PdfOptions();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, PdfWriter.Write(options));
        return path;
    }

    public static string Xmp(string? title = null, string? creator = null, string? isbn = null) => $"""
        <?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
        <x:xmpmeta xmlns:x="adobe:ns:meta/">
         <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:prism="http://prismstandard.org/namespaces/basic/2.0/">
           {(title is null ? "" : $"<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{SecurityElement.Escape(title)}</rdf:li></rdf:Alt></dc:title>")}
           {(creator is null ? "" : $"<dc:creator><rdf:Seq><rdf:li>{SecurityElement.Escape(creator)}</rdf:li></rdf:Seq></dc:creator>")}
           {(isbn is null ? "" : $"<prism:isbn>{isbn}</prism:isbn>")}
          </rdf:Description>
         </rdf:RDF>
        </x:xmpmeta>
        <?xpacket end="w"?>
        """;

    private static IEnumerable<(string Name, byte[] Bytes)> ComicPages(int pages, bool shuffledNames)
    {
        // Natural order matters: "page2" comes before "page10" even though it sorts after it as text.
        for (var i = pages; i >= 1; i--)
        {
            var name = shuffledNames ? $"pages/page{i}.jpg" : $"pages/page{i:D3}.jpg";
            yield return (name, SyntheticImages.Jpeg(200, 300, i == 1 ? CoverColor : PageColor));
        }
    }
}
