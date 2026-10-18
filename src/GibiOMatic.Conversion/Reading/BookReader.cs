using GibiOMatic.Core.Domain;

namespace GibiOMatic.Conversion.Reading;

/// <summary>A book opened in the reader: pages as images, or a text EPUB.</summary>
public sealed class OpenedBook : IDisposable
{
    private OpenedBook(IPageSource? pages, EpubBook? text)
    {
        Pages = pages;
        Text = text;
    }

    /// <summary>Comics and PDFs: one image per page.</summary>
    public IPageSource? Pages { get; }

    /// <summary>Text EPUBs: chapters shown in a web view.</summary>
    public EpubBook? Text { get; }

    internal static OpenedBook Paged(IPageSource pages) => new(pages, null);

    internal static OpenedBook Book(EpubBook text) => new(null, text);

    public void Dispose()
    {
        Pages?.Dispose();
        Text?.Dispose();
    }
}

/// <summary>Opens a book file for the reader (read only).</summary>
public static class BookReader
{
    /// <exception cref="InvalidDataException">The file has no pages that can be shown.</exception>
    public static OpenedBook Open(string path, BookFormat format)
    {
        switch (format)
        {
            case BookFormat.Cbz or BookFormat.Cbr:
                var archive = new ArchivePageSource(path);
                if (archive.PageCount == 0)
                {
                    archive.Dispose();
                    throw new InvalidDataException("The archive has no images.");
                }

                return OpenedBook.Paged(archive);
            case BookFormat.Pdf:
                var pdf = new PdfPageSource(path);
                if (pdf.PageCount == 0)
                {
                    pdf.Dispose();
                    throw new InvalidDataException("The PDF has no pages.");
                }

                return OpenedBook.Paged(pdf);
            case BookFormat.Epub:
                return EpubPageSource.TryOpen(path) is { } comic ? OpenedBook.Paged(comic) : OpenedBook.Book(EpubBook.Open(path));
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, null);
        }
    }
}
