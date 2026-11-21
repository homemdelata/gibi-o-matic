using GibiOMatic.Conversion.Reading;
using GibiOMatic.Core.Domain;
using SkiaSharp;

namespace GibiOMatic.Conversion.Images;

/// <summary>
/// A few pages of a book as JPEG files, read with the app's own readers (PDFium, archives, EPUB):
/// what Claude looks at to tell which book it is (SPEC §8.4). The original is only read.
/// </summary>
public static class PageSnapshots
{
    /// <summary>Longest side of a page given to Claude: enough to read a credits page.</summary>
    public const int PageSize = 1100;

    private const int JpegQuality = 80;

    /// <summary>
    /// The pages where the credits usually are: the 3 after the cover and the last 2 (0-based
    /// indices, without the cover itself).
    /// </summary>
    public static IReadOnlyList<int> CreditPages(int pageCount) =>
        new[] { 1, 2, 3, pageCount - 2, pageCount - 1 }.Where(i => i > 0 && i < pageCount).Distinct().Order().ToList();

    /// <summary>
    /// Writes the chosen pages as <c>{prefix}NNN.jpg</c> (1-based page numbers) in <paramref name="folder"/>
    /// and returns their file names and the book's page count. A text EPUB has no pages: none are written.
    /// </summary>
    public static (IReadOnlyList<string> Files, int PageCount) Write(string bookPath, BookFormat format, string folder, string prefix,
        Func<int, IReadOnlyList<int>> choose, CancellationToken ct = default)
    {
        using var book = BookReader.Open(bookPath, format);
        if (book.Pages is not { } pages)
        {
            return ([], 0);
        }

        var files = new List<string>();
        foreach (var index in choose(pages.PageCount))
        {
            ct.ThrowIfCancellationRequested();
            using var bitmap = pages.Render(index, PageSize, PageSize, ct);
            var name = $"{prefix}{index + 1:D3}.jpg";
            File.WriteAllBytes(Path.Combine(folder, name), Encode(bitmap));
            files.Add(name);
        }

        return (files, pages.PageCount);
    }

    /// <summary>An image file scaled to fit <paramref name="size"/>, as JPEG; null when it cannot be decoded.</summary>
    public static byte[]? Scale(byte[] encodedImage, int size) => CoverImages.Scale(encodedImage, size, size);

    private static byte[] Encode(SKBitmap bitmap)
    {
        // Pages with transparency get a white background instead of turning black in JPEG.
        using var surface = SKSurface.Create(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.DrawBitmap(bitmap, 0, 0);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        return data.ToArray();
    }
}
