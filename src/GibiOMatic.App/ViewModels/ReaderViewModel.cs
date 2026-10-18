using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
using GibiOMatic.Conversion.Reading;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Settings;
using GibiOMatic.Library.Reading;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace GibiOMatic.App.ViewModels;

/// <summary>A book file to open in the reader.</summary>
public sealed record ReaderRequest(string BookId, string Title, string FilePath, BookFormat Format)
{
    public static ReaderRequest For(Core.Libraries.LibraryInfo library, Library.Index.CatalogBook book, BookFile file) =>
        new(book.Metadata.Id, book.Metadata.Title, Path.Combine(library.RootPath, book.Folder, file.Name), file.Format);
}

/// <summary>Turns rendered pages into images the view can draw (Avalonia bitmaps in the app).</summary>
public interface IReaderImages
{
    /// <summary>Takes ownership of <paramref name="bitmap"/>. Safe to call off the UI thread.</summary>
    IImage Create(SKBitmap bitmap);
}

/// <summary>
/// Which pages are shown together. One page at a time, or two side by side with the cover alone;
/// a wide page (a spread scanned as one image) is always alone and the pairing continues after it.
/// </summary>
public static class ReaderSpreads
{
    public static IReadOnlyList<int[]> Compute(IReadOnlyList<PageSize> sizes, bool twoPages)
    {
        var spreads = new List<int[]>();
        for (var i = 0; i < sizes.Count; i++)
        {
            if (twoPages && i > 0 && !sizes[i].IsWide && i + 1 < sizes.Count && !sizes[i + 1].IsWide)
            {
                spreads.Add([i, i + 1]);
                i++;
            }
            else
            {
                spreads.Add([i]);
            }
        }

        return spreads;
    }
}

/// <summary>A page in the thumbnail strip; the image is rendered the first time the strip shows it.</summary>
public sealed partial class ThumbnailViewModel(int index, Action<ThumbnailViewModel> load) : ViewModelBase
{
    private bool _requested;
    private IImage? _image;

    public int Index { get; } = index;

    public string Label { get; } = (index + 1).ToString(CultureInfo.CurrentCulture);

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    public IImage? Image
    {
        get
        {
            if (!_requested)
            {
                _requested = true;
                load(this);
            }

            return _image;
        }
        internal set => SetProperty(ref _image, value);
    }

    /// <summary>Without asking for a render, unlike <see cref="Image"/>.</summary>
    internal bool HasImage => _image is not null;
}

/// <summary>An entry of a text book's table of contents.</summary>
public sealed partial class TocItemViewModel(string title, int chapter, string? fragment, int depth, Action<TocItemViewModel> open) : ViewModelBase
{
    public string Title { get; } = title;
    public int Chapter { get; } = chapter;
    public string? Fragment { get; } = fragment;
    public Avalonia.Thickness Indent { get; } = new(12 + (16 * Math.Min(depth, 4)), 0, 8, 0);

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    [RelayCommand]
    private void Open() => open(this);
}

/// <summary>
/// The reader (F3.5): to check which edition a book is, not to replace the Komga or the Kindle.
/// Comics and PDFs show one or two pages with fit modes, zoom and thumbnails; text EPUBs show their
/// chapters in a web view. The last page of each book is kept in the index.
/// </summary>
public sealed partial class ReaderViewModel : ViewModelBase, IAsyncDisposable
{
    private const int DefaultRenderHeight = 1600;
    private const int ThumbnailHeight = 150;
    private const int CacheSize = 10;

    /// <summary>
    /// Pages slower than this (huge scans in a PDF take seconds each, whatever the size asked for)
    /// get no extra renders for thumbnails, and only the next pages are rendered ahead.
    /// </summary>
    private const double SlowRenderMs = 700;
    private const int MinTextScale = 60;
    private const int MaxTextScale = 200;

    private readonly ReaderRequest _request;
    private readonly ISettingsService _settings;
    private readonly IUiThread _ui;
    private readonly IReaderImages _images;
    private readonly ReadingTracker _reading;
    private readonly HashSet<int> _seen = [];
    private bool _reachedEnd;
    private readonly ILogger _logger;
    private readonly Debouncer _savePosition;
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private readonly Dictionary<int, (IImage Image, int Height)> _cache = [];
    private readonly LinkedList<int> _recent = new();
    private readonly Stack<ThumbnailViewModel> _thumbnailQueue = new();

