using SkiaSharp;

namespace GibiOMatic.Testing;

/// <summary>Solid-colour images for synthetic books.</summary>
public static class SyntheticImages
{
    public static byte[] Jpeg(int width, int height, SKColor color) => Encode(width, height, color, SKEncodedImageFormat.Jpeg);

    public static byte[] Png(int width, int height, SKColor color) => Encode(width, height, color, SKEncodedImageFormat.Png);

    /// <summary>Decodes an image and returns its size and the colour at its centre.</summary>
    public static (int Width, int Height, SKColor Center) Inspect(byte[] encoded)
    {
        using var bitmap = SKBitmap.Decode(encoded) ?? throw new InvalidDataException("Not an image.");
        return (bitmap.Width, bitmap.Height, bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2));
    }

    /// <summary>True when two colours differ by at most <paramref name="tolerance"/> per channel (JPEG is lossy).</summary>
    public static bool Near(SKColor a, SKColor b, int tolerance = 12) =>
        Math.Abs(a.Red - b.Red) <= tolerance && Math.Abs(a.Green - b.Green) <= tolerance && Math.Abs(a.Blue - b.Blue) <= tolerance;

    private static byte[] Encode(int width, int height, SKColor color, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }
}
