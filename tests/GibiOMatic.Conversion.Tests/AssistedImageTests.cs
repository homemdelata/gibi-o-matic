using GibiOMatic.Conversion.Images;
using GibiOMatic.Core.Domain;
using SkiaSharp;

namespace GibiOMatic.Conversion.Tests;

/// <summary>The cover check and the pages given to Claude (SPEC §8.4).</summary>
public sealed class AssistedImageTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>A "cover" with a drawing, so the hash has something to see (a plain colour hashes to zero).</summary>
    private static byte[] Cover(int width, int height, SKColor background, SKColor shape, bool flipped = false, int quality = 90)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(background);
        using var paint = new SKPaint { Color = shape, IsAntialias = true };
        var x = flipped ? width * 0.7f : width * 0.3f;
        canvas.DrawCircle(x, height * 0.35f, width * 0.22f, paint);
        canvas.DrawRect(width * 0.1f, height * 0.7f, width * 0.8f, height * 0.12f, paint);
        using var gradient = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, 0), [SKColors.Transparent, SKColors.Black.WithAlpha(120)],
                SKShaderTileMode.Clamp),
        };
        canvas.DrawRect(0, 0, width, height, gradient);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    [Fact]
    public void The_same_cover_at_another_size_and_quality_is_the_same_image()
    {
        var book = PerceptualHash.Compute(Cover(1200, 1800, SKColors.Navy, SKColors.Gold));
        var record = PerceptualHash.Compute(Cover(160, 240, SKColors.Navy, SKColors.Gold, quality: 60));

        Assert.True(PerceptualHash.Same(book, record));
        Assert.True(PerceptualHash.Distance(book!.Value, record!.Value) <= PerceptualHash.SameImageDistance);
    }

    [Fact]
    public void Another_cover_is_another_image()
    {
        var book = PerceptualHash.Compute(Cover(600, 900, SKColors.Navy, SKColors.Gold));
        var variant = PerceptualHash.Compute(Cover(600, 900, SKColors.Navy, SKColors.Gold, flipped: true));

        Assert.False(PerceptualHash.Same(book, variant));
    }

    [Fact]
    public void Something_that_is_not_an_image_has_no_hash() =>
        Assert.Null(PerceptualHash.Same(PerceptualHash.Compute([1, 2, 3]), 0));

    [Theory]
    [InlineData(1, new int[0])]
    [InlineData(3, new[] { 1, 2 })]
    [InlineData(10, new[] { 1, 2, 3, 8, 9 })]
    public void The_credits_pages_are_the_three_after_the_cover_and_the_last_two(int pages, int[] expected) =>
        Assert.Equal(expected, PageSnapshots.CreditPages(pages));

    [Fact]
    public void The_chosen_pages_of_a_cbz_are_written_as_jpeg()
    {
        var cbz = SyntheticBooks.Cbz(_temp.Combine("book.cbz"), pages: 6);
        var folder = Directory.CreateDirectory(_temp.Combine("run")).FullName;

        var (files, count) = PageSnapshots.Write(cbz, BookFormat.Cbz, folder, "page-", PageSnapshots.CreditPages, TestContext.Current.CancellationToken);

        Assert.Equal(6, count);
        Assert.Equal(["page-002.jpg", "page-003.jpg", "page-004.jpg", "page-005.jpg", "page-006.jpg"], files);
        Assert.All(files, f => Assert.True(SyntheticImages.Inspect(File.ReadAllBytes(Path.Combine(folder, f))).Width > 0));
    }

    [Fact]
    public void The_pages_of_a_pdf_are_rendered_no_bigger_than_the_limit()
    {
        var pdf = SyntheticBooks.Pdf(_temp.Combine("book.pdf"), new SyntheticBooks.PdfOptions { Pages = 4 });
        var folder = Directory.CreateDirectory(_temp.Combine("run")).FullName;

        var (files, count) = PageSnapshots.Write(pdf, BookFormat.Pdf, folder, "p", _ => [1], TestContext.Current.CancellationToken);

        Assert.Equal(4, count);
        var (width, height, _) = SyntheticImages.Inspect(File.ReadAllBytes(Path.Combine(folder, Assert.Single(files))));
        Assert.True(Math.Max(width, height) <= PageSnapshots.PageSize);
    }
}