    private OpenedBook? _book;
    private EpubServer? _server;
    private PageSize[] _sizes = [];
    private IReadOnlyList<int[]> _spreads = [];
    private int _spreadIndex;
    private int _showVersion;
    private int _navigationVersion;
    private bool _thumbnailWorkerRunning;

    /// <summary>Work for the page on screen (cancelled when another page is asked for).</summary>
    private CancellationTokenSource _showCts = new();

    /// <summary>Page renders waiting or running: thumbnails wait for them.</summary>
    private int _pageRenders;

    /// <summary>Average time to render a page (0 until the first one).</summary>
    private double _renderMs;

    private bool SlowBook => _renderMs > SlowRenderMs;

    public ReaderViewModel(ReaderRequest request, ISettingsService settings, IUiThread ui, IReaderImages images,
        ReadingTracker reading, ILogger logger)
    {
        _request = request;
        _settings = settings;
        _ui = ui;
        _images = images;
        _reading = reading;
        _logger = logger;
        _savePosition = new Debouncer(ui, TimeSpan.FromSeconds(1), SavePositionAsync);
        var reader = settings.Current.Reader;
        Fit = reader.Fit;
        TwoPages = reader.TwoPages;
        ShowThumbnails = reader.ShowThumbnails;
        TextScale = Math.Clamp(reader.TextScale, MinTextScale, MaxTextScale);
        _initialized = true;
    }

    /// <summary>Preferences read in the constructor are not saved back.</summary>
    private readonly bool _initialized;

    public string Title => _request.Title;

    public string WindowTitle => string.Format(CultureInfo.CurrentCulture, Strings.Reader_WindowTitle, _request.Title);

    public string FileName => Path.GetFileName(_request.FilePath);

    public string BookId => _request.BookId;

