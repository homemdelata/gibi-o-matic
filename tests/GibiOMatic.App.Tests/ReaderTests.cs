using System.Globalization;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
using GibiOMatic.App.ViewModels;
using GibiOMatic.Conversion.Reading;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Settings;
using GibiOMatic.Library.Reading;
using GibiOMatic.Metadata.Identification;
using Microsoft.Extensions.Logging.Abstractions;

namespace GibiOMatic.App.Tests;

public sealed class ReaderTests : IDisposable
{
    private readonly AppTestContext _app = new();

    public void Dispose() => _app.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private LibrarySession Session => _app.Get<LibrarySession>();

    private string Downloads(string name) => _app.Folder(Path.Combine("Downloads", name));

    private ReaderViewModel Reader(string path, BookFormat format, string bookId = "book-1") =>
        new(new ReaderRequest(bookId, "Saga, Vol. 1", path, format), _app.Get<ISettingsService>(), _app.Get<IUiThread>(),
            _app.Get<IReaderImages>(), new ReadingStateStore(Session.Index!), NullLogger.Instance);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(25, Ct);
        }

        Assert.True(condition());
    }

    private static string Pages(int first, int count) => string.Format(CultureInfo.CurrentCulture, Strings.Reader_PageOf, first, count);

    private static string Pages(int first, int second, int count) =>
        string.Format(CultureInfo.CurrentCulture, Strings.Reader_PagesOf, first, second, count);

    private async Task ImportAsync(params string[] files)
    {
        await Session.CreateAsync(_app.Folder("Arquivo"), Ct);
        await Session.Queue!.AddAsync(files, Ct);
        await Session.Queue.WaitUntilIdleAsync(Ct);
        await Session.Queue.ConfirmAsync(Ct);
        await Session.Queue.WaitUntilIdleAsync(Ct);
    }

    [Fact]
    public void Two_pages_keep_the_cover_alone_and_wide_pages_alone()
    {
        var p = new PageSize(200, 300);
        var w = new PageSize(400, 300);
        PageSize[] sizes = [p, p, p, p, w, p, p];

        Assert.Equal([[0], [1, 2], [3], [4], [5, 6]], ReaderSpreads.Compute(sizes, twoPages: true));
        Assert.Equal(7, ReaderSpreads.Compute(sizes, twoPages: false).Count);
    }

    [Fact]
    public async Task The_library_panel_opens_the_book_and_each_file_in_the_reader()
    {
        await ImportAsync(SyntheticBooks.Cbz(Downloads("saga.cbz"), comicInfoXml: SyntheticBooks.ComicInfo(series: "Saga", number: "1")));
        var library = _app.Get<LibraryPageViewModel>();
        await library.LoadAsync(Ct);
        var details = library.Selected!;

        details.OpenCommand.Execute(null);
        details.Files[0].Open.Execute(null);

        Assert.Equal(2, _app.Reader.Opened.Count);
        var request = _app.Reader.Opened[0];
        Assert.Equal(details.Card.Book.Metadata.Id, request.BookId);
        Assert.Equal(BookFormat.Cbz, request.Format);
        Assert.True(File.Exists(request.FilePath));
        Assert.StartsWith(_app.Folder("Arquivo"), request.FilePath);
    }

    [Fact]
    public async Task The_review_opens_the_book_under_review()
    {
        _app.Identifier.Respond = (_, _) => [FakeIdentifier.Candidate("Saga, Vol. 1", "Saga", "1", 0.6)];
        await ImportAsync(SyntheticBooks.Pdf(Downloads("saga_vol1.pdf")));
        var review = _app.Get<ReviewPageViewModel>();
        for (var i = 0; i < 200 && review.Selected is null; i++)
        {
            await review.OnNavigatedToAsync(Ct);
            if (review.Selected is null)
            {
                await Task.Delay(50, Ct);
            }
        }

        review.OpenBookCommand.Execute(null);

        var request = Assert.Single(_app.Reader.Opened);
        Assert.Equal(review.Selected!.BookId, request.BookId);
        Assert.Equal(BookFormat.Pdf, request.Format);
    }

    [Fact]
    public async Task Pages_turn_in_one_or_two_page_mode_and_the_book_reopens_where_it_was_left()
    {
        await Session.CreateAsync(_app.Folder("Arquivo"), Ct);
        var path = SyntheticBooks.Cbz(Downloads("saga.cbz"), pages: 12);
        var reader = Reader(path, BookFormat.Cbz);

        await reader.LoadAsync(Ct);

        Assert.True(reader.IsPaged);
        Assert.Null(reader.ErrorText);
        Assert.Equal(12, reader.PageCount);
        Assert.Equal(12, reader.Thumbnails.Count);
        Assert.Equal(Pages(1, 12), reader.PageText);
        var cover = Assert.IsType<FakeImage>(reader.LeftImage);
        Assert.True(SyntheticImages.Near(SyntheticBooks.CoverColor, cover.Center));
        Assert.Null(reader.RightImage);

        reader.NextCommand.Execute(null);
        Assert.Equal(Pages(2, 12), reader.PageText);
        await WaitForAsync(() => reader.LeftImage is FakeImage { Center: var c } && SyntheticImages.Near(SyntheticBooks.PageColor, c));

        // Two pages: the cover alone, then 2–3, 4–5…
        reader.ToggleTwoPagesCommand.Execute(null);
        Assert.Equal(Pages(2, 3, 12), reader.PageText);
        await WaitForAsync(() => reader.RightImage is not null);

        // Right to left: the left arrow goes forward.
        reader.ToggleRightToLeftCommand.Execute(null);
        reader.GoLeftCommand.Execute(null);
        Assert.Equal(Pages(4, 5, 12), reader.PageText);
        reader.GoRightCommand.Execute(null);
        Assert.Equal(Pages(2, 3, 12), reader.PageText);

        reader.LastCommand.Execute(null);
        Assert.Equal(Pages(12, 12), reader.PageText);
        Assert.True(reader.Thumbnails[11].IsCurrent);

        // Thumbnails are rendered when the strip shows them.
        Assert.Null(reader.Thumbnails[0].Image);
        await WaitForAsync(() => reader.Thumbnails[0].Image is not null);

        await reader.DisposeAsync();

        // Reopened: same page, same direction; two-page mode is a preference for every book.
        await WaitForAsync(() => _app.Get<ISettingsService>().Current.Reader.TwoPages);
        var again = Reader(path, BookFormat.Cbz);
        await again.LoadAsync(Ct);
        Assert.Equal(Pages(12, 12), again.PageText);
        Assert.True(again.RightToLeft);
        Assert.True(again.TwoPages);
        await again.DisposeAsync();

        // Another file of the same book starts at the beginning.
        var other = Reader(SyntheticBooks.Cbz(Downloads("saga-hd.cbz"), pages: 12), BookFormat.Cbz);
        await other.LoadAsync(Ct);
        Assert.Equal(Pages(1, 12), other.PageText);
        await other.DisposeAsync();
    }

    [Fact]
    public async Task A_pdf_opens_with_its_pages_rendered()
    {
        await Session.CreateAsync(_app.Folder("Arquivo"), Ct);
        var reader = Reader(SyntheticBooks.Pdf(Downloads("plutona.pdf"), new SyntheticBooks.PdfOptions { Pages = 3, TextOverPages = true }), BookFormat.Pdf);

        await reader.LoadAsync(Ct);
        Assert.Equal(Pages(1, 3), reader.PageText);
        Assert.IsType<FakeImage>(reader.LeftImage);

        // A bigger window or zooming in renders a sharper page.
        var before = reader.LeftImage!.Size.Height;
        reader.NeededHeight = (int)before * 2;
        await WaitForAsync(() => reader.LeftImage!.Size.Height >= before * 2 - 1);
        await reader.DisposeAsync();
    }

    [Fact]
    public async Task A_damaged_file_shows_a_message_instead_of_failing()
    {
        await Session.CreateAsync(_app.Folder("Arquivo"), Ct);
        var path = Downloads("broken.cbz");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "not a zip", Ct);
        var reader = Reader(path, BookFormat.Cbz);

        await reader.LoadAsync(Ct);

        Assert.False(reader.IsLoading);
        Assert.False(reader.IsPaged);
        Assert.NotNull(reader.ErrorText);
        await reader.DisposeAsync();
    }

    [Fact]
    public async Task A_text_epub_shows_its_chapters_only_from_the_book()
    {
        await Session.CreateAsync(_app.Folder("Arquivo"), Ct);
        var path = SyntheticBooks.Epub(Downloads("novel.epub"),
            new SyntheticBooks.EpubOptions { TextOnly = true, FixedLayout = false, Pages = 3, Nav = true });
        var reader = Reader(path, BookFormat.Epub);
        var shown = new List<Uri>();
        reader.NavigationRequested += (_, uri) => shown.Add(uri);
        var closed = false;
        reader.CloseRequested += (_, _) => closed = true;

        await reader.LoadAsync(Ct);

        Assert.True(reader.IsText);
        Assert.False(reader.IsPaged);
        Assert.Equal(["Chapter 1", "Chapter 2", "Chapter 3"], reader.Toc.Select(t => t.Title));
        Assert.True(reader.Toc[0].IsCurrent);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Reader_ChapterOf, 1, 3), reader.ChapterText);
        var first = Assert.Single(shown);
        Assert.Equal("127.0.0.1", first.Host);
        using (var http = new HttpClient())
        {
            Assert.Contains("Chapter 1", await http.GetStringAsync(first, Ct));
        }

        reader.Toc[2].OpenCommand.Execute(null);
        Assert.Equal(2, reader.Chapter);
        Assert.EndsWith("p0003.xhtml", shown[^1].AbsolutePath);

        // Links inside the book are followed; anything else is not loaded.
        Assert.True(reader.AllowNavigation(shown[0]));
        Assert.False(reader.AllowNavigation(new Uri("https://example.com/")));
        reader.OnNavigated(shown[0]);
        Assert.Equal(0, reader.Chapter);

        reader.TextBiggerCommand.Execute(null);
        Assert.Equal(110, reader.TextScale);
        Assert.Contains("v=", shown[^1].Query);
        await WaitForAsync(() => _app.Get<ISettingsService>().Current.Reader.TextScale == 110);

        reader.EscapeCommand.Execute(null);
        Assert.True(closed);
        await reader.DisposeAsync();
    }
}
