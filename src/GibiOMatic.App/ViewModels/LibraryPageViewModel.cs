using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
using GibiOMatic.Conversion;
using GibiOMatic.Core.Quality;
using GibiOMatic.Library;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.App.ViewModels;

public enum LibraryFilter
{
    All,
    NeedsReview,
    NoDate,
    NoSeries,
    UnknownAuthor,
    Complete,
}

public enum LibraryViewMode
{
    Series,
    Books,
}

public sealed partial class FilterChipViewModel(LibraryFilter filter, string label, Action<LibraryFilter> pick) : ViewModelBase
{
    public LibraryFilter Filter { get; } = filter;
    public string Label { get; } = label;

    [ObservableProperty]
    public partial int Count { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Pick() => pick(Filter);
}

/// <summary>
/// The library (SPEC §12): cover grid by series or by book, quality badges, filters, search,
/// summary cards and the detail panel of the selected book.
/// </summary>
public sealed partial class LibraryPageViewModel : PageViewModel
{
    /// <summary>Smallest card width; the grid fits as many columns as possible.</summary>
    public const double MinCardWidth = 150;
    public const double CardGap = 18;

    private readonly LibrarySession _session;
    private readonly ThumbnailCache _thumbnails;
    private readonly ICoverLoader _covers;
    private readonly IFilePicker _filePicker;
    private readonly ILogger<LibraryPageViewModel> _logger;
    private readonly Debouncer _reload;
    private List<BookCardViewModel> _books = [];
    private double _viewportWidth;

    public LibraryPageViewModel(LibrarySession session, ThumbnailCache thumbnails, ICoverLoader covers, IFilePicker filePicker,
        IUiThread ui, ILogger<LibraryPageViewModel> logger)
    {
        _session = session;
        _thumbnails = thumbnails;
        _covers = covers;
        _filePicker = filePicker;
        _logger = logger;
        _reload = new Debouncer(ui, TimeSpan.FromMilliseconds(400), () => LoadAsync());
        if (session.Queue is { } queue)
        {
            queue.BookImported += OnBookImported;
        }

        Filters =
        [
            new(LibraryFilter.All, Strings.Filter_All, PickFilter),
            new(LibraryFilter.NeedsReview, Strings.Filter_NeedsReview, PickFilter),
            new(LibraryFilter.NoDate, Strings.Filter_NoDate, PickFilter),
            new(LibraryFilter.NoSeries, Strings.Filter_NoSeries, PickFilter),
            new(LibraryFilter.UnknownAuthor, Strings.Filter_UnknownAuthor, PickFilter),
            new(LibraryFilter.Complete, Strings.Filter_Complete, PickFilter),
        ];
        Filters[0].IsSelected = true;
    }

    public override string Title => Strings.Nav_Library;

    public string LibraryPath => _session.Current?.RootPath ?? string.Empty;

