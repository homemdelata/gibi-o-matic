using System.Net;
using System.Text;
using GibiOMatic.Conversion.Reading;
using GibiOMatic.Core.Domain;
using SkiaSharp;

namespace GibiOMatic.Conversion.Tests;

public sealed class ReaderTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SKColor Center(SKBitmap bitmap) => bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);

    [Theory]
    [InlineData(BookFormat.Cbz)]
    [InlineData(BookFormat.Cbr)]
    [InlineData(BookFormat.Epub)]
    [InlineData(BookFormat.Pdf)]
    public void Pages_open_in_reading_order_and_render_to_fit(BookFormat format)
    {
        var path = format switch
        {
            BookFormat.Cbz => SyntheticBooks.Cbz(_temp.Combine("b.cbz"), pages: 12),
            BookFormat.Cbr => SyntheticBooks.Cbr(_temp.Combine("b.cbr"), pages: 12),
            BookFormat.Epub => SyntheticBooks.Epub(_temp.Combine("b.epub"), new SyntheticBooks.EpubOptions { Pages = 12 }),
            _ => SyntheticBooks.Pdf(_temp.Combine("b.pdf"), new SyntheticBooks.PdfOptions { Pages = 12 }),
        };

        using var book = BookReader.Open(path, format);
        Assert.Null(book.Text);
        var pages = Assert.IsAssignableFrom<IPageSource>(book.Pages);
        Assert.Equal(12, pages.PageCount);

        // Images are 200×300; a PDF page is 400×600 pt, 533×800 at 100%.
        var size = pages.GetSize(0);
        Assert.Equal(format == BookFormat.Pdf ? new PageSize(533, 800) : new PageSize(200, 300), size);
        Assert.False(size.IsWide);

        using (var cover = pages.Render(0, 100, 100, Ct))
        {
            Assert.Equal(100, cover.Height, tolerance: 1);
            Assert.True(SyntheticImages.Near(SyntheticBooks.CoverColor, Center(cover)), Center(cover).ToString());
        }

        // The tenth page comes after the second one (natural order of "page2"/"page10" in archives).
        using var tenth = pages.Render(9, 1000, 1000, Ct);
        Assert.True(SyntheticImages.Near(SyntheticBooks.PageColor, Center(tenth)), Center(tenth).ToString());
        if (format != BookFormat.Pdf)
        {
            Assert.Equal(300, tenth.Height); // never larger than the image
        }
    }

    [Fact]
    public void A_text_epub_opens_as_chapters_with_its_table_of_contents()
    {
        var path = SyntheticBooks.Epub(_temp.Combine("novel.epub"),
            new SyntheticBooks.EpubOptions { TextOnly = true, FixedLayout = false, Pages = 3, Nav = true, Title = "A Novel" });

        using var book = BookReader.Open(path, BookFormat.Epub);
        Assert.Null(book.Pages);
        var text = Assert.IsType<EpubBook>(book.Text);
        Assert.Equal("A Novel", text.Title);
        Assert.Equal(["OEBPS/p0001.xhtml", "OEBPS/p0002.xhtml", "OEBPS/p0003.xhtml"], text.Chapters.Select(c => c.Path));
        Assert.Equal(["Chapter 1", "Chapter 2", "Chapter 3"], text.Toc.Select(t => t.Title));
        Assert.Equal([0, 1, 2], text.Toc.Select(t => t.Chapter));
        Assert.Equal("Chapter 2", text.Chapters[1].Title);
        Assert.Equal(1, text.ChapterOf("oebps/P0002.xhtml"));
    }

    [Fact]
    public void Chapters_lose_scripts_and_get_a_policy_that_blocks_them()
    {
        var html = """
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE html PUBLIC "-//W3C//DTD XHTML 1.1//EN" "http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd">
            <html xmlns="http://www.w3.org/1999/xhtml"><head><title>T</title>
            <meta http-equiv="refresh" content="0;url=https://example.com"/>
            <link rel="stylesheet" href="style.css"/><link rel="prefetch" href="https://example.com/x"/>
            <script>alert(1)</script></head>
            <body onload="steal()"><p>Caf&eacute;&nbsp;&amp; more</p>
            <a href="javascript:alert(2)">bad</a> <a href="ch2.xhtml#s1">good</a>
            <img src="img/a.jpg" onerror="x()"/><iframe src="https://example.com"></iframe><object data="x.swf"></object>
            </body></html>
            """;

        var (clean, type) = EpubHtml.Sanitize(Encoding.UTF8.GetBytes(html), "../__gibiomatic/reader.css");

        Assert.StartsWith("application/xhtml+xml", type);
        Assert.DoesNotContain("<script", clean);
        Assert.DoesNotContain("alert", clean);
        Assert.DoesNotContain("onload", clean);
        Assert.DoesNotContain("onerror", clean);
        Assert.DoesNotContain("iframe", clean);
        Assert.DoesNotContain("<object", clean);
        Assert.DoesNotContain("refresh", clean);
        Assert.DoesNotContain("prefetch", clean);
        Assert.Contains("Café", clean);
        Assert.Contains(" &amp; more", clean);
        Assert.Contains("href=\"ch2.xhtml#s1\"", clean);
        Assert.Contains("href=\"style.css\"", clean);
        Assert.Contains("src=\"img/a.jpg\"", clean);
        Assert.Contains("Content-Security-Policy", clean);
        Assert.Contains("script-src 'none'", clean);
        // The reader's style sheet comes last, so its font size and colors win.
        Assert.True(clean.IndexOf("reader.css", StringComparison.Ordinal) > clean.IndexOf("style.css", StringComparison.Ordinal));
    }

    [Fact]
    public void Broken_markup_is_cleaned_as_text()
    {
        var html = "<html><head></head><body><p>Open <br> tags<script>alert(1)</script><img src=x onerror=alert(2)><a href='javascript:alert(3)'>x</a></body></html>";

        var (clean, type) = EpubHtml.Sanitize(Encoding.UTF8.GetBytes(html), "reader.css");

        Assert.StartsWith("text/html", type);
        Assert.DoesNotContain("alert", clean);
        Assert.Contains("Content-Security-Policy", clean);
        Assert.Contains("tags", clean);
    }

    [Fact]
    public async Task The_server_answers_only_the_book_on_its_token_with_the_policy_header()
    {
        var path = SyntheticBooks.Epub(_temp.Combine("novel.epub"), new SyntheticBooks.EpubOptions
        {
            TextOnly = true,
            FixedLayout = false,
            Pages = 2,
            ChapterBody = "<p onclick=\"go()\">Chapter {n}</p><script>alert(1)</script>",
        });
        using var book = EpubBook.Open(path);
        using var server = new EpubServer(book, () => "body { font-size: 120% }");
        using var http = new HttpClient();

        Assert.Equal(IPAddress.Loopback.ToString(), server.BaseUri.Host);
        var chapter = server.ChapterUri(1);
        Assert.Equal("OEBPS/p0002.xhtml", server.BookPathOf(chapter));

        using var response = await http.GetAsync(chapter, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("script-src 'none'", string.Join(' ', response.Headers.GetValues("Content-Security-Policy")));
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("Chapter 2", body);
        Assert.DoesNotContain("alert", body);
        Assert.DoesNotContain("onclick", body);
        Assert.Contains("../__gibiomatic/reader.css", body);

        var css = await http.GetStringAsync(new Uri(server.BaseUri, EpubServer.ReaderCssPath), Ct);
        Assert.Equal("body { font-size: 120% }", css);

        // Another token, a file outside the book, a missing file or another method: nothing.
        var other = new Uri($"http://127.0.0.1:{server.BaseUri.Port}/0123456789abcdef/OEBPS/p0002.xhtml");
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(other, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(new Uri(server.BaseUri, "OEBPS/nothing.xhtml"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.PostAsync(chapter, new StringContent("x"), Ct)).StatusCode);
        Assert.Null(server.BookPathOf(new Uri("https://example.com/OEBPS/p0002.xhtml")));
    }
}
