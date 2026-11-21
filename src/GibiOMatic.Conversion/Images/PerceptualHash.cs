using GibiOMatic.Conversion.Reading;
using SkiaSharp;

namespace GibiOMatic.Conversion.Images;

/// <summary>
/// A difference hash (dHash) of an image: 64 bits saying, row by row of a 9×8 grey thumbnail,
/// whether each pixel is brighter than the next. The same cover at another size, crop of a few
/// pixels or JPEG quality gives nearly the same bits; another cover does not. Used to check that
/// the record Claude found has the book's cover (SPEC §8.4).
/// </summary>
public static class PerceptualHash
{
    /// <summary>
    /// Covers at most this many bits apart are the same image. In the test of 2026-11-21 the 14
    /// matching covers were 0 to 8 bits apart; different covers are usually 20 or more.
    /// </summary>
    public const int SameImageDistance = 10;

    /// <summary>The hash of an encoded image; null when it cannot be decoded.</summary>
    public static ulong? Compute(byte[] encodedImage)
    {
        using var bitmap = PageImages.DecodeToFit(encodedImage, 256, 256);
        if (bitmap is null)
        {
            return null;
        }

        using var small = bitmap.Resize(new SKImageInfo(9, 8, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if (small is null)
        {
            return null;
        }

        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                if (Grey(small.GetPixel(x, y)) > Grey(small.GetPixel(x + 1, y)))
                {
                    hash |= 1UL << bit;
                }

                bit++;
            }
        }

        return hash;
    }

    public static ulong? Compute(string path) => File.Exists(path) ? Compute(File.ReadAllBytes(path)) : null;

    /// <summary>How many of the 64 bits differ.</summary>
    public static int Distance(ulong a, ulong b) => System.Numerics.BitOperations.PopCount(a ^ b);

    /// <summary>True, false, or null when either image could not be read.</summary>
    public static bool? Same(ulong? a, ulong? b) => a is { } x && b is { } y ? Distance(x, y) <= SameImageDistance : null;

    private static double Grey(SKColor c) => 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
}
