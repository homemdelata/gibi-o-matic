using System.Buffers.Binary;
using System.Text;

namespace GibiOMatic.Testing;

/// <summary>
/// Writes RAR 4.x archives with the "store" method (no compression), which is enough for
/// readers to treat them as real CBR files. Format: marker block, archive header, one file
/// header + data per entry, end block. Header CRCs are the low 16 bits of CRC-32.
/// </summary>
public static class RarWriter
{
    public static byte[] Store(IEnumerable<(string Name, byte[] Bytes)> entries)
    {
        using var output = new MemoryStream();
        output.Write([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00]);

        // MAIN_HEAD: type 0x73, flags 0, size 13, 6 reserved bytes.
        WriteBlock(output, 0x73, flags: 0, body: new byte[6]);

        foreach (var (name, bytes) in entries)
        {
            var nameBytes = Encoding.ASCII.GetBytes(name.Replace('/', '\\'));
            var body = new byte[25 + nameBytes.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0), (uint)bytes.Length); // PACK_SIZE
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), (uint)bytes.Length); // UNP_SIZE
            body[8] = 2; // HOST_OS: Windows
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9), Crc32(bytes)); // FILE_CRC
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(13), 0x5A210000); // FTIME (DOS, 2025)
            body[17] = 29; // UNP_VER 2.9
            body[18] = 0x30; // METHOD: store
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(19), (ushort)nameBytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(21), 0x20); // ATTR: archive
            nameBytes.CopyTo(body, 25);

            // FILE_HEAD: type 0x74, flag 0x8000 = data follows the header.
            WriteBlock(output, 0x74, flags: 0x8000, body);
            output.Write(bytes);
        }

        // ENDARC_HEAD
        WriteBlock(output, 0x7B, flags: 0x4000, body: []);
        return output.ToArray();
    }

    private static void WriteBlock(Stream output, byte type, ushort flags, byte[] body)
    {
        var header = new byte[7 + body.Length];
        header[2] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(3), flags);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(5), (ushort)header.Length);
        body.CopyTo(header, 7);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0), (ushort)(Crc32(header.AsSpan(2)) & 0xFFFF));
        output.Write(header);
    }

    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }

        return c;
    }).ToArray();

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }
}