    public string FilePath => _request.FilePath;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; } = true;

    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStrip))]
    public partial bool IsPaged { get; private set; }

    [ObservableProperty]
    public partial bool IsText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStrip), nameof(ShowToolbar))]
    public partial bool IsFullScreen { get; set; }

    /// <summary>The toolbar is hidden in full screen until the pointer reaches the top (set by the view).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowToolbar), nameof(ShowStrip))]
    public partial bool PointerAtEdge { get; set; }

    public bool ShowToolbar => !IsFullScreen || PointerAtEdge;

    public bool ShowStrip => IsPaged && ShowThumbnails && (!IsFullScreen || PointerAtEdge);

    // ---- Pages ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFitHeight), nameof(IsFitWidth), nameof(IsFitOriginal))]
    public partial ReaderFit Fit { get; set; }

    public bool IsFitHeight => Fit == ReaderFit.Height;
    public bool IsFitWidth => Fit == ReaderFit.Width;
    public bool IsFitOriginal => Fit == ReaderFit.Original;

    [ObservableProperty]
    public partial bool TwoPages { get; set; }

    [ObservableProperty]
    public partial bool RightToLeft { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStrip))]
    public partial bool ShowThumbnails { get; set; }

    /// <summary>The page asked for is still being rendered (a slow PDF can take seconds).</summary>
    [ObservableProperty]
    public partial bool IsPageLoading { get; private set; }

    public int PageCount => _sizes.Length;

    /// <summary>First page of the spread on screen (0-based, reading order).</summary>
    [ObservableProperty]
    public partial int CurrentPage { get; private set; }

    [ObservableProperty]
    public partial string? PageText { get; private set; }

    /// <summary>Pages in screen order: the left one, and the right one in two-page mode.</summary>
    [ObservableProperty]
    public partial IImage? LeftImage { get; private set; }

    [ObservableProperty]
    public partial IImage? RightImage { get; private set; }

    [ObservableProperty]
    public partial PageSize LeftSize { get; private set; }

    [ObservableProperty]
    public partial PageSize RightSize { get; private set; }

    /// <summary>Changes on every page turn, so the view resets zoom and scrolling.</summary>
    [ObservableProperty]
    public partial int PageTurn { get; private set; }

    /// <summary>Height in device pixels the pages take on screen (set by the view); pages are rendered at that size.</summary>
    [ObservableProperty]
    public partial int NeededHeight { get; set; }

    public ObservableCollection<ThumbnailViewModel> Thumbnails { get; } = [];

    // ---- Text ----

    public ObservableCollection<TocItemViewModel> Toc { get; } = [];

    [ObservableProperty]
    public partial int Chapter { get; private set; }

    [ObservableProperty]
    public partial string? ChapterText { get; private set; }

    [ObservableProperty]
    public partial int TextScale { get; private set; }

    public string TextScaleText => string.Format(CultureInfo.CurrentCulture, "{0}%", TextScale);

    /// <summary>The web view must show this address (the view calls <see cref="OnNavigated"/> back).</summary>
    public event EventHandler<Uri>? NavigationRequested;

    /// <summary>Esc outside full screen closes the reader.</summary>
    public event EventHandler? CloseRequested;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var state = _reading.Store is { } store ? await store.GetAsync(_request.BookId, ct) : null;
            var saved = state is not null && state.File.Equals(FileName, StringComparison.OrdinalIgnoreCase) ? state : null;
            _book = await Task.Run(() => BookReader.Open(_request.FilePath, _request.Format), ct);
            if (_book.Pages is { } pages)
            {
                RightToLeft = saved?.RightToLeft ?? false;
                _sizes = Enumerable.Repeat(PageSize.Portrait, pages.PageCount).ToArray();
                _sizes[0] = await Task.Run(() => pages.GetSize(0), ct);
                for (var i = 0; i < pages.PageCount; i++)
                {
                    Thumbnails.Add(new ThumbnailViewModel(i, QueueThumbnail));
                }

                _spreads = ReaderSpreads.Compute(_sizes, TwoPages);
                _spreadIndex = SpreadOf(Math.Clamp(saved?.Position ?? 0, 0, PageCount - 1));
                IsPaged = true;
                OnPropertyChanged(nameof(PageCount));
                await ShowSpreadAsync();
                _ = Task.Run(() => ReadSizesAsync(pages, _closing.Token));
            }
            else if (_book.Text is { } text)
            {
                _server = new EpubServer(text, ReaderCss);
                var toc = text.Toc.Count > 0
                    ? text.Toc.Select(t => (t.Title, t.Chapter, t.Fragment, t.Depth))
                    : text.Chapters.Select((c, i) => (c.Title ?? string.Format(CultureInfo.CurrentCulture, Strings.Reader_ChapterN, i + 1), i, (string?)null, 0));
                foreach (var (title, chapter, fragment, depth) in toc)
                {
                    Toc.Add(new TocItemViewModel(title, chapter, fragment, depth, item => GoToChapter(item.Chapter, item.Fragment)));
                }

                IsText = true;
                GoToChapter(Math.Clamp(saved?.Position ?? 0, 0, text.Chapters.Count - 1), null);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Any file the reader cannot read (damaged, password, unknown archive) ends here, not in a crash.
            _logger.LogWarning(ex, "Reader could not open {Path}", _request.FilePath);
            ErrorText = string.Format(CultureInfo.CurrentCulture, Strings.Reader_CannotOpen, ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ---- Page navigation ----

    [RelayCommand]
    private void Next() => GoToSpread(_spreadIndex + 1);

    [RelayCommand]
    private void Previous() => GoToSpread(_spreadIndex - 1);

    /// <summary>Left arrow / left side of the page: back, or forward when reading right to left.</summary>
    [RelayCommand]
    private void GoLeft()
    {
        if (IsText)
        {
            GoToChapter(Chapter + (RightToLeft ? 1 : -1), null);
        }
        else
        {
            GoToSpread(_spreadIndex + (RightToLeft ? 1 : -1));
        }
    }

    [RelayCommand]
    private void GoRight()
    {
        if (IsText)
        {
            GoToChapter(Chapter + (RightToLeft ? -1 : 1), null);
        }
        else
        {
            GoToSpread(_spreadIndex + (RightToLeft ? -1 : 1));
        }
    }

    [RelayCommand]
    private void First()
    {
        if (IsText)
        {
            GoToChapter(0, null);
        }
        else
        {
            GoToSpread(0);
        }
    }

    [RelayCommand]
    private void Last()
    {
        if (IsText && _book?.Text is { } text)
        {
            GoToChapter(text.Chapters.Count - 1, null);
        }
        else
        {
            GoToSpread(_spreads.Count - 1);
        }
    }

    [RelayCommand]
    private void GoToPage(int page) => GoToSpread(SpreadOf(page));

    [RelayCommand]
    private void SetFit(ReaderFit fit) => Fit = fit;

    [RelayCommand]
    private void ToggleTwoPages() => TwoPages = !TwoPages;

    [RelayCommand]
    private void ToggleRightToLeft() => RightToLeft = !RightToLeft;

    [RelayCommand]
    private void ToggleThumbnails() => ShowThumbnails = !ShowThumbnails;

    [RelayCommand]
    private void ToggleFullScreen() => IsFullScreen = !IsFullScreen;

    [RelayCommand]
    private void Escape()
    {
        if (IsFullScreen)
        {
            IsFullScreen = false;
        }
        else
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    partial void OnFitChanged(ReaderFit value) => SaveSettings();

    partial void OnShowThumbnailsChanged(bool value) => SaveSettings();

    partial void OnTwoPagesChanged(bool value)
    {
        SaveSettings();
        if (IsPaged)
        {
            var page = CurrentPage;
            _spreads = ReaderSpreads.Compute(_sizes, value);
            _spreadIndex = SpreadOf(page);
            _ = ShowSpreadAsync();
        }
    }

    partial void OnRightToLeftChanged(bool value)
    {
        if (IsPaged)
        {
            _ = ShowSpreadAsync();
        }

        _savePosition.Trigger();
    }

    partial void OnNeededHeightChanged(int value)
    {
        // Zooming in or a bigger window: render sharper pages when the current ones are too small.
        if (IsPaged && CurrentSpread.Any(p => !_cache.TryGetValue(p, out var c) || c.Height < TargetHeight(p) * 0.9))
        {
            _ = ShowSpreadAsync();
        }
    }

    private int[] CurrentSpread => _spreads.Count == 0 ? [] : _spreads[_spreadIndex];

    private int SpreadOf(int page)
    {
        for (var i = 0; i < _spreads.Count; i++)
        {
            if (_spreads[i].Contains(page))
            {
                return i;
            }
        }

        return 0;
    }

    private void GoToSpread(int index)
    {
        if (!IsPaged || index < 0 || index >= _spreads.Count || index == _spreadIndex)
        {
            return;
        }

        _spreadIndex = index;
        _ = ShowSpreadAsync();
        _savePosition.Trigger();
    }

    private int TargetHeight(int page)
    {
        var needed = NeededHeight > 0 ? NeededHeight : DefaultRenderHeight;
        // An image is never rendered above its own size (a PDF page can be, for zooming in).
        return _request.Format == BookFormat.Pdf
            ? needed
            : Math.Min(needed, _sizes[page].Height);
    }

    private async Task ShowSpreadAsync()
    {
        if (_book?.Pages is not { } pages || _spreads.Count == 0)
        {
            return;
        }

        var version = ++_showVersion;
        // Synchronously: the counter and the pages asked for change before anything else runs.
#pragma warning disable CA1849
        _showCts.Cancel();
#pragma warning restore CA1849
        _showCts = new CancellationTokenSource();
        var ct = _showCts.Token;
        var spread = CurrentSpread;
        CurrentPage = spread[0];
        MarkSeen(spread);
        PageText = spread.Length == 2
            ? string.Format(CultureInfo.CurrentCulture, Strings.Reader_PagesOf, spread[0] + 1, spread[1] + 1, PageCount)
            : string.Format(CultureInfo.CurrentCulture, Strings.Reader_PageOf, spread[0] + 1, PageCount);
        foreach (var thumbnail in Thumbnails)
        {
            thumbnail.IsCurrent = spread.Contains(thumbnail.Index);
        }

        var screen = RightToLeft ? spread.Reverse().ToArray() : spread;
        var turned = LeftImage is null || CurrentPageChanged(screen);
        _shownPages = screen;
        IsPageLoading = !screen.All(IsCached);
        try
        {
            var images = new IImage[screen.Length];
            for (var i = 0; i < screen.Length; i++)
            {
                images[i] = await GetPageAsync(pages, screen[i], ct);
            }

            if (version != _showVersion)
            {
                return;
            }

            IsPageLoading = false;

            LeftSize = _sizes[screen[0]];
            RightSize = screen.Length > 1 ? _sizes[screen[1]] : default;
            LeftImage = images[0];
            RightImage = images.Length > 1 ? images[1] : null;
            if (turned)
            {
                PageTurn++;
            }

            // The next pages (and the previous ones) are ready before they are asked for.
            foreach (var page in Neighbours())
            {
                if (version != _showVersion)
                {
                    return;
                }

                await GetPageAsync(pages, page, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Another page was asked for (or the reader closed): that show takes over.
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            _logger.LogWarning(ex, "Reader could not show page {Page} of {Path}", CurrentPage + 1, _request.FilePath);
            if (version == _showVersion)
            {
                IsPageLoading = false;
                ErrorText = string.Format(CultureInfo.CurrentCulture, Strings.Reader_PageFailed, CurrentPage + 1, ex.Message);
            }
        }
    }

    private int[] _shownPages = [];

    private bool CurrentPageChanged(int[] screen) => !screen.SequenceEqual(_shownPages);

    /// <summary>The next pages first; a slow book renders two spreads ahead and none behind.</summary>
    private IEnumerable<int> Neighbours()
    {
        for (var ahead = 1; ahead <= (SlowBook ? 2 : 1) && _spreadIndex + ahead < _spreads.Count; ahead++)
        {
            foreach (var page in _spreads[_spreadIndex + ahead])
            {
                yield return page;
            }
        }

        if (_spreadIndex > 0 && !SlowBook)
        {
            foreach (var page in _spreads[_spreadIndex - 1])
            {
                yield return page;
            }
        }
    }

    private bool IsCached(int page) => _cache.TryGetValue(page, out var cached) && cached.Height >= TargetHeight(page) * 0.9;

    private async Task<IImage> GetPageAsync(IPageSource pages, int page, CancellationToken ct)
    {
        var height = TargetHeight(page);
        if (_cache.TryGetValue(page, out var cached) && cached.Height >= height * 0.9)
        {
            Touch(page);
            return cached.Image;
        }

        var thumbnail = page < Thumbnails.Count && !Thumbnails[page].HasImage ? Thumbnails[page] : null;
        IImage image;
        IImage? small = null;
        Interlocked.Increment(ref _pageRenders);
        try
        {
            await _renderGate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();

                // Rendered ahead while this waited (the next page, usually).
                if (_cache.TryGetValue(page, out cached) && cached.Height >= height * 0.9)
                {
                    Touch(page);
                    return cached.Image;
                }

                var started = Stopwatch.GetTimestamp();
                (image, small) = await Task.Run(() =>
                {
                    var bitmap = pages.Render(page, int.MaxValue / 4, height, ct);
                    // The thumbnail comes from the page just rendered, at no extra cost.
                    var thumb = thumbnail is null ? null : _images.Create(Downscale(bitmap, ThumbnailHeight));
                    return (_images.Create(bitmap), thumb);
                }, CancellationToken.None);
                var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                _renderMs = _renderMs == 0 ? ms : (_renderMs * 0.7) + (ms * 0.3);
            }
            finally
            {
                _renderGate.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pageRenders);
        }

        if (small is not null && thumbnail is not null)
        {
            thumbnail.Image = small;
        }

        if (_cache.Remove(page, out var old) && !IsShown(old.Image))
        {
            (old.Image as IDisposable)?.Dispose();
        }

        _cache[page] = (image, height);
        Touch(page);
        Evict();
        return image;
    }

    private static SKBitmap Downscale(SKBitmap bitmap, int height)
    {
        var width = Math.Max(1, (int)Math.Round(bitmap.Width * (double)height / Math.Max(1, bitmap.Height)));
        return bitmap.Resize(new SKImageInfo(width, height, bitmap.ColorType, bitmap.AlphaType), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
               ?? throw new InvalidOperationException("The thumbnail could not be made.");
    }

    private bool IsShown(IImage image) => ReferenceEquals(image, LeftImage) || ReferenceEquals(image, RightImage);

    private void Touch(int page)
    {
        _recent.Remove(page);
        _recent.AddFirst(page);
    }

    private void Evict()
    {
        while (_recent.Count > CacheSize)
        {
            var page = _recent.Last!.Value;
            _recent.RemoveLast();
            if (_cache.Remove(page, out var entry) && !IsShown(entry.Image))
            {
                (entry.Image as IDisposable)?.Dispose();
            }
        }
    }

    /// <summary>Real page sizes (wide pages change the two-page pairing), read in the background.</summary>
    private async Task ReadSizesAsync(IPageSource pages, CancellationToken ct)
    {
        var sizes = new PageSize[pages.PageCount];
        try
        {
            for (var i = 0; i < sizes.Length; i++)
            {
                // The page on screen comes first; the gate keeps the file open while it is read
                // (closing the reader waits for it).
                while (Volatile.Read(ref _pageRenders) > 0)
                {
                    await Task.Delay(50, ct);
                }

                await _renderGate.WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    sizes[i] = pages.GetSize(i);
                }
                finally
                {
                    _renderGate.Release();
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reader could not read the page sizes of {Path}", _request.FilePath);
            return;
        }

        _ui.Post(() =>
        {
            var page = CurrentPage;
            _sizes = sizes;
            _spreads = ReaderSpreads.Compute(_sizes, TwoPages);
            _spreadIndex = SpreadOf(page);
            SizesReady?.Invoke(this, EventArgs.Empty);
            _ = ShowSpreadAsync();
        });
    }

    /// <summary>For tests: the real page sizes were read.</summary>
    internal event EventHandler? SizesReady;

    private void QueueThumbnail(ThumbnailViewModel thumbnail)
    {
        lock (_thumbnailQueue)
        {
            // The most recently shown thumbnails first: the ones on screen now.
            _thumbnailQueue.Push(thumbnail);
            if (_thumbnailWorkerRunning)
            {
                return;
            }

            _thumbnailWorkerRunning = true;
        }

        _ = Task.Run(RenderThumbnailsAsync);
    }

    private async Task RenderThumbnailsAsync()
    {
        while (true)
        {
            ThumbnailViewModel next;
            lock (_thumbnailQueue)
            {
                if (_thumbnailQueue.Count == 0 || _closing.IsCancellationRequested || _book?.Pages is null)
                {
                    _thumbnailWorkerRunning = false;
                    return;
                }

                next = _thumbnailQueue.Pop();
            }

            try
            {
                // The page on screen comes first.
                while (Volatile.Read(ref _pageRenders) > 0)
                {
                    await Task.Delay(50, _closing.Token);
                }

                if (next.HasImage || SlowBook)
                {
                    continue;
                }

                await _renderGate.WaitAsync(_closing.Token);
                IImage image;
                try
                {
                    _closing.Token.ThrowIfCancellationRequested();
                    if (_book?.Pages is not { } pages)
                    {
                        throw new OperationCanceledException();
                    }

                    image = _images.Create(pages.Render(next.Index, ThumbnailHeight, ThumbnailHeight, _closing.Token));
                }
                finally
                {
                    _renderGate.Release();
                }

                _ui.Post(() => next.Image = image);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                lock (_thumbnailQueue)
                {
                    _thumbnailWorkerRunning = false;
                }

                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Thumbnail of page {Page} failed", next.Index + 1);
            }
        }
    }

    // ---- Text books ----

    [RelayCommand]
    private void NextChapter() => GoToChapter(Chapter + 1, null);

    [RelayCommand]
    private void PreviousChapter() => GoToChapter(Chapter - 1, null);

    [RelayCommand]
    private void TextBigger() => ChangeTextScale(+10);

    [RelayCommand]
    private void TextSmaller() => ChangeTextScale(-10);

    private void ChangeTextScale(int delta)
    {
        var scale = Math.Clamp(TextScale + delta, MinTextScale, MaxTextScale);
        if (scale == TextScale)
        {
            return;
        }

        TextScale = scale;
        OnPropertyChanged(nameof(TextScaleText));
        SaveSettings();
        // The style sheet is not cached: showing the chapter again applies the new size.
        if (_server is not null)
        {
            NavigationRequested?.Invoke(this, WithVersion(_server.ChapterUri(Chapter)));
        }
    }

    private Uri WithVersion(Uri uri) => new UriBuilder(uri) { Query = "v=" + (++_navigationVersion).ToString(CultureInfo.InvariantCulture) }.Uri;

    private void GoToChapter(int chapter, string? fragment)
    {
        if (_server is null || _book?.Text is not { } text || chapter < 0 || chapter >= text.Chapters.Count)
        {
            return;
        }

        ShowChapter(chapter);
        NavigationRequested?.Invoke(this, _server.ChapterUri(chapter, fragment));
    }

    private void ShowChapter(int chapter)
    {
        if (_book?.Text is not { } text)
        {
            return;
        }

        var changed = chapter != Chapter || ChapterText is null;
        Chapter = chapter;
        MarkSeen([chapter]);
        ChapterText = string.Format(CultureInfo.CurrentCulture, Strings.Reader_ChapterOf, chapter + 1, text.Chapters.Count);
        foreach (var item in Toc)
        {
            item.IsCurrent = false;
        }

        if (Toc.LastOrDefault(t => t.Chapter == chapter) is { } current)
        {
            current.IsCurrent = true;
        }

        if (changed)
        {
            _savePosition.Trigger();
        }
    }

    /// <summary>Only the book's own pages may be shown: links elsewhere do nothing.</summary>
    public bool AllowNavigation(Uri uri) => _server?.BookPathOf(uri) is not null;

    /// <summary>The web view shows <paramref name="uri"/> (a link inside the book was followed).</summary>
    public void OnNavigated(Uri uri)
    {
        if (_server?.BookPathOf(uri) is { } path && _book?.Text?.ChapterOf(path) is >= 0 and var chapter)
        {
            ShowChapter(chapter);
        }
    }

    /// <summary>Text size and colors of text books: the app's dark theme over the book's own style.</summary>
    private string ReaderCss() => string.Create(CultureInfo.InvariantCulture, $$"""
        html { background: #121317 !important; }
        body { color: #ECEDEF !important; background: transparent !important; font-size: {{TextScale}}% !important;
               max-width: 44em; margin: 0 auto !important; padding: 2.5em 2em 4em !important; line-height: 1.6; }
        body *:not(img):not(svg):not(svg *) { color: inherit !important; background-color: transparent !important; border-color: #2E313A !important; }
        a, a * { color: #F5C542 !important; }
        img, svg { max-width: 100% !important; height: auto !important; }
        ::selection { background: #F5C542; color: #17181D; }
        """);

    // ---- Settings and position ----

    private void SaveSettings()
    {
        if (!_initialized)
        {
            return;
        }

        var reader = new ReaderSettings { Fit = Fit, TwoPages = TwoPages, ShowThumbnails = ShowThumbnails, TextScale = TextScale };
        if (_settings.Current.Reader == reader)
        {
            return;
        }

        var saving = _settings.SaveAsync(_settings.Current with { Reader = reader });
        _ = saving.ContinueWith(t => _logger.LogWarning(t.Exception, "Reader settings could not be saved"), TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// Pages (or chapters) shown in this session decide whether the book was really read. Reopening a
    /// book where it was left, on its last page, does not count as reaching the end again.
    /// </summary>
    private void MarkSeen(IEnumerable<int> units)
    {
        var last = IsText ? (_book?.Text?.Chapters.Count ?? 0) - 1 : PageCount - 1;
        var reopened = _seen.Count == 0;
        foreach (var unit in units)
        {
            _seen.Add(unit);
            _reachedEnd |= unit == last && !reopened;
        }
    }

    private async Task SavePositionAsync()
    {
        if (_reading.Store is not { } store || (!IsPaged && !IsText))
        {
            return;
        }

        try
        {
            var total = IsText ? _book?.Text?.Chapters.Count ?? 0 : PageCount;
            // Short books count once every page was shown.
            var started = _seen.Count > (IsText ? ReadingStateStore.StartedAfterChapters : ReadingStateStore.StartedAfterPages)
                          || (total > 1 && _seen.Count >= total);
            await store.SaveAsync(_request.BookId, new ReadingState(FileName, IsText ? Chapter : CurrentPage, RightToLeft),
                new ReadingProgress(total, started, _reachedEnd));
            _reading.NotifyChanged(_request.BookId);
        }
        catch (Exception ex) when (ex is IOException or System.Data.Common.DbException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "Reading position could not be saved");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _savePosition.Dispose();
        await SavePositionAsync();
        await _closing.CancelAsync();
        await _showCts.CancelAsync();
        // Wait for a page being rendered before closing the file under it.
        await _renderGate.WaitAsync();
        try
        {
            _server?.Dispose();
            _book?.Dispose();
            _book = null;
        }
        finally
        {
            _renderGate.Release();
        }

        foreach (var (image, _) in _cache.Values)
        {
            (image as IDisposable)?.Dispose();
        }

        _cache.Clear();
        _closing.Dispose();
    }
}
