using SkiaSharp;

namespace GibiOMatic.Conversion.Reading;

/// <summary>Size of a page at 100% (pixels of the image; for PDF, points at 96 dpi).</summary>
public readonly record struct PageSize(int Width, int Height)
{
    /// <summary>A double-page spread or a landscape page: shown alone in two-page mode.</summary>
    public bool IsWide => Width > Height;

    public static PageSize Portrait { get; } = new(2, 3);
}

/// <summary>
/// The pages of a book as images, for the reader. Implementations are safe to call from any
/// thread (one page is read at a time); the original file is only read.
/// </summary>
public interface IPageSource : IDisposable
{
    int PageCount { get; }

    /// <summary>The page's size at 100%, read from the image header (cheap).</summary>
    PageSize GetSize(int index);

    /// <summary>
    /// The page scaled to fit <paramref name="maxWidth"/>×<paramref name="maxHeight"/> (never
    /// larger than the image itself; PDF pages are rendered at that size), as premultiplied BGRA.
    /// </summary>
    SKBitmap Render(int index, int maxWidth, int maxHeight, CancellationToken ct = default);
}

/// <summary>Image pages that live as encoded files (CBZ, CBR, comic EPUB).</summary>
internal abstract class EncodedPageSource : IPageSource
{
    private readonly Dictionary<int, PageSize> _sizes = [];

    public abstract int PageCount { get; }

    /// <summary>The page's encoded bytes, or only its first <paramref name="limit"/> bytes.</summary>
    protected abstract byte[] Read(int index, int limit, CancellationToken ct);

    public PageSize GetSize(int index)
    {
        lock (_sizes)
        {
            if (_sizes.TryGetValue(index, out var known))
            {
                return known;
            }
        }

        // The header is at the start of the file; 64 KB covers JPEG with big EXIF blocks.
        var size = PageImages.ReadSize(Read(index, 64 * 1024, CancellationToken.None))
                   ?? PageImages.ReadSize(Read(index, int.MaxValue, CancellationToken.None))
                   ?? PageSize.Portrait;
        lock (_sizes)
        {
            _sizes[index] = size;
        }

        return size;
    }

    public SKBitmap Render(int index, int maxWidth, int maxHeight, CancellationToken ct = default)
    {
        var data = Read(index, int.MaxValue, ct);
        ct.ThrowIfCancellationRequested();
        var bitmap = PageImages.DecodeToFit(data, maxWidth, maxHeight)
                     ?? throw new InvalidDataException($"Page {index + 1} is not an image that can be read.");
        lock (_sizes)
        {
            _sizes.TryAdd(index, PageImages.ReadSize(data) ?? new PageSize(bitmap.Width, bitmap.Height));
        }

        return bitmap;
    }

    public abstract void Dispose();

    protected static byte[] ReadAll(Stream stream, int limit)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while (buffer.Length < limit && (read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, limit - buffer.Length))) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}

/// <summary>Decoding and scaling of page images.</summary>
internal static class PageImages
{
    public static PageSize? ReadSize(byte[] data)
    {
        using var skData = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(skData);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
        {
            return null;
        }

        return new PageSize(codec.Info.Width, codec.Info.Height);
    }

    /// <summary>
    /// Decodes with the codec's cheap downscaling (JPEG: 1/2, 1/4, 1/8), then resizes to fit;
    /// <c>null</c> when the data is not an image.
    /// </summary>
    public static SKBitmap? DecodeToFit(byte[] data, int maxWidth, int maxHeight)
    {
        using var skData = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(skData);
        if (codec is null)
        {
            return null;
        }

        var info = codec.Info;
        var scale = Math.Min(1f, Math.Min((float)Math.Max(1, maxWidth) / info.Width, (float)Math.Max(1, maxHeight) / info.Height));
        var decodedSize = codec.GetScaledDimensions(scale);
        var decoded = new SKBitmap(new SKImageInfo(decodedSize.Width, decodedSize.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var result = codec.GetPixels(decoded.Info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            decoded.Dispose();
            decoded = SKBitmap.Decode(data)?.Copy(SKColorType.Bgra8888);
            if (decoded is null)
            {
                return null;
            }
        }

        var width = Math.Max(1, (int)Math.Round(info.Width * scale));
        var height = Math.Max(1, (int)Math.Round(info.Height * scale));
        if (decoded.Width == width && decoded.Height == height)
        {
            return decoded;
        }

        using (decoded)
        {
            return decoded.Resize(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul),
                new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
    }
}
