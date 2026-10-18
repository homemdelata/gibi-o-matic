using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;

namespace GibiOMatic.Conversion.Pdf;

public sealed class PdfException(string message) : Exception(message);

/// <summary>
/// A PDF opened with PDFium. The file is read through a callback, so any path works
/// (accents, long paths) and large files are not loaded into memory.
/// </summary>
internal sealed unsafe class PdfDocument : IDisposable
{
    private readonly FileStream _stream;
    private readonly GCHandle _streamHandle;
    private readonly PdfiumNative.FileAccess* _access;
    private nint _document;

    private PdfDocument(string path)
    {
        _stream = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read, bufferSize: 1 << 16);
        _streamHandle = GCHandle.Alloc(_stream);
        _access = (PdfiumNative.FileAccess*)NativeMemory.AllocZeroed((nuint)sizeof(PdfiumNative.FileAccess));
        _access->FileLength = new CULong(checked((nuint)_stream.Length));
        _access->GetBlock = &PdfiumNative.ReadBlock;
        _access->Param = GCHandle.ToIntPtr(_streamHandle);
    }

    public static PdfDocument Open(string path)
    {
        PdfiumNative.EnsureInitialized();
        var doc = new PdfDocument(path);
        lock (PdfiumNative.Sync)
        {
            doc._document = PdfiumNative.FPDF_LoadCustomDocument(doc._access, null);
            if (doc._document == 0)
            {
                var error = PdfiumNative.FPDF_GetLastError();
                doc.Dispose();
                throw new PdfException(error switch
                {
                    3 => "The file is not a valid PDF.",
                    4 => "The PDF is password protected.",
                    5 => "The PDF uses an unsupported security scheme.",
                    _ => $"PDFium could not open the file (error {error}).",
                });
            }
        }

        return doc;
    }

    public int PageCount
    {
        get
        {
            lock (PdfiumNative.Sync)
            {
                return PdfiumNative.FPDF_GetPageCount(_document);
            }
        }
    }

    /// <summary>A value of the Info dictionary ("Title", "Author", "Producer"…), or <c>null</c>.</summary>
    public string? GetInfo(string tag)
    {
        var tagBytes = Encoding.ASCII.GetBytes(tag + "\0");
        lock (PdfiumNative.Sync)
        {
            fixed (byte* t = tagBytes)
            {
                var length = (int)PdfiumNative.FPDF_GetMetaText(_document, t, null, default).Value;
                if (length <= 2)
                {
                    return null;
                }

                var buffer = new byte[length];
                fixed (byte* b = buffer)
                {
                    PdfiumNative.FPDF_GetMetaText(_document, t, b, new CULong((nuint)length));
                }

                return Encoding.Unicode.GetString(buffer, 0, length - 2);
            }
        }
    }

    /// <summary>Renders a page on white, scaled so that its height is <paramref name="targetHeight"/> pixels.</summary>
    /// <summary>Width and height of a page, in points (1/72 inch).</summary>
    public (float Width, float Height) GetPageSize(int index)
    {
        lock (PdfiumNative.Sync)
        {
            var page = PdfiumNative.FPDF_LoadPage(_document, index);
            if (page == 0)
            {
                throw new PdfException($"Page {index + 1} could not be loaded.");
            }

            try
            {
                return (PdfiumNative.FPDF_GetPageWidthF(page), PdfiumNative.FPDF_GetPageHeightF(page));
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }
    }

    public SKBitmap RenderPage(int index, int targetHeight)
    {
        lock (PdfiumNative.Sync)
        {
            var page = PdfiumNative.FPDF_LoadPage(_document, index);
            if (page == 0)
            {
                throw new PdfException($"Page {index + 1} could not be loaded.");
            }

            try
            {
                var widthPt = PdfiumNative.FPDF_GetPageWidthF(page);
                var heightPt = PdfiumNative.FPDF_GetPageHeightF(page);
                if (widthPt <= 0 || heightPt <= 0)
                {
                    throw new PdfException($"Page {index + 1} has no size.");
                }

                var height = Math.Max(1, targetHeight);
                var width = Math.Max(1, (int)Math.Round(widthPt * height / heightPt));
                var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
                var pdfBitmap = PdfiumNative.FPDFBitmap_CreateEx(width, height, PdfiumNative.BitmapFormatBgra, bitmap.GetPixels(), bitmap.RowBytes);
                try
                {
                    PdfiumNative.FPDFBitmap_FillRect(pdfBitmap, 0, 0, width, height, 0xFFFFFFFF);
                    PdfiumNative.FPDF_RenderPageBitmap(pdfBitmap, page, 0, 0, width, height, 0, PdfiumNative.RenderAnnotations);
                }
                finally
                {
                    PdfiumNative.FPDFBitmap_Destroy(pdfBitmap);
                }

                bitmap.NotifyPixelsChanged();
                return bitmap;
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }
    }

    private const int JpegQuality = 92;
    private const int DefaultRenderHeight = 2400;
    private const int MinRenderHeight = 1200;
    private const int MaxRenderHeight = 3200;
    private const float CoverTolerance = 2f;

    /// <summary>
    /// The page as an image (SPEC §9.2, validated in reference/python/pdf2epub.py): when the page is
    /// a single upright gray/RGB JPEG covering it, with no text or vectors on top, its original bytes
    /// are returned untouched; otherwise the page is rendered at the resolution of its largest
    /// image and saved as JPEG quality 92.
    /// </summary>
    public PdfPageImage GetPageImage(int index)
    {
        var content = GetPageContent(index);
        return content.Original ?? Encode(content.Rendered!);
    }

    /// <summary>A rendered page as JPEG quality 92 (no PDFium call, so any thread). Disposes the bitmap.</summary>
    public static PdfPageImage Encode(SKBitmap bitmap)
    {
        using (bitmap)
        {
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
            return new PdfPageImage(data.ToArray(), bitmap.Width, bitmap.Height, IsOriginal: false);
        }
    }

    /// <summary>
    /// The page's original JPEG when it can go as it is, or the page rendered (to encode with
    /// <see cref="Encode"/>, possibly on another thread).
    /// </summary>
    public PdfPageContent GetPageContent(int index)
    {
        int targetHeight;
        lock (PdfiumNative.Sync)
        {
            var page = PdfiumNative.FPDF_LoadPage(_document, index);
            if (page == 0)
            {
                throw new PdfException($"Page {index + 1} could not be loaded.");
            }

            try
            {
                var heightPt = PdfiumNative.FPDF_GetPageHeightF(page);
                var widthPt = PdfiumNative.FPDF_GetPageWidthF(page);
                var images = new List<(nint Object, PdfiumNative.ImageMetadata Metadata, float Left, float Bottom, float Right, float Top)>();
                var onlyImages = true;
                var count = PdfiumNative.FPDFPage_CountObjects(page);
                for (var i = 0; i < count; i++)
                {
                    var obj = PdfiumNative.FPDFPage_GetObject(page, i);
                    if (PdfiumNative.FPDFPageObj_GetType(obj) != PdfiumNative.PageObjectImage)
                    {
                        onlyImages = false; // text, vector paths, shadings, forms
                        continue;
                    }

                    PdfiumNative.FPDFPageObj_GetBounds(obj, out var left, out var bottom, out var right, out var top);
                    PdfiumNative.FPDFImageObj_GetImageMetadata(obj, page, out var metadata);
                    images.Add((obj, metadata, left, bottom, right, top));
                }

                if (onlyImages && images.Count == 1 && OriginalJpeg(images[0].Object, images[0].Metadata) is { } jpeg)
                {
                    var (_, md, l, b, r, t) = images[0];
                    var covers = l <= CoverTolerance && b <= CoverTolerance && r >= widthPt - CoverTolerance && t >= heightPt - CoverTolerance;
                    if (covers && PdfiumNative.FPDFPageObj_GetMatrix(images[0].Object, out var m) != 0
                        && Math.Abs(m.B) < 1e-6 && Math.Abs(m.C) < 1e-6 && m.A > 0 && m.D > 0)
                    {
                        return new PdfPageContent(new PdfPageImage(jpeg, (int)md.Width, (int)md.Height, IsOriginal: true), null);
                    }
                }

                // Render at the resolution of the largest image, without upscaling a lot.
                targetHeight = DefaultRenderHeight;
                var scaled = images
                    .Where(img => img.Top - img.Bottom > 1 && img.Metadata.Height > 0)
                    .Select(img => img.Metadata.Height * heightPt / (img.Top - img.Bottom))
                    .ToList();
                if (scaled.Count > 0)
                {
                    targetHeight = (int)scaled.Max();
                }

                targetHeight = Math.Clamp(targetHeight, MinRenderHeight, MaxRenderHeight);
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }

        return new PdfPageContent(null, RenderPage(index, targetHeight));
    }

    /// <summary>
    /// The raw JPEG of an image object when it is a plain DCT-encoded gray or RGB image. Like the
    /// reference script, an ICC-based color space counts when the JPEG has 1 or 3 components
    /// (Humble's PDFs tag their pages this way); CMYK and the like are rendered.
    /// </summary>
    private static byte[]? OriginalJpeg(nint imageObject, PdfiumNative.ImageMetadata metadata)
    {
        if (metadata.ColorSpace is not (PdfiumNative.ColorSpaceRgb or PdfiumNative.ColorSpaceGray or PdfiumNative.ColorSpaceCalRgb
                or PdfiumNative.ColorSpaceCalGray or PdfiumNative.ColorSpaceIccBased)
            || PdfiumNative.FPDFImageObj_GetImageFilterCount(imageObject) != 1)
        {
            return null;
        }

        var filterLength = (int)PdfiumNative.FPDFImageObj_GetImageFilter(imageObject, 0, null, default).Value;
        var filter = new byte[Math.Max(filterLength, 1)];
        fixed (byte* f = filter)
        {
            PdfiumNative.FPDFImageObj_GetImageFilter(imageObject, 0, f, new CULong((nuint)filter.Length));
        }

        if (Encoding.ASCII.GetString(filter).TrimEnd('\0') != "DCTDecode")
        {
            return null;
        }

        var length = (int)PdfiumNative.FPDFImageObj_GetImageDataRaw(imageObject, null, default).Value;
        if (length < 4)
        {
            return null;
        }

        var bytes = new byte[length];
        fixed (byte* b = bytes)
        {
            PdfiumNative.FPDFImageObj_GetImageDataRaw(imageObject, b, new CULong((nuint)length));
        }

        return bytes[0] == 0xFF && bytes[1] == 0xD8 && JpegComponents(bytes) is 1 or 3 ? bytes : null;
    }

    /// <summary>Number of color components in the JPEG's frame header (SOFn), or 0 when not found.</summary>
    internal static int JpegComponents(ReadOnlySpan<byte> jpeg)
    {
        var i = 2;
        while (i + 9 < jpeg.Length)
        {
            if (jpeg[i] != 0xFF)
            {
                return 0;
            }

            var marker = jpeg[i + 1];
            if (marker == 0xFF)
            {
                i++;
                continue;
            }

            // SOF0 to SOF15, except DHT (C4), JPG (C8) and DAC (CC).
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                return jpeg[i + 9];
            }

            if (marker is 0xD9 or 0xDA)
            {
                return 0;
            }

            i += 2 + ((jpeg[i + 2] << 8) | jpeg[i + 3]);
        }

        return 0;
    }

    public void Dispose()
    {
        lock (PdfiumNative.Sync)
        {
            if (_document != 0)
            {
                PdfiumNative.FPDF_CloseDocument(_document);
                _document = 0;
            }
        }

        NativeMemory.Free(_access);
        if (_streamHandle.IsAllocated)
        {
            _streamHandle.Free();
        }

        _stream.Dispose();
    }
}

/// <param name="IsOriginal">The JPEG bytes stored in the PDF, untouched (no recompression).</param>
internal sealed record PdfPageImage(byte[] Jpeg, int Width, int Height, bool IsOriginal);

/// <summary>Either the page's original JPEG or the rendered page, still to encode.</summary>
internal sealed record PdfPageContent(PdfPageImage? Original, SKBitmap? Rendered);
