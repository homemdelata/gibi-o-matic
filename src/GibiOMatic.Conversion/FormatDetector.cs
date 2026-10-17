using System.IO.Compression;
using System.Text;
using GibiOMatic.Core.Domain;

namespace GibiOMatic.Conversion;

/// <summary>
/// Detects a book file's format from its content (magic bytes), never from its extension:
/// a ".cbz" that is really a RAR archive is reported as <see cref="BookFormat.Cbr"/>.
/// </summary>
public static class FormatDetector
{
    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] RarMagic = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07];

    /// <summary>Returns the format, or <c>null</c> when the file is not a supported book.</summary>
    public static BookFormat? Detect(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Detect(stream);
    }

    public static BookFormat? Detect(Stream stream)
    {
        // PDF allows up to 1 KB of junk before the header.
        Span<byte> head = stackalloc byte[1024];
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        head = head[..read];

        if (head.StartsWith(RarMagic))
        {
            return BookFormat.Cbr;
        }

        if (head.StartsWith(ZipMagic))
        {
            stream.Position = 0;
            return IsEpub(stream) ? BookFormat.Epub : BookFormat.Cbz;
        }

        return head.IndexOf(PdfMagic) >= 0 ? BookFormat.Pdf : null;
    }

    private static bool IsEpub(Stream stream)
    {
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var mimetype = zip.GetEntry("mimetype");
            if (mimetype is not null)
            {
                using var reader = new StreamReader(mimetype.Open(), Encoding.ASCII);
                if (reader.ReadToEnd().Trim() == "application/epub+zip")
                {
                    return true;
                }
            }

            return zip.GetEntry("META-INF/container.xml") is not null;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
