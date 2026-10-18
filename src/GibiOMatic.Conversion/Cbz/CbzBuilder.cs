using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GibiOMatic.Conversion.Archives;
using GibiOMatic.Conversion.Epub;
using GibiOMatic.Conversion.Pdf;
using GibiOMatic.Core.Domain;
using SharpCompress.Archives;

namespace GibiOMatic.Conversion.Cbz;

/// <summary>What was built.</summary>
/// <param name="OriginalPages">PDF pages copied byte for byte (the rest were rendered).</param>
public sealed record CbzResult(int Pages, int OriginalPages);

/// <summary>
/// Builds a reading copy as CBZ with <c>ComicInfo.xml</c> (SPEC §9.2): a CBZ is copied with its
/// ComicInfo replaced; a CBR is repacked; an EPUB gives its images in spine order; a PDF gives each
/// page's original JPEG when possible, otherwise a rendering. The original is only read.
/// </summary>
public static partial class CbzBuilder
{
    public static Task<CbzResult> BuildAsync(string source, BookFormat format, BookMetadata book, string target,
        IProgress<double>? progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            using var zip = new ZipArchive(output, ZipArchiveMode.Create);
            var result = format switch
            {
                BookFormat.Cbz or BookFormat.Cbr => FromArchive(source, zip, progress, ct),
                BookFormat.Epub => FromEpub(source, zip, progress, ct),
                BookFormat.Pdf => FromPdf(source, zip, progress, ct),
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
            };

            if (result.Pages == 0)
            {
                throw new InvalidDataException("No pages were found in the original.");
            }

            var entry = zip.CreateEntry("ComicInfo.xml", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(entry.Open(), new System.Text.UTF8Encoding(false)))
            {
                writer.Write(ComicInfoWriter.Write(book, result.Pages));
            }

            return result;
        }, ct);

    /// <summary>Pages are named 0001.jpg, 0002.png… so every reader keeps the order.</summary>
    private static void AddPage(ZipArchive zip, int number, string extension, Stream data)
    {
        // Images are already compressed: storing them is faster and loses nothing.
        var entry = zip.CreateEntry($"{number:D4}{extension.ToLowerInvariant()}", CompressionLevel.NoCompression);
        using var stream = entry.Open();
        data.CopyTo(stream);
    }

    private static CbzResult FromArchive(string source, ZipArchive zip, IProgress<double>? progress, CancellationToken ct)
    {
        using var archive = ArchiveFactory.Open(source);
        var pages = archive.Entries
            .Where(e => !e.IsDirectory && ImageFiles.IsImage(e.Key) && !IsHidden(e.Key))
            .OrderBy(e => e.Key, NaturalStringComparer.Instance)
            .ToList();
        for (var i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var data = pages[i].OpenEntryStream();
            AddPage(zip, i + 1, Path.GetExtension(pages[i].Key!), data);
            progress?.Report((i + 1.0) / pages.Count);
        }

        return new CbzResult(pages.Count, 0);
    }

    private static CbzResult FromEpub(string source, ZipArchive zip, IProgress<double>? progress, CancellationToken ct)
    {
        using var epub = ZipFile.OpenRead(source);
        var images = EpubPageImages(epub);
        for (var i = 0; i < images.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var data = images[i].Open();
            AddPage(zip, i + 1, Path.GetExtension(images[i].FullName), data);
            progress?.Report((i + 1.0) / images.Count);
        }

        return new CbzResult(images.Count, 0);
    }

    /// <summary>The image of each page in spine order (one per page in comic EPUBs); manifest order as a fallback.</summary>
    internal static List<ZipArchiveEntry> EpubPageImages(ZipArchive epub)
    {
        var container = epub.GetEntry("META-INF/container.xml");
        var opfPath = container is null ? null : XDocument.Load(container.Open())
            .Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value;
        opfPath ??= epub.Entries.FirstOrDefault(e => e.FullName.EndsWith(".opf", StringComparison.OrdinalIgnoreCase))?.FullName;
        if (opfPath is null || Find(epub, opfPath) is not { } opfEntry)
        {
            return [];
        }

        var package = new OpfPackage(XDocument.Load(opfEntry.Open()), Path.GetDirectoryName(opfPath)?.Replace('\\', '/') ?? string.Empty);
        var result = new List<ZipArchiveEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in package.SpineItemIds)
        {
            if (package.Manifest.FirstOrDefault(m => m.Id == id) is not { } item)
            {
                continue;
            }

            var pagePath = package.Resolve(item.Href);
            if (item.MediaType.StartsWith("image/", StringComparison.Ordinal))
            {
                if (Find(epub, pagePath) is { } direct && seen.Add(direct.FullName))
                {
                    result.Add(direct);
                }

                continue;
            }

            if (Find(epub, pagePath) is not { } page)
            {
                continue;
            }

            string html;
            using (var reader = new StreamReader(page.Open()))
            {
                html = reader.ReadToEnd();
            }

            var dir = Path.GetDirectoryName(pagePath)?.Replace('\\', '/') ?? string.Empty;
            foreach (Match m in ImageReference().Matches(html))
            {
                if (Find(epub, OpfPackage.Combine(dir, m.Groups[1].Value)) is { } image && seen.Add(image.FullName))
                {
                    result.Add(image);
                }
            }
        }

        if (result.Count == 0)
        {
            result.AddRange(package.Manifest
                .Where(m => m.MediaType.StartsWith("image/", StringComparison.Ordinal))
                .Select(m => Find(epub, package.Resolve(m.Href)))
                .OfType<ZipArchiveEntry>());
        }

        return result;
    }

    /// <summary>
    /// PDFium renders one page at a time (it is not thread-safe), but encoding rendered pages as
    /// JPEG is not tied to it: a few pages are encoded in parallel while the next ones render.
    /// </summary>
    private static CbzResult FromPdf(string source, ZipArchive zip, IProgress<double>? progress, CancellationToken ct)
    {
        using var pdf = PdfDocument.Open(source);
        var count = pdf.PageCount;
        var original = 0;
        var written = 0;
        // A rendered page is ~25 MB of pixels until encoded: keep only a few in flight.
        var inFlight = Math.Clamp(Environment.ProcessorCount - 1, 1, 4);
        var pending = new Queue<Task<PdfPageImage>>();

        void WriteOldest()
        {
            var page = pending.Dequeue().GetAwaiter().GetResult();
            if (page.IsOriginal)
            {
                original++;
            }

            using var data = new MemoryStream(page.Jpeg);
            AddPage(zip, ++written, ".jpg", data);
            progress?.Report((double)written / count);
        }

        try
        {
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var content = pdf.GetPageContent(i);
                pending.Enqueue(content.Original is { } jpeg
                    ? Task.FromResult(jpeg)
                    : Task.Run(() => PdfDocument.Encode(content.Rendered!), CancellationToken.None));
                while (pending.Count > inFlight || (pending.Count > 0 && pending.Peek().IsCompleted))
                {
                    WriteOldest();
                }
            }

            while (pending.Count > 0)
            {
                WriteOldest();
            }
        }
        finally
        {
            // On cancellation or an error, wait for the encodes so their bitmaps are released.
            foreach (var task in pending)
            {
                ((IAsyncResult)task).AsyncWaitHandle.WaitOne();
            }
        }

        return new CbzResult(count, original);
    }

    private static ZipArchiveEntry? Find(ZipArchive zip, string path) =>
        zip.GetEntry(path) ?? zip.Entries.FirstOrDefault(e => e.FullName.Equals(path, StringComparison.OrdinalIgnoreCase));

    private static bool IsHidden(string? key) =>
        key is null || key.Replace('\\', '/').Split('/').Any(p => p.StartsWith('.') || p.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex("""(?:src|xlink:href|href)\s*=\s*["']([^"']+\.(?:jpe?g|png|gif|webp))["']""", RegexOptions.IgnoreCase)]
    private static partial Regex ImageReference();
}
