using GibiOMatic.Conversion.Images;
using GibiOMatic.Core.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace GibiOMatic.Conversion.Tests;

public sealed class BookFileReaderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly BookFileReader _reader = new(NullLogger<BookFileReader>.Instance);

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void AssertRedCover(BookFileContent content)
    {
        Assert.NotNull(content.Cover);
        var (_, _, center) = SyntheticImages.Inspect(content.Cover);
        Assert.True(SyntheticImages.Near(center, SyntheticBooks.CoverColor), $"Cover centre is {center}");
    }

    [Fact]
    public async Task Cbz_reads_comic_info_and_takes_the_first_page_in_natural_order()
    {
        var path = SyntheticBooks.Cbz(_temp.Combine("saga.cbz"), pages: 12, shuffledNames: true,
            comicInfoXml: SyntheticBooks.ComicInfo(series: "Saga", number: "1", year: 2012, month: 10, writer: "Brian K. Vaughan",
                penciller: "Fiona Staples", publisher: "Image Comics", gtin: "978-1-60706-601-9", format: "TPB",
                seriesGroup: "Massive-Verse,  Supermassive , massive-verse"));

        var content = await _reader.ReadAsync(path, BookFormat.Cbz, Ct);

        var m = content.Metadata;
        Assert.Equal("Saga", m.Series);
        Assert.Equal("1", m.Number);
        Assert.Equal(["Massive-Verse", "Supermassive"], m.Universes);
        Assert.Equal(new PartialDate(2012, 10), m.Date);
        Assert.Equal("Image Comics", m.Publisher);
        Assert.Equal("9781607066019", m.Isbn);
        Assert.Equal(BookKind.Tpb, m.Kind);
        Assert.Equal([new Credit(CreditRole.Writer, "Brian K. Vaughan"), new Credit(CreditRole.Artist, "Fiona Staples")], m.Credits);
        Assert.Equal(12, m.PageCount);
        Assert.Empty(content.Warnings);
        AssertRedCover(content);
    }

    [Fact]
    public async Task Cbr_is_read_like_a_cbz()
    {
        var path = SyntheticBooks.Cbr(_temp.Combine("plutona.cbr"), comicInfoXml: SyntheticBooks.ComicInfo(title: "Plutona", writer: "Jeff Lemire, Emi Lenox"));

        var content = await _reader.ReadAsync(path, BookFormat.Cbr, Ct);

        Assert.Equal("Plutona", content.Metadata.Title);
        Assert.Equal(["Jeff Lemire", "Emi Lenox"], content.Metadata.Credits.Select(c => c.Name));
        Assert.Equal(3, content.Metadata.PageCount);
        AssertRedCover(content);
    }

    [Fact]
    public async Task Epub_reads_the_opf_and_the_declared_cover()
    {
        var path = SyntheticBooks.Epub(_temp.Combine("book.epub"), new SyntheticBooks.EpubOptions
        {
            Title = "Deadly Class, Vol. 11",
            Creators = [("Rick Remender", "aut"), ("Wes Craig", "ill"), ("Jordan Boyd", "clr")],
            Publisher = "Image Comics",
            Date = "2022-03-15",
            Isbn = "9781534321236",
            CalibreSeries = "Deadly Class",
            CalibreSeriesIndex = "11.0",
        });

        var content = await _reader.ReadAsync(path, BookFormat.Epub, Ct);

        var m = content.Metadata;
        Assert.Equal("Deadly Class, Vol. 11", m.Title);
        Assert.Equal(new PartialDate(2022, 3, 15), m.Date);
        Assert.Equal("9781534321236", m.Isbn);
        Assert.Equal(("Deadly Class", "11"), (m.Series, m.Number));
        Assert.Equal([CreditRole.Writer, CreditRole.Artist, CreditRole.Colorist], m.Credits.Select(c => c.Role));
        Assert.False(m.IsTextBook);
        Assert.False(m.LooksConverted);
        AssertRedCover(content);
    }

    [Fact]
    public async Task Epub_without_a_declared_cover_uses_the_first_page_image()
    {
        var path = SyntheticBooks.Epub(_temp.Combine("nocover.epub"), new SyntheticBooks.EpubOptions { DeclareCover = false, FixedLayout = false });

        var content = await _reader.ReadAsync(path, BookFormat.Epub, Ct);

        AssertRedCover(content);
    }

    [Fact]
    public async Task A_text_epub_converted_by_calibre_is_flagged()
    {
        var path = SyntheticBooks.Epub(_temp.Combine("novel.epub"), new SyntheticBooks.EpubOptions
        {
            TextOnly = true, FixedLayout = false, DeclareCover = false, Pages = 10, Generator = "calibre (7.2.0)",
        });

        var content = await _reader.ReadAsync(path, BookFormat.Epub, Ct);

        Assert.True(content.Metadata.IsTextBook);
        Assert.True(content.Metadata.LooksConverted);
        Assert.Null(content.Cover);
    }

    [Fact]
    public async Task Pdf_reads_info_and_renders_page_one()
    {
        var path = SyntheticBooks.Pdf(_temp.Combine("Pasta com acentuação", "radiant.pdf"), new SyntheticBooks.PdfOptions
        {
            Pages = 3, Title = "Radiant Black, Vol. 5", Author = "Kyle Higgins, Marcelo Costa", Producer = "Adobe InDesign",
        });

        var content = await _reader.ReadAsync(path, BookFormat.Pdf, Ct);

        var m = content.Metadata;
        Assert.Equal("Radiant Black, Vol. 5", m.Title);
        Assert.Equal(["Kyle Higgins", "Marcelo Costa"], m.Credits.Select(c => c.Name));
        Assert.Equal(3, m.PageCount);
        Assert.Null(m.Date);
        Assert.False(m.LooksConverted);
        Assert.Empty(content.Warnings);
        AssertRedCover(content);
    }

    [Fact]
    public async Task Pdf_prefers_xmp_and_detects_calibre()
    {
        var path = SyntheticBooks.Pdf(_temp.Combine("xmp.pdf"), new SyntheticBooks.PdfOptions
        {
            Title = "file-name-title", Producer = "calibre 7.2", Xmp = SyntheticBooks.Xmp("Saga, Vol. 1", "Brian K. Vaughan", "9781607066019"),
        });

        var content = await _reader.ReadAsync(path, BookFormat.Pdf, Ct);

        Assert.Equal("Saga, Vol. 1", content.Metadata.Title);
        Assert.Equal("Brian K. Vaughan", Assert.Single(content.Metadata.Credits).Name);
        Assert.Equal("9781607066019", content.Metadata.Isbn);
        Assert.True(content.Metadata.LooksConverted);
    }

    [Fact]
    public async Task A_damaged_file_returns_a_warning_instead_of_throwing()
    {
        var path = _temp.Combine("broken.pdf");
        await File.WriteAllTextAsync(path, "%PDF-1.4 this is not really a pdf", Ct);

        var content = await _reader.ReadAsync(path, BookFormat.Pdf, Ct);

        Assert.Null(content.Cover);
        Assert.NotEmpty(content.Warnings);
    }

    [Fact]
    public void Covers_and_thumbnails_are_scaled_jpegs()
    {
        var big = SyntheticImages.Png(2000, 3000, SyntheticBooks.CoverColor);

        var cover = SyntheticImages.Inspect(CoverImages.CreateCover(big)!);
        var thumb = SyntheticImages.Inspect(CoverImages.CreateThumbnail(big)!);

        Assert.Equal((800, 1200), (cover.Width, cover.Height));
        Assert.Equal((240, 360), (thumb.Width, thumb.Height));
        Assert.True(SyntheticImages.Near(thumb.Center, SyntheticBooks.CoverColor));
        Assert.Null(CoverImages.CreateCover([1, 2, 3]));
    }
}

