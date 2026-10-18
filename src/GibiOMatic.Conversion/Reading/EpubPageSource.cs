using System.IO.Compression;
using GibiOMatic.Conversion.Cbz;

namespace GibiOMatic.Conversion.Reading;

/// <summary>The page images of a comic EPUB, in spine order (the same as its reading copy).</summary>
internal sealed class EpubPageSource : EncodedPageSource
{
    private readonly ZipArchive _zip;
    private readonly List<ZipArchiveEntry> _pages;

    private EpubPageSource(ZipArchive zip, List<ZipArchiveEntry> pages)
    {
        _zip = zip;
        _pages = pages;
    }

    /// <summary>The comic's pages, or <c>null</c> when the EPUB is a text book (few images for its chapters).</summary>
    public static EpubPageSource? TryOpen(string path)
    {
        var zip = ZipFile.OpenRead(path);
        try
        {
            var pages = CbzBuilder.EpubPageImages(zip);
            if (pages.Count == 0 || !EpubBook.LooksLikeComic(zip, pages.Count))
            {
                zip.Dispose();
                return null;
            }

            return new EpubPageSource(zip, pages);
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    public override int PageCount => _pages.Count;

    protected override byte[] Read(int index, int limit, CancellationToken ct)
    {
        lock (_zip)
        {
            using var stream = _pages[index].Open();
            return ReadAll(stream, limit);
        }
    }

    public override void Dispose()
    {
        lock (_zip)
        {
            _zip.Dispose();
        }
    }
}
