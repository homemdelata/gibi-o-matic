using System.IO.Compression;
using System.Xml.Linq;
using GibiOMatic.Conversion.Cbz;
using GibiOMatic.Core.Domain;

namespace GibiOMatic.Conversion.Tests;

public sealed class CbzBuilderTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly BookMetadata Saga = new()
    {
        Id = "01ARYZ6S41TSV4RRFFQ69G5FAV",
        Title = "Saga, Vol. 1",
        Kind = BookKind.Tpb,
        Series = new SeriesInfo("Saga", "1"),
        Date = new PartialDate(2012, 10, 10),
        Publisher = "Image Comics",
        Credits = [new Credit(CreditRole.Writer, "Brian K. Vaughan"), new Credit(CreditRole.Artist, "Fiona Staples")],
        Identifiers = new BookIdentifiers { Isbn = "9781607066019", ComicVine = "4000-1" },
        Summary = "Space opera.",
    };

    private string Target => _temp.Combine("out", "book.cbz");

    private static (List<string> Pages, XDocument ComicInfo, ZipArchive Zip) Open(string path)
    {
        var zip = ZipFile.OpenRead(path);
        var pages = zip.Entries.Select(e => e.FullName).Where(n => n != "ComicInfo.xml").ToList();
        var info = XDocument.Load(zip.GetEntry("ComicInfo.xml")!.Open());
        return (pages, info, zip);
    }

    private static byte[] Bytes(ZipArchive zip, string name)
    {
        using var stream = zip.GetEntry(name)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    [Fact]
    public async Task Pdf_pages_that_are_one_jpeg_are_copied_without_recompression()
    {
        var pdf = SyntheticBooks.Pdf(_temp.Combine("saga.pdf"), new SyntheticBooks.PdfOptions { Pages = 3 });

        var result = await CbzBuilder.BuildAsync(pdf, BookFormat.Pdf, Saga, Target, null, Ct);

        Assert.Equal(new CbzResult(3, 3), result);
        var (pages, _, zip) = Open(Target);
        using (zip)
        {
            Assert.Equal(["0001.jpg", "0002.jpg", "0003.jpg"], pages);
            Assert.Equal(SyntheticImages.Jpeg(200, 300, SyntheticBooks.CoverColor), Bytes(zip, "0001.jpg"));
        }
    }

    [Fact]
    public async Task Pdf_jpegs_tagged_with_an_icc_profile_are_copied_too()
    {
        var pdf = SyntheticBooks.Pdf(_temp.Combine("humble.pdf"), new SyntheticBooks.PdfOptions { Pages = 2, IccBased = true });

        var result = await CbzBuilder.BuildAsync(pdf, BookFormat.Pdf, Saga, Target, null, Ct);

        Assert.Equal(new CbzResult(2, 2), result);
    }

    [Fact]
    public async Task Pdf_pages_copied_and_rendered_keep_their_order()
    {
        var pdf = SyntheticBooks.Pdf(_temp.Combine("mixed.pdf"), new SyntheticBooks.PdfOptions { Pages = 9, TextOverEvenPages = true });
        var reported = new List<double>();

        var result = await CbzBuilder.BuildAsync(pdf, BookFormat.Pdf, Saga, Target, new SyncProgress<double>(reported.Add), Ct);

        Assert.Equal(new CbzResult(9, 5), result);
        Assert.Equal(1.0, reported[^1]);
        Assert.Equal(reported.Order(), reported);
        var (pages, _, zip) = Open(Target);
        using (zip)
        {
            Assert.Equal(Enumerable.Range(1, 9).Select(n => $"{n:0000}.jpg"), pages);
            for (var n = 1; n <= 9; n++)
            {
                var height = SyntheticImages.Inspect(Bytes(zip, $"{n:0000}.jpg")).Height;
                Assert.Equal(n % 2 == 1 ? 300 : 1200, height);
            }
        }
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    [Fact]
    public async Task Pdf_pages_with_lettering_on_top_are_rendered()
    {
        var pdf = SyntheticBooks.Pdf(_temp.Combine("radiant.pdf"), new SyntheticBooks.PdfOptions { Pages = 2, TextOverPages = true });

        var result = await CbzBuilder.BuildAsync(pdf, BookFormat.Pdf, Saga, Target, null, Ct);

        Assert.Equal(new CbzResult(2, 0), result);
        var (_, _, zip) = Open(Target);
        using (zip)
        {
            var page = SyntheticImages.Inspect(Bytes(zip, "0001.jpg"));
            Assert.Equal(1200, page.Height); // the 300 px image on a 600 pt page: rendered at the minimum height
            Assert.True(SyntheticImages.Near(page.Center, SyntheticBooks.CoverColor));
        }
    }

    [Fact]
    public async Task Cbz_keeps_its_pages_in_natural_order_and_gets_a_new_comic_info()
    {
        var cbz = SyntheticBooks.Cbz(_temp.Combine("saga.cbz"), pages: 11, shuffledNames: true, comicInfoXml: SyntheticBooks.ComicInfo(title: "old title"));

        var result = await CbzBuilder.BuildAsync(cbz, BookFormat.Cbz, Saga, Target, null, Ct);

        Assert.Equal(11, result.Pages);
        var (pages, info, zip) = Open(Target);
        using (zip)
        {
            Assert.Equal(11, pages.Count);
            Assert.True(SyntheticImages.Near(SyntheticImages.Inspect(Bytes(zip, "0001.jpg")).Center, SyntheticBooks.CoverColor));
            Assert.Equal("Saga, Vol. 1", info.Root!.Element("Title")!.Value);
            Assert.Single(zip.Entries, e => e.FullName == "ComicInfo.xml");
        }
    }

    [Fact]
    public async Task Cbr_and_epub_become_cbz()
    {
        var cbr = SyntheticBooks.Cbr(_temp.Combine("a.cbr"), pages: 4);
        Assert.Equal(4, (await CbzBuilder.BuildAsync(cbr, BookFormat.Cbr, Saga, Target, null, Ct)).Pages);

        var epub = SyntheticBooks.Epub(_temp.Combine("b.epub"), new SyntheticBooks.EpubOptions { Pages = 5 });
        var target = _temp.Combine("out", "epub.cbz");
        Assert.Equal(5, (await CbzBuilder.BuildAsync(epub, BookFormat.Epub, Saga, target, null, Ct)).Pages);
        var (_, _, zip) = Open(target);
        using (zip)
        {
            Assert.True(SyntheticImages.Near(SyntheticImages.Inspect(Bytes(zip, "0001.jpg")).Center, SyntheticBooks.CoverColor));
        }
    }

    [Fact]
    public void Comic_info_has_the_spec_fields_and_one_shots_are_series_of_one()
    {
        var info = XDocument.Parse(ComicInfoWriter.Write(Saga, 144)).Root!;
        string? V(string name) => info.Element(name)?.Value;

        Assert.Equal(("Saga", "1", "2012", "10", "10"), (V("Series"), V("Number"), V("Year"), V("Month"), V("Day")));
        Assert.Equal(("Brian K. Vaughan", "Fiona Staples", "Image Comics"), (V("Writer"), V("Penciller"), V("Publisher")));
        Assert.Equal(("TPB", "9781607066019", "144"), (V("Format"), V("GTIN"), V("PageCount")));
        Assert.Equal("https://comicvine.gamespot.com/issue/4000-1/", V("Web"));
        Assert.Null(V("SeriesGroup"));

        // Universes go in SeriesGroup, which Komga turns into collections.
        var grouped = XDocument.Parse(ComicInfoWriter.Write(Saga with { Universes = ["Massive-Verse", "Supermassive"] }, 144)).Root!;
        Assert.Equal("Massive-Verse, Supermassive", grouped.Element("SeriesGroup")?.Value);

        var plutona = XDocument.Parse(ComicInfoWriter.Write(Saga with { Title = "Plutona", Series = null, OneShot = true }, 10)).Root!;
        Assert.Equal(("Plutona", "1", "1"), (plutona.Element("Series")?.Value, plutona.Element("Number")?.Value, plutona.Element("Count")?.Value));
    }

    [Fact]
    public void An_author_goes_in_the_writer_field()
    {
        var book = Saga with { Credits = [new Credit(CreditRole.Author, "Jim Lee"), new Credit(CreditRole.Writer, "Brian K. Vaughan")] };

        var info = XDocument.Parse(ComicInfoWriter.Write(book, 144)).Root!;

        Assert.Equal("Jim Lee, Brian K. Vaughan", info.Element("Writer")?.Value);
    }
}
