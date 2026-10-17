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
