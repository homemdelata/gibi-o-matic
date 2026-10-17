using System.Globalization;
using System.Text;

namespace GibiOMatic.Testing;

/// <summary>Writes a minimal PDF 1.4 with one full-page JPEG per page, an Info dictionary and optional XMP.</summary>
internal static class PdfWriter
{
    private const int PageWidthPt = 400;
    private const int PageHeightPt = 600;

    public static byte[] Write(SyntheticBooks.PdfOptions options)
    {
        var objects = new List<byte[]>();
        int Reserve()
        {
            objects.Add([]);
            return objects.Count;
        }

        void Set(int number, string text) => objects[number - 1] = Encoding.Latin1.GetBytes(text);

        void SetStream(int number, string dictionary, byte[] data)
        {
            using var ms = new MemoryStream();
            ms.Write(Encoding.Latin1.GetBytes($"<< {dictionary} /Length {data.Length} >>\nstream\n"));
            ms.Write(data);
            ms.Write("\nendstream"u8);
            objects[number - 1] = ms.ToArray();
        }

        var catalog = Reserve();
        var pagesNode = Reserve();
        var info = Reserve();
        var metadata = options.Xmp is null ? 0 : Reserve();
        var kids = new List<int>();

        for (var i = 0; i < options.Pages; i++)
        {
            var page = Reserve();
            var image = Reserve();
            var content = Reserve();
            kids.Add(page);

            var jpeg = SyntheticImages.Jpeg(200, 300, i == 0 ? SyntheticBooks.CoverColor : SyntheticBooks.PageColor);
            SetStream(image, "/Type /XObject /Subtype /Image /Width 200 /Height 300 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", jpeg);
            SetStream(content, string.Empty, Encoding.ASCII.GetBytes($"q {PageWidthPt} 0 0 {PageHeightPt} 0 0 cm /Im0 Do Q"));
            Set(page, $"<< /Type /Page /Parent {pagesNode} 0 R /MediaBox [0 0 {PageWidthPt} {PageHeightPt}] /Resources << /XObject << /Im0 {image} 0 R >> >> /Contents {content} 0 R >>");
        }

        Set(pagesNode, $"<< /Type /Pages /Kids [{string.Join(' ', kids.Select(k => $"{k} 0 R"))}] /Count {kids.Count} >>");
        Set(catalog, $"<< /Type /Catalog /Pages {pagesNode} 0 R{(metadata == 0 ? "" : $" /Metadata {metadata} 0 R")} >>");
        if (metadata != 0)
        {
            SetStream(metadata, "/Type /Metadata /Subtype /XML", Encoding.UTF8.GetBytes(options.Xmp!));
        }

        var infoEntries = new StringBuilder();
        void Info(string key, string? value)
        {
            if (value is not null)
            {
                infoEntries.Append($" /{key} {TextString(value)}");
            }
        }

        Info("Title", options.Title);
        Info("Author", options.Author);
        Info("Producer", options.Producer);
        Info("Keywords", options.Keywords);
        Info("CreationDate", "D:20200101000000Z");
        Set(info, $"<<{infoEntries} >>");

        using var output = new MemoryStream();
        output.Write("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n"u8);
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            output.Write(Encoding.ASCII.GetBytes($"{i + 1} 0 obj\n"));
            output.Write(objects[i]);
            output.Write("\nendobj\n"u8);
        }

        var xref = output.Position;
        var table = new StringBuilder($"xref\n0 {objects.Count + 1}\n0000000000 65535 f\r\n");
        foreach (var offset in offsets)
        {
            table.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n\r\n");
        }

        table.Append($"trailer\n<< /Size {objects.Count + 1} /Root {catalog} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        output.Write(Encoding.ASCII.GetBytes(table.ToString()));
        return output.ToArray();
    }

    /// <summary>A PDF text string in UTF-16BE with BOM, as a hex string (handles accents safely).</summary>
    private static string TextString(string value) =>
        "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(value)) + ">";
}