    /// <summary>Asks the shell to show another page (Entrada after picking files to import).</summary>
    public event EventHandler<NavPage>? NavigationRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(HasBooks))]
    public partial int? BookCount { get; private set; }

    public bool IsEmpty => BookCount == 0;

    public bool HasBooks => BookCount > 0;

    public IReadOnlyList<FilterChipViewModel> Filters { get; }

    [ObservableProperty]
    public partial LibraryFilter Filter { get; private set; }

    [ObservableProperty]
    public partial string? SearchText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSeriesView), nameof(IsBooksView))]
    public partial LibraryViewMode ViewMode { get; set; } = LibraryViewMode.Series;

    public bool IsSeriesView => ViewMode == LibraryViewMode.Series;

    public bool IsBooksView => ViewMode == LibraryViewMode.Books;

    [RelayCommand]
    private void SetViewMode(LibraryViewMode mode) => ViewMode = mode;

    /// <summary>The series opened from the Séries view, whose books are listed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInSeries))]
    public partial SeriesCardViewModel? OpenSeries { get; private set; }

    public bool IsInSeries => OpenSeries is not null;

    /// <summary>Everything shown in the grid, after filter, search and grouping.</summary>
    public IReadOnlyList<ViewModelBase> VisibleCards { get; private set; } = [];

    public ObservableCollection<CardRow> Rows { get; } = [];

    [ObservableProperty]
    public partial int Columns { get; private set; } = 5;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoResults))]
    public partial bool HasResults { get; private set; }

    public bool HasNoResults => HasBooks && !HasResults;

    [ObservableProperty]
    public partial BookDetailsViewModel? Selected { get; private set; }

    // Summary cards
    [ObservableProperty]
    public partial int CompleteCount { get; private set; }

    [ObservableProperty]
    public partial string CompletePercent { get; private set; } = "0%";

    [ObservableProperty]
    public partial int NoDateCount { get; private set; }

    [ObservableProperty]
    public partial int NoSeriesCount { get; private set; }

    [ObservableProperty]
    public partial int NeedsReviewCount { get; private set; }

    public override Task OnNavigatedToAsync(CancellationToken ct = default) => LoadAsync(ct);

    /// <summary>Reads every book from the index and scores it.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_session.Current is not { } library || _session.Index is not { } index)
        {
            return;
        }

        try
        {
            var books = await index.GetBooksAsync(ct);
            var context = QualityContext.From(books.Select(b => b.Metadata));
            var selectedId = Selected?.Card.Id;
            _books = books
                .Select(b => new BookCardViewModel(b, QualityAnalyzer.Analyze(b.Metadata, context), library, _thumbnails, _covers, SelectBook))
                .OrderBy(b => b.Book.Metadata.Series?.Name ?? b.Title, NaturalStringComparer.Instance)
                .ThenBy(b => b.Book.Metadata.Series?.Number ?? string.Empty, NaturalStringComparer.Instance)
                .ThenBy(b => b.Title, NaturalStringComparer.Instance)
                .ToList();

            BookCount = _books.Count;
            UpdateSummary();
            ApplyView();
            var keep = selectedId is null ? null : _books.FirstOrDefault(b => b.Id == selectedId);
            SelectBook(keep ?? (VisibleCards.FirstOrDefault() switch
            {
                BookCardViewModel b => b,
                SeriesCardViewModel s => s.Cover,
                _ => null,
            }));
        }
        catch (Exception ex) when (LibrarySession.IsExpectedFailure(ex))
        {
            _logger.LogError(ex, "Could not read the index of library {LibraryId}", library.Id);
        }
    }

    private void UpdateSummary()
    {
        var total = _books.Count;
        CompleteCount = _books.Count(b => Matches(b, LibraryFilter.Complete));
        CompletePercent = total == 0 ? "0%" : ((double)CompleteCount / total).ToString("0%", CultureInfo.CurrentCulture);
        NoDateCount = _books.Count(b => Matches(b, LibraryFilter.NoDate));
        NoSeriesCount = _books.Count(b => Matches(b, LibraryFilter.NoSeries));
        NeedsReviewCount = _books.Count(b => Matches(b, LibraryFilter.NeedsReview));
        foreach (var chip in Filters)
        {
            chip.Count = _books.Count(b => Matches(b, chip.Filter));
        }

        var series = _books.Select(b => b.Book.Metadata.Series?.Name).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Subtitle = string.Format(CultureInfo.CurrentCulture, Strings.Library_Summary,
            total == 1 ? Strings.Library_SubtitleOne : string.Format(CultureInfo.CurrentCulture, Strings.Library_SubtitleMany, total),
            series, CompleteCount);
    }

    public static bool Matches(BookCardViewModel book, LibraryFilter filter) => filter switch
    {
        LibraryFilter.NeedsReview => book.Band == QualityBand.Bad,
        LibraryFilter.NoDate => book.Book.Metadata.Date is null,
        LibraryFilter.NoSeries => book.Book.Metadata.Series is null,
        LibraryFilter.UnknownAuthor => QualityAnalyzer.HasUnknownAuthor(book.Book.Metadata),
        LibraryFilter.Complete => book.Band == QualityBand.Ok,
        _ => true,
    };

    private void PickFilter(LibraryFilter filter) => Filter = filter;

    partial void OnFilterChanged(LibraryFilter value)
    {
        foreach (var chip in Filters)
        {
            chip.IsSelected = chip.Filter == value;
        }

        ApplyView();
    }

    partial void OnSearchTextChanged(string? value) => ApplyView();

    partial void OnViewModeChanged(LibraryViewMode value)
    {
        OpenSeries = null;
        ApplyView();
    }

    [RelayCommand]
    private void BackToSeries()
    {
        OpenSeries = null;
        ApplyView();
    }

    private void OpenSeriesCard(SeriesCardViewModel series)
    {
        OpenSeries = series;
        ApplyView();
        SelectBook(series.Books[0]);
    }

    private void ApplyView()
    {
        var search = string.IsNullOrWhiteSpace(SearchText) ? null : BookCardViewModel.Normalize(SearchText.Trim());
        var books = _books.Where(b => Matches(b, Filter) && (search is null || b.SearchText.Contains(search, StringComparison.Ordinal))).ToList();

        if (OpenSeries is { } open)
        {
            VisibleCards = books.Where(b => string.Equals(b.Book.Metadata.Series?.Name, open.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        else if (ViewMode == LibraryViewMode.Series)
        {
            // A series becomes one card; books without a series stay on their own, in title order.
            var cards = new List<ViewModelBase>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var book in books)
            {
                if (book.Book.Metadata.Series?.Name is not { } name)
                {
                    cards.Add(book);
                }
                else if (seen.Add(name))
                {
                    cards.Add(new SeriesCardViewModel(name, books.Where(b => string.Equals(b.Book.Metadata.Series?.Name, name, StringComparison.OrdinalIgnoreCase)).ToList(), OpenSeriesCard));
                }
            }

            VisibleCards = cards;
        }
        else
        {
            VisibleCards = books;
        }

        HasResults = VisibleCards.Count > 0;
        RebuildRows();
    }

    /// <summary>Called by the view when the grid width changes: fits as many columns as possible.</summary>
    public void SetViewportWidth(double width)
    {
        if (width <= 0 || Math.Abs(width - _viewportWidth) < 1)
        {
            return;
        }

        _viewportWidth = width;
        var columns = Math.Max(1, (int)((width + CardGap) / (MinCardWidth + CardGap)));
        if (columns != Columns)
        {
            Columns = columns;
            RebuildRows();
        }
    }

    private void RebuildRows()
    {
        Rows.Clear();
        for (var i = 0; i < VisibleCards.Count; i += Columns)
        {
            Rows.Add(new CardRow(VisibleCards.Skip(i).Take(Columns).ToList(), Columns));
        }
    }

    private void SelectBook(BookCardViewModel? card)
    {
        if (Selected?.Card is { } previous)
        {
            previous.IsSelected = false;
        }

        if (card is null || _session.Current is not { } library)
        {
            Selected = null;
            return;
        }

        card.IsSelected = true;
        Selected = new BookDetailsViewModel(card, library, _covers);
    }

    [RelayCommand]
    private async Task ImportAsync(CancellationToken ct)
    {
        var files = await _filePicker.PickBooksAsync(Strings.Picker_ImportTitle, ct);
        await AddToInboxAsync(files, ct);
    }

    /// <summary>Files dropped on the library go to the inbox, which is then shown.</summary>
    [RelayCommand]
    private Task DropFilesAsync(string[] paths, CancellationToken ct) => AddToInboxAsync(paths, ct);

    private async Task AddToInboxAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (paths.Count == 0 || _session.Queue is not { } queue)
        {
            return;
        }

        await queue.AddAsync(paths, ct);
        NavigationRequested?.Invoke(this, NavPage.Inbox);
    }

    private void OnBookImported(object? sender, string bookId) => _reload.Trigger();

    public override void Dispose()
    {
        if (_session.Queue is { } queue)
        {
            queue.BookImported -= OnBookImported;
        }

        _reload.Dispose();
    }
}
