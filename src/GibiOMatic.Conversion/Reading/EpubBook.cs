using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using GibiOMatic.Conversion.Epub;

namespace GibiOMatic.Conversion.Reading;

/// <summary>A chapter of a text EPUB (a document of the spine).</summary>
/// <param name="Path">Path of the document inside the EPUB ("OEBPS/text/ch01.xhtml").</param>
/// <param name="Title">Its title in the table of contents, when it has one.</param>
public sealed record EpubChapter(string Path, string? Title);

/// <summary>An entry of the table of contents; <paramref name="Depth"/> 0 is the top level.</summary>
public sealed record EpubTocEntry(string Title, int Chapter, string? Fragment, int Depth);

/// <summary>
/// A text EPUB opened for reading: its chapters, table of contents and files. The content is read
/// straight from the archive; nothing is extracted to disk.
/// </summary>
public sealed class EpubBook : IDisposable
{
    private readonly ZipArchive _zip;

    private EpubBook(ZipArchive zip, string? title, IReadOnlyList<EpubChapter> chapters, IReadOnlyList<EpubTocEntry> toc)
    {
        _zip = zip;
        Title = title;
        Chapters = chapters;
        Toc = toc;
    }

    public string? Title { get; }

    public IReadOnlyList<EpubChapter> Chapters { get; }

    public IReadOnlyList<EpubTocEntry> Toc { get; }

