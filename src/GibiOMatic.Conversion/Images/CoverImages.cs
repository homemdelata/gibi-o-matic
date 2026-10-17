using SkiaSharp;

namespace GibiOMatic.Conversion.Images;

/// <summary>Scales cover images and stores them as JPEG.</summary>
public static class CoverImages
{
    /// <summary>Longest side of the <c>cover.jpg</c> kept next to the original.</summary>
    public const int CoverMaxHeight = 1200;

    /// <summary>Size of the grid thumbnails kept in the local cache (2:3 covers, about 2× the card size).</summary>
    public const int ThumbnailWidth = 240;
    public const int ThumbnailHeight = 360;

    private const int JpegQuality = 88;

    /// <summary>The <c>cover.jpg</c> for the library: at most <see cref="CoverMaxHeight"/> pixels tall.</summary>
    public static byte[]? CreateCover(byte[] encodedImage) =>
        Scale(encodedImage, maxWidth: CoverMaxHeight, maxHeight: CoverMaxHeight);

    /// <summary>A thumbnail that fits in <see cref="ThumbnailWidth"/>×<see cref="ThumbnailHeight"/>.</summary>
    public static byte[]? CreateThumbnail(byte[] encodedImage) =>
        Scale(encodedImage, ThumbnailWidth, ThumbnailHeight);

    /// <summary>Returns <c>null</c> when the image cannot be decoded.</summary>
    public static byte[]? Scale(byte[] encodedImage, int maxWidth, int maxHeight)
    {
        using var source = Decode(encodedImage, maxWidth, maxHeight);
        if (source is null)
        {
            return null;
        }

        var scale = Math.Min(1.0, Math.Min((double)maxWidth / source.Width, (double)maxHeight / source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var scaled = width == source.Width && height == source.Height
            ? source.Copy()
            : source.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell));
        if (scaled is null)
        {
            return null;
        }

        // Transparent PNG covers get a white background instead of turning black in JPEG.
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.DrawBitmap(scaled, 0, 0);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        return data?.ToArray();
    }

    /// <summary>Decodes using the codec's cheap downscaling when the source is much larger than needed.</summary>
    private static SKBitmap? Decode(byte[] encoded, int maxWidth, int maxHeight)
    {
        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data);
        if (codec is null)
        {
            return null;
        }

        var info = codec.Info;
        var scale = Math.Min(1f, Math.Min((float)maxWidth / info.Width, (float)maxHeight / info.Height));
        var size = codec.GetScaledDimensions(scale);
        var bitmap = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
        {
            return bitmap;
        }

        bitmap.Dispose();
        return SKBitmap.Decode(encoded);
    }
}
