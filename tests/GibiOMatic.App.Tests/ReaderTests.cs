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
            _app.Get<IReaderImages>(), _app.Get<ReadingTracker>(), NullLogger.Instance);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        // Rendering is slow on the CI machines: a generous deadline, checked often.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline)
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

        // Pages shown (or rendered ahead) give their thumbnails.
        Assert.True(reader.Thumbnails[0].HasImage);

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
    public async Task The_pages_around_the_screen_are_rendered_ahead_and_the_strip_renders_the_rest()
    {
        await Session.CreateAsync(_app.Folder("Arquivo"), Ct);
        var reader = Reader(SyntheticBooks.Cbz(Downloads("saga.cbz"), pages: 20), BookFormat.Cbz);
        await reader.LoadAsync(Ct);
        reader.NextCommand.Execute(null);
        for (var i = 0; i < 8; i++)
        {
            reader.NextCommand.Execute(null);
        }

        // On page 10: pages 11–14 ahead and 8–9 behind are rendered without being asked for.
        Assert.Equal(Pages(10, 20), reader.PageText);
        await WaitForAsync(() => new[] { 7, 8, 10, 11, 12, 13 }.All(p => reader.Thumbnails[p].HasImage));
        Assert.False(reader.Thumbnails[16].HasImage);

        // Thumbnails of pages far away are rendered when the strip shows them (reading Image asks for
        // the render, which may finish before the getter returns: HasImage only looks).
        Assert.False(reader.Thumbnails[18].HasImage);
        _ = reader.Thumbnails[18].Image;
        await WaitForAsync(() => reader.Thumbnails[18].HasImage);
        await reader.DisposeAsync();
    }

    [Fact]
    public async Task Pages_rendered_ahead_before_the_sizes_are_read_are_sharp()
    {
        await Session.CreateAsync(_app.Folder("Arquivo"), Ct);
        var reader = Reader(SyntheticBooks.Cbz(Downloads("saga.cbz"), pages: 20), BookFormat.Cbz);
        await reader.LoadAsync(Ct);

        // Page 2 is rendered ahead right away, while the real page sizes are still being read.
        reader.NextCommand.Execute(null);
        await WaitForAsync(() => reader.LeftImage is FakeImage { Center: var c } && SyntheticImages.Near(SyntheticBooks.PageColor, c));

        Assert.Equal(300, reader.LeftImage!.Size.Height);
        await reader.DisposeAsync();
    }

    [Fact]
    public async Task A_pdf_opens_with_its_pages_rendered()
    {
        await Session.CreateAsync(_app.Folder("Arquivo"), Ct);
        var reader = Reader(SyntheticBooks.Pdf(Downloads("plutona.pdf"), new SyntheticBooks.PdfOptions { Pages = 3, TextOverPages = true }), BookFormat.Pdf);

        await reader.LoadAsync(Ct);
        Assert.Equal(Pages(1, 3), reader.PageText);
        Assert.IsType<FakeImage>(reader.LeftImage);
        Assert.False(reader.IsPageLoading);

        // The thumbnail of a page shown comes from that render (slow PDFs render nothing extra).
        Assert.True(reader.Thumbnails[0].HasImage);

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

    private async Task<IReadOnlyDictionary<string, BookReading>> ReadingsAsync() =>
        await new ReadingStateStore(Session.Index!).GetAllAsync(Ct);

    private async Task TurnAsync(ReaderViewModel reader, int times)
    {
        for (var i = 0; i < times; i++)
        {
            reader.NextCommand.Execute(null);
            await Task.Yield();
        }
    }

    [Fact]
    public async Task A_book_counts_as_started_after_five_pages_and_read_at_its_last_page()
    {
        await Session.CreateAsync(_app.Folder("Arquivo"), Ct);
        var path = SyntheticBooks.Cbz(Downloads("saga.cbz"), pages: 12);

        // Opened to look at the cover and the last page: neither started nor read.
        var peek = Reader(path, BookFormat.Cbz);
        await peek.LoadAsync(Ct);
        peek.LastCommand.Execute(null);
        await peek.DisposeAsync();
        var reading = (await ReadingsAsync())["book-1"];
        Assert.False(reading.Started);
        Assert.False(reading.IsRead);

        // Six pages shown: being read.
        var reader = Reader(path, BookFormat.Cbz);
        await reader.LoadAsync(Ct);
        reader.FirstCommand.Execute(null);
        await TurnAsync(reader, 5);
        await reader.DisposeAsync();
        reading = (await ReadingsAsync())["book-1"];
        Assert.True(reading.IsReading);
        Assert.Equal(12, reading.Total);
        Assert.Equal(6 / 12.0, reading.Fraction, 3);

        // Started before: jumping to the end now finishes it.
        var again = Reader(path, BookFormat.Cbz);
        await again.LoadAsync(Ct);
        again.LastCommand.Execute(null);
        await again.DisposeAsync();
        reading = (await ReadingsAsync())["book-1"];
        Assert.True(reading.IsRead);
        Assert.False(reading.IsReading);
    }

    [Fact]
    public async Task The_library_shows_continue_reading_filters_by_status_and_marks_books()
    {
        await ImportAsync(
            SyntheticBooks.Cbz(Downloads("saga1.cbz"), pages: 12, comicInfoXml: SyntheticBooks.ComicInfo(series: "Saga", number: "1")),
            SyntheticBooks.Cbz(Downloads("saga2.cbz"), pages: 12, comicInfoXml: SyntheticBooks.ComicInfo(series: "Saga", number: "2")),
            SyntheticBooks.Cbz(Downloads("paper.cbz"), pages: 12, comicInfoXml: SyntheticBooks.ComicInfo(series: "Paper Girls", number: "1")));
        var library = _app.Get<LibraryPageViewModel>();
        await library.LoadAsync(Ct);
        library.SetViewModeCommand.Execute(LibraryViewMode.Books);
        var cards = library.VisibleCards.Cast<BookCardViewModel>().ToList();
        Assert.Equal(3, cards.Count);
        Assert.False(library.ShowContinueReading);
        Assert.Equal(3, library.Filters.Single(f => f.Filter == LibraryFilter.Unread).Count);

        // Double click opens the book; reading it (in the reader) puts it in "Continuar lendo".
        var saga2 = cards.Single(c => c.Book.Metadata.Series?.Number == "2");
        saga2.OpenCommand.Execute(null);
        var request = Assert.Single(_app.Reader.Opened);
        var reader = new ReaderViewModel(request, _app.Get<ISettingsService>(), _app.Get<IUiThread>(), _app.Get<IReaderImages>(),
            _app.Get<ReadingTracker>(), NullLogger.Instance);
        await reader.LoadAsync(Ct);
        await TurnAsync(reader, 6);
        await reader.DisposeAsync();
        await WaitForAsync(() => library.ShowContinueReading);

        var item = Assert.Single(library.ContinueReading);
        Assert.Same(saga2, item.Card);
        Assert.True(saga2.IsReading);
        Assert.Equal(1, library.Filters.Single(f => f.Filter == LibraryFilter.Reading).Count);
        Assert.Equal(2, library.Filters.Single(f => f.Filter == LibraryFilter.Unread).Count);

        // A filter or a search hides the strip.
        library.Filters.Single(f => f.Filter == LibraryFilter.Reading).PickCommand.Execute(null);
        Assert.False(library.ShowContinueReading);
        Assert.Equal([saga2], library.VisibleCards);
        library.Filters[0].PickCommand.Execute(null);
        Assert.True(library.ShowContinueReading);

        // The strip opens the file where the book was left.
        item.OpenCommand.Execute(null);
        Assert.Equal(request.FilePath, _app.Reader.Opened[^1].FilePath);

        // Marked read by hand: leaves the strip.
        saga2.SelectCommand.Execute(null);
        var details = library.Selected!;
        await details.MarkReadCommand.ExecuteAsync(null);
        Assert.True(saga2.IsRead);
        Assert.Empty(library.ContinueReading);
        Assert.False(library.ShowContinueReading);
        Assert.Equal(1, library.Filters.Single(f => f.Filter == LibraryFilter.Read).Count);
        Assert.StartsWith(Strings.Reading_ReadOn.Split('{')[0], details.ReadingText);

        // Not read again: back to the start, nothing kept.
        await details.MarkUnreadCommand.ExecuteAsync(null);
        Assert.False(saga2.IsRead);
        Assert.False(saga2.IsReading);
        Assert.Equal(Strings.Reading_NotRead, details.ReadingText);

        // A book never opened can be marked read too.
        var paper = cards.Single(c => c.Book.Metadata.Series?.Name == "Paper Girls");
        paper.SelectCommand.Execute(null);
        await library.Selected!.MarkReadCommand.ExecuteAsync(null);
        Assert.True(paper.IsRead);
    }

    [Fact]
    public async Task Clearing_the_progress_takes_the_book_out_of_continue_reading()
    {
        await ImportAsync(SyntheticBooks.Cbz(Downloads("saga.cbz"), pages: 12, comicInfoXml: SyntheticBooks.ComicInfo(series: "Saga", number: "1")));
        var library = _app.Get<LibraryPageViewModel>();
        await library.LoadAsync(Ct);
        var details = library.Selected!;
        var reader = Reader(ReaderRequest.For(Session.Current!, details.Card.Book, details.Card.Book.MainFile!).FilePath, BookFormat.Cbz,
            details.Card.Id);
        await reader.LoadAsync(Ct);
        await TurnAsync(reader, 7);
        await reader.DisposeAsync();
        await WaitForAsync(() => library.ContinueReading.Count == 1);

        await details.ClearProgressCommand.ExecuteAsync(null);

        Assert.Empty(library.ContinueReading);
        Assert.False(details.Card.IsReading);
        var again = Reader(ReaderRequest.For(Session.Current!, details.Card.Book, details.Card.Book.MainFile!).FilePath, BookFormat.Cbz,
            details.Card.Id);
        await again.LoadAsync(Ct);
        Assert.Equal(Pages(1, 12), again.PageText);
        await again.DisposeAsync();
    }
}