    public static EpubBook Open(string path)
    {
        var zip = ZipFile.OpenRead(path);
        try
        {
            var (package, opf) = LoadPackage(zip) ?? throw new InvalidDataException("The EPUB has no OPF package.");
            var documents = package.SpineItemIds
                .Select(id => package.Manifest.FirstOrDefault(m => m.Id == id))
                .OfType<ManifestItem>()
                .Select(m => package.Resolve(m.Href))
                .Where(p => Find(zip, p) is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (documents.Count == 0)
            {
                throw new InvalidDataException("The EPUB has no chapters.");
            }

            var toc = ReadToc(zip, package, opf, documents);
            var chapters = documents
                .Select((p, i) => new EpubChapter(p, toc.FirstOrDefault(t => t.Chapter == i)?.Title))
                .ToList();
            var title = package.Metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value.Trim();
            return new EpubBook(zip, string.IsNullOrEmpty(title) ? null : title, chapters, toc);
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    /// <summary>The chapter that is the document at <paramref name="path"/>, or -1.</summary>
    public int ChapterOf(string path) =>
        Chapters.Select((c, i) => (c, i)).FirstOrDefault(x => x.c.Path.Equals(path, StringComparison.OrdinalIgnoreCase), (null!, -1)).Item2;

    /// <summary>A file of the book, or <c>null</c> when there is no such file.</summary>
    public byte[]? Read(string path)
    {
        lock (_zip)
        {
            if (Find(_zip, path) is not { } entry)
            {
                return null;
            }

            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
    }

    public void Dispose()
    {
        lock (_zip)
        {
            _zip.Dispose();
        }
    }

    /// <summary>
    /// A comic (pages are images) rather than a text book: fixed layout, or about one image per
    /// document of the spine. Same rule as the import (EpubReader).
    /// </summary>
    internal static bool LooksLikeComic(ZipArchive zip, int pageImages)
    {
        if (LoadPackage(zip) is not { } loaded)
        {
            return pageImages > 0;
        }

        var package = loaded.Package;

        var fixedLayout = package.Metadata.Elements().Any(e => e.Name.LocalName == "meta"
            && (((string?)e.Attribute("property") == "rendition:layout" && e.Value.Trim() == "pre-paginated")
                || ((string?)e.Attribute("name") == "fixed-layout" && (string?)e.Attribute("content") == "true")));
        return fixedLayout || pageImages >= package.SpineItemIds.Count * 0.8;
    }

    private static (OpfPackage Package, XDocument Opf)? LoadPackage(ZipArchive zip)
    {
        string? opfPath = null;
        if (Find(zip, "META-INF/container.xml") is { } container)
        {
            using var stream = container.Open();
            opfPath = LoadXml(stream).Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value;
        }

        opfPath ??= zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".opf", StringComparison.OrdinalIgnoreCase))?.FullName;
        if (opfPath is null || Find(zip, opfPath) is not { } opfEntry)
        {
            return null;
        }

        using var opfStream = opfEntry.Open();
        var opf = LoadXml(opfStream);
        return (new OpfPackage(opf, Path.GetDirectoryName(opfPath)?.Replace('\\', '/') ?? string.Empty), opf);
    }

    /// <summary>EPUB 3 navigation document, or the EPUB 2 NCX.</summary>
    private static List<EpubTocEntry> ReadToc(ZipArchive zip, OpfPackage package, XDocument opf, List<string> documents)
    {
        var result = new List<EpubTocEntry>();

        void Add(string title, string tocDirectory, string? href, int depth)
        {
            title = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (title.Length == 0 || string.IsNullOrWhiteSpace(href))
            {
                return;
            }

            var hash = href.IndexOf('#');
            var fragment = hash >= 0 ? Uri.UnescapeDataString(href[(hash + 1)..]) : null;
            var path = OpfPackage.Combine(tocDirectory, href);
            var chapter = documents.FindIndex(d => d.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (chapter >= 0)
            {
                result.Add(new EpubTocEntry(title, chapter, string.IsNullOrEmpty(fragment) ? null : fragment, depth));
            }
        }

        try
        {
            var nav = package.Manifest.FirstOrDefault(m => m.Properties.Split(' ').Contains("nav"));
            if (nav is not null && Find(zip, package.Resolve(nav.Href)) is { } navEntry)
            {
                var navPath = package.Resolve(nav.Href);
                var navDirectory = Path.GetDirectoryName(navPath)?.Replace('\\', '/') ?? string.Empty;
                XDocument doc;
                using (var stream = navEntry.Open())
                {
                    doc = LoadXml(stream);
                }

                var navs = doc.Descendants().Where(e => e.Name.LocalName == "nav").ToList();
                var tocNav = navs.FirstOrDefault(n => n.Attributes().Any(a => a.Name.LocalName == "type" && a.Value.Split(' ').Contains("toc")))
                             ?? navs.FirstOrDefault();
                if (tocNav?.Elements().FirstOrDefault(e => e.Name.LocalName == "ol") is { } list)
                {
                    void Walk(XElement ol, int depth)
                    {
                        foreach (var li in ol.Elements().Where(e => e.Name.LocalName == "li"))
                        {
                            var link = li.Elements().FirstOrDefault(e => e.Name.LocalName is "a" or "span");
                            if (link is not null)
                            {
                                Add(link.Value, navDirectory, (string?)link.Attribute("href"), depth);
                            }

                            if (li.Elements().FirstOrDefault(e => e.Name.LocalName == "ol") is { } children)
                            {
                                Walk(children, depth + 1);
                            }
                        }
                    }

                    Walk(list, 0);
                }
            }

            if (result.Count == 0)
            {
                var spine = opf.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "spine");
                var ncxId = (string?)spine?.Attribute("toc");
                var ncx = package.Manifest.FirstOrDefault(m => m.Id == ncxId)
                          ?? package.Manifest.FirstOrDefault(m => m.MediaType == "application/x-dtbncx+xml");
                if (ncx is not null && Find(zip, package.Resolve(ncx.Href)) is { } ncxEntry)
                {
                    var ncxDirectory = Path.GetDirectoryName(package.Resolve(ncx.Href))?.Replace('\\', '/') ?? string.Empty;
                    XDocument doc;
                    using (var stream = ncxEntry.Open())
                    {
                        doc = LoadXml(stream);
                    }

                    void Walk(XElement parent, int depth)
                    {
                        foreach (var point in parent.Elements().Where(e => e.Name.LocalName == "navPoint"))
                        {
                            var label = point.Elements().FirstOrDefault(e => e.Name.LocalName == "navLabel")?.Value ?? string.Empty;
                            var src = (string?)point.Elements().FirstOrDefault(e => e.Name.LocalName == "content")?.Attribute("src");
                            Add(label, ncxDirectory, src, depth);
                            Walk(point, depth + 1);
                        }
                    }

                    if (doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "navMap") is { } navMap)
                    {
                        Walk(navMap, 0);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException)
        {
            // A broken table of contents: the chapters can still be read in order.
        }

        return result;
    }

    /// <summary>XML without DTD processing (no external entities).</summary>
    internal static XDocument LoadXml(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        return XDocument.Load(reader);
    }

    private static ZipArchiveEntry? Find(ZipArchive zip, string path) =>
        zip.GetEntry(path) ?? zip.Entries.FirstOrDefault(e => e.FullName.Equals(path, StringComparison.OrdinalIgnoreCase));
}
