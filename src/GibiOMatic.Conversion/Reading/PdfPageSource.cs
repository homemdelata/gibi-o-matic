using GibiOMatic.Conversion.Pdf;
using SkiaSharp;

namespace GibiOMatic.Conversion.Reading;

/// <summary>
/// PDF pages rendered by PDFium at the size asked for. Only rendering is used: PDFium is built
/// without JavaScript and XFA, and no form environment is created, so nothing in the PDF runs.
/// </summary>
internal sealed class PdfPageSource(string path) : IPageSource
{
    /// <summary>Bigger renderings are not sharper on any screen and cost a lot of memory.</summary>
    private const int MaxSide = 6000;

    private readonly PdfDocument _pdf = PdfDocument.Open(path);
    private readonly Dictionary<int, PageSize> _sizes = [];

    public int PageCount => _pdf.PageCount;

    public PageSize GetSize(int index)
    {
        lock (_sizes)
        {
            if (_sizes.TryGetValue(index, out var known))
            {
                return known;
            }
        }

        var (width, height) = _pdf.GetPageSize(index);
        // 100% in a PDF viewer: one point is 1/72 inch, shown at 96 pixels per inch.
        var size = width > 0 && height > 0
            ? new PageSize(Math.Max(1, (int)Math.Round(width * 96 / 72)), Math.Max(1, (int)Math.Round(height * 96 / 72)))
            : PageSize.Portrait;
        lock (_sizes)
        {
            _sizes[index] = size;
        }

        return size;
    }

    public SKBitmap Render(int index, int maxWidth, int maxHeight, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var size = GetSize(index);
        var scale = Math.Min((double)Math.Max(1, maxWidth) / size.Width, (double)Math.Max(1, maxHeight) / size.Height);
        var height = (int)Math.Round(size.Height * scale);
        var width = size.Width * scale;
        if (Math.Max(width, height) > MaxSide)
        {
            height = (int)Math.Round(height * MaxSide / Math.Max(width, height));
        }

        return _pdf.RenderPage(index, Math.Max(1, height));
    }

    public void Dispose() => _pdf.Dispose();
}
