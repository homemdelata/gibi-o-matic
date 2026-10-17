using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GibiOMatic.Conversion.Pdf;

/// <summary>
/// The few PDFium functions the app needs (binaries from bblanchon/pdfium-binaries, Apache-2.0).
/// PDFium is not thread-safe: every call must hold <see cref="Sync"/>.
/// </summary>
internal static unsafe partial class PdfiumNative
{
    private const string Library = "pdfium";

    public const int BitmapFormatBgra = 4;
    public const int RenderAnnotations = 0x01;

    public static readonly Lock Sync = new();

    private static bool _initialized;

    public static void EnsureInitialized()
    {
        lock (Sync)
        {
            if (!_initialized)
            {
                FPDF_InitLibrary();
                _initialized = true;
            }
        }
    }

    /// <summary>
    /// FPDF_FILEACCESS. <c>m_FileLen</c> is a C <c>unsigned long</c>: 32 bits on Windows, 64 on Linux.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FileAccess
    {
        public CULong FileLength;
        public delegate* unmanaged[Cdecl]<nint, CULong, byte*, CULong, int> GetBlock;
        public nint Param;
    }

    [LibraryImport(Library)]
    private static partial void FPDF_InitLibrary();

    [LibraryImport(Library)]
    public static partial nint FPDF_LoadCustomDocument(FileAccess* access, byte* password);

    [LibraryImport(Library)]
    public static partial void FPDF_CloseDocument(nint document);

    [LibraryImport(Library)]
    public static partial uint FPDF_GetLastError();

    [LibraryImport(Library)]
    public static partial int FPDF_GetPageCount(nint document);

    [LibraryImport(Library)]
    public static partial nint FPDF_LoadPage(nint document, int pageIndex);

    [LibraryImport(Library)]
    public static partial void FPDF_ClosePage(nint page);

    [LibraryImport(Library)]
    public static partial float FPDF_GetPageWidthF(nint page);

    [LibraryImport(Library)]
    public static partial float FPDF_GetPageHeightF(nint page);

    [LibraryImport(Library)]
    public static partial nint FPDFBitmap_CreateEx(int width, int height, int format, nint firstScan, int stride);

    [LibraryImport(Library)]
    public static partial int FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);

    [LibraryImport(Library)]
    public static partial void FPDFBitmap_Destroy(nint bitmap);

    [LibraryImport(Library)]
    public static partial void FPDF_RenderPageBitmap(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    /// <summary>Returns the byte length of the UTF-16LE value, including the terminator.</summary>
    [LibraryImport(Library)]
    public static partial CULong FPDF_GetMetaText(nint document, byte* tag, void* buffer, CULong length);

    /// <summary>Read callback for <see cref="FPDF_LoadCustomDocument"/>; <paramref name="param"/> is a GCHandle to a Stream.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int ReadBlock(nint param, CULong position, byte* buffer, CULong size)
    {
        try
        {
            var stream = (Stream)GCHandle.FromIntPtr(param).Target!;
            stream.Position = checked((long)position.Value);
            stream.ReadExactly(new Span<byte>(buffer, checked((int)size.Value)));
            return 1;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
