using System.Security.Cryptography;

namespace GibiOMatic.Core.Ids;

/// <summary>
/// Minimal ULID generator (https://github.com/ulid/spec): 48-bit millisecond timestamp
/// followed by 80 random bits, encoded as 26 Crockford base32 characters.
/// </summary>
public static class Ulid
{
    public const int Length = 26;
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string NewUlid() => NewUlid(DateTimeOffset.UtcNow);

    public static string NewUlid(DateTimeOffset timestamp)
    {
        Span<byte> bytes = stackalloc byte[16];
        var ms = (ulong)timestamp.ToUnixTimeMilliseconds();
        for (var i = 5; i >= 0; i--)
        {
            bytes[i] = (byte)(ms & 0xFF);
            ms >>= 8;
        }

        RandomNumberGenerator.Fill(bytes[6..]);
        return Encode(bytes);
    }

    /// <summary>
    /// Short, stable book id used in folder names (e.g. "k3f9a2"): the last 6 characters
    /// of the ULID, which come from its random part, in lowercase.
    /// </summary>
    public static string ShortId(string ulid)
    {
        if (!IsValid(ulid))
        {
            throw new ArgumentException($"Not a valid ULID: '{ulid}'", nameof(ulid));
        }

        return ulid[^6..].ToLowerInvariant();
    }

    public static bool IsValid(string? value) =>
        value is { Length: Length } && value[0] <= '7' && value.All(c => Alphabet.Contains(char.ToUpperInvariant(c)));

    private static string Encode(ReadOnlySpan<byte> bytes)
    {
        // 128 bits -> 26 chars of 5 bits; the first char carries only 3 bits.
        var hi = (UInt128)0;
        foreach (var b in bytes)
        {
            hi = (hi << 8) | b;
        }

        Span<char> chars = stackalloc char[Length];
        for (var i = Length - 1; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(hi & 0x1F)];
            hi >>= 5;
        }

        return new string(chars);
    }
}