public sealed class FormatDetectorTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Detects_formats_by_content_not_by_extension()
    {
        Assert.Equal(BookFormat.Cbz, FormatDetector.Detect(SyntheticBooks.Cbz(_temp.Combine("a.cbz"))));
        Assert.Equal(BookFormat.Epub, FormatDetector.Detect(SyntheticBooks.Epub(_temp.Combine("b.epub"))));
        Assert.Equal(BookFormat.Pdf, FormatDetector.Detect(SyntheticBooks.Pdf(_temp.Combine("c.pdf"))));
        Assert.Equal(BookFormat.Cbr, FormatDetector.Detect(SyntheticBooks.Cbr(_temp.Combine("d.cbr"))));

        // A RAR disguised as CBZ, and an EPUB without extension.
        Assert.Equal(BookFormat.Cbr, FormatDetector.Detect(SyntheticBooks.Cbr(_temp.Combine("fake.cbz"))));
        Assert.Equal(BookFormat.Epub, FormatDetector.Detect(SyntheticBooks.Epub(_temp.Combine("noext"))));
    }

    [Fact]
    public void Unknown_content_is_not_a_book()
    {
        var path = _temp.Combine("notes.txt");
        File.WriteAllText(path, "hello");

        Assert.Null(FormatDetector.Detect(path));
    }
}

public sealed class NaturalStringComparerTests
{
    [Fact]
    public void Orders_numbers_by_value() =>
        Assert.Equal(
            ["page1.jpg", "Page2.jpg", "page10.jpg", "page010b.jpg", "page11.jpg"],
            new[] { "page11.jpg", "page10.jpg", "Page2.jpg", "page010b.jpg", "page1.jpg" }.Order(NaturalStringComparer.Instance));
}
