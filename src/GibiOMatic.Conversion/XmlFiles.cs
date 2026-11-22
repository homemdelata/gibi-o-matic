using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace GibiOMatic.Conversion;

/// <summary>
/// XML read from the books (EPUB packages and pages, ComicInfo.xml): no DTD processing (no external
/// entities), and an "XML 1.1" declaration read as 1.0. .NET only reads XML 1.0, and the books that
/// declare 1.1 (an EPUB package seen in a Humble Bundle book) use nothing of 1.1.
/// </summary>
internal static partial class XmlFiles
{
    /// <summary>How far into a file its declaration is looked for.</summary>
    private const int DeclarationBytes = 128;

    public static XDocument Load(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var length = (int)buffer.Length;
        var data = buffer.GetBuffer();
        var start = length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;

        // Latin-1 maps each byte to one char, so an index in the text is an index in the bytes.
        var head = Encoding.Latin1.GetString(data, start, Math.Min(DeclarationBytes, length - start));
        if (Version11().Match(head) is { Success: true } match)
        {
            data[start + match.Groups["minor"].Index] = (byte)'0';
        }

        using var reader = XmlReader.Create(new MemoryStream(data, 0, length, writable: false), Settings());
        return XDocument.Load(reader);
    }

    public static XDocument Parse(string text)
    {
        var match = Version11().Match(text.Length > DeclarationBytes ? text[..DeclarationBytes] : text);
        if (match.Success)
        {
            var minor = match.Groups["minor"].Index;
            text = string.Concat(text.AsSpan(0, minor), "0", text.AsSpan(minor + 1));
        }

        using var reader = XmlReader.Create(new StringReader(text), Settings());
        return XDocument.Load(reader);
    }

    private static XmlReaderSettings Settings() => new() { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };

    [GeneratedRegex("""^﻿?<\?xml\s+version\s*=\s*["']1\.(?<minor>1)["']""")]
    private static partial Regex Version11();
}
