using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
using GibiOMatic.Conversion;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Quality;
using GibiOMatic.Core.Settings;
using GibiOMatic.Destinations.Kindle;
using GibiOMatic.Destinations.Sync;
using GibiOMatic.Library;
using GibiOMatic.Library.Reading;
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
    AutoApproved,
    Reading,
    Read,
    Unread,
}

public enum LibraryViewMode
{
    Series,
    Books,
    Universes,
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
public sealed partial class LibraryPageViewModel : PageViewModel, IReadingActions
{
    /// <summary>Books shown in "Continuar lendo", the most recent first.</summary>
    public const int ContinueReadingCount = 8;

    /// <summary>Smallest card width; the grid fits as many columns as possible.</summary>
    public const double MinCardWidth = 150;
    public const double CardGap = 18;

    private readonly LibrarySession _session;
    private readonly ThumbnailCache _thumbnails;
    private readonly ICoverLoader _covers;
    private readonly IFilePicker _filePicker;
    private readonly IReaderLauncher _reader;
    private readonly ReadingTracker _reading;
    private readonly Debouncer _reloadReading;
    private readonly ISettingsService _settings;
    private readonly ILogger<LibraryPageViewModel> _logger;
    private readonly Debouncer _reload;
    private List<BookCardViewModel> _books = [];
    private double _viewportWidth;

    public LibraryPageViewModel(LibrarySession session, ThumbnailCache thumbnails, ICoverLoader covers, IFilePicker filePicker,
        ISettingsService settings, IUiThread ui, IReaderLauncher reader, ReadingTracker reading, ILogger<LibraryPageViewModel> logger)
    {
        _reader = reader;
        _reading = reading;
        _reloadReading = new Debouncer(ui, TimeSpan.FromMilliseconds(300), () => RefreshReadingAsync());
        reading.Changed += OnReadingChanged;
        _session = session;
        _thumbnails = thumbnails;
        _covers = covers;
        _filePicker = filePicker;
        _settings = settings;
        _logger = logger;
        _reload = new Debouncer(ui, TimeSpan.FromMilliseconds(400), () => LoadAsync());
        Status = new IdentificationStatusViewModel(session, ui);
        if (session.Queue is { } queue)
        {
            queue.BookImported += OnBookImported;
        }

        if (session.Identification is { } identification)
        {
            identification.BookUpdated += OnBookImported;
        }

        Filters =
        [
            new(LibraryFilter.All, Strings.Filter_All, PickFilter),
            new(LibraryFilter.NeedsReview, Strings.Filter_NeedsReview, PickFilter),
            new(LibraryFilter.NoDate, Strings.Filter_NoDate, PickFilter),
            new(LibraryFilter.NoSeries, Strings.Filter_NoSeries, PickFilter),
            new(LibraryFilter.UnknownAuthor, Strings.Filter_UnknownAuthor, PickFilter),
            new(LibraryFilter.Complete, Strings.Filter_Complete, PickFilter),
            new(LibraryFilter.AutoApproved, Strings.Filter_AutoApproved, PickFilter),
            new(LibraryFilter.Reading, Strings.Filter_Reading, PickFilter),
            new(LibraryFilter.Read, Strings.Filter_Read, PickFilter),
            new(LibraryFilter.Unread, Strings.Filter_Unread, PickFilter),
        ];
        Filters[0].IsSelected = true;
    }

    public override string Title => Strings.Nav_Library;

    public string LibraryPath => _session.Current?.RootPath ?? string.Empty;

    public IdentificationStatusViewModel Status { get; }

    /// <summary>Selection mode: clicking a card checks it instead of showing it.</summary>
    [ObservableProperty]
    public partial bool IsSelecting { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChecked))]
    [NotifyCanExecuteChangedFor(nameof(EditCheckedCommand))]
    public partial int CheckedCount { get; private set; }

    public bool HasChecked => CheckedCount > 0;

    [ObservableProperty]
    public partial string? CheckedText { get; private set; }

    /// <summary>"Buscar metadados" for the whole library waits for this confirmation.</summary>
    [ObservableProperty]
    public partial string? FetchConfirmation { get; private set; }

    private IReadOnlyList<string> _fetchIds = [];

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
    [NotifyPropertyChangedFor(nameof(IsSeriesView), nameof(IsBooksView), nameof(IsUniversesView))]
    public partial LibraryViewMode ViewMode { get; set; } = LibraryViewMode.Series;

    public bool IsSeriesView => ViewMode == LibraryViewMode.Series;

    public bool IsBooksView => ViewMode == LibraryViewMode.Books;

    public bool IsUniversesView => ViewMode == LibraryViewMode.Universes;

    [RelayCommand]
    private void SetViewMode(LibraryViewMode mode) => ViewMode = mode;

    /// <summary>The series opened from the Séries (or a universe's) view, whose books are listed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInSeries), nameof(IsDrilledIn), nameof(DrillTitle), nameof(BackText))]
    public partial SeriesCardViewModel? OpenSeries { get; private set; }

    public bool IsInSeries => OpenSeries is not null;

    /// <summary>The universe opened from the Universos view, whose series are listed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDrilledIn), nameof(DrillTitle), nameof(BackText))]
    public partial UniverseCardViewModel? OpenUniverse { get; private set; }

    /// <summary>Inside a series or a universe: the back button and the heading show.</summary>
    public bool IsDrilledIn => OpenSeries is not null || OpenUniverse is not null;

    public string? DrillTitle => OpenSeries?.Name ?? OpenUniverse?.Name;

    public string BackText => OpenSeries is null && OpenUniverse is not null ? Strings.Library_BackToUniverses
        : OpenUniverse is not null ? OpenUniverse.Name
        : Strings.Library_BackToSeries;

    /// <summary>The Universos view with no universe at all yet: how to make one.</summary>
    [ObservableProperty]
    public partial bool ShowNoUniverses { get; private set; }

    /// <summary>Everything shown in the grid, after filter, search and grouping.</summary>
    public IReadOnlyList<ViewModelBase> VisibleCards { get; private set; } = [];

    public ObservableCollection<CardRow> Rows { get; } = [];

    /// <summary>The last books being read (started, not finished).</summary>
    public ObservableCollection<ContinueReadingViewModel> ContinueReading { get; } = [];

    /// <summary>The strip shows over the whole library only, not over a search, a filter or a series.</summary>
    [ObservableProperty]
    public partial bool ShowContinueReading { get; private set; }

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

    public override async Task OnNavigatedToAsync(CancellationToken ct = default)
    {
        await Status.RefreshAsync();
        await LoadAsync(ct);
    }

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
            var checkedIds = _books.Where(b => b.IsChecked).Select(b => b.Id).ToHashSet();
            _books = books
                .Select(b => new BookCardViewModel(b, QualityAnalyzer.Analyze(b.Metadata, context), library, _thumbnails, _covers, OnCardClicked, OpenCard))
                .OrderBy(b => b.Book.Metadata.Series?.Name ?? b.Title, NaturalStringComparer.Instance)
                .ThenBy(b => b.Book.Metadata.Series?.Number ?? string.Empty, NaturalStringComparer.Instance)
                .ThenBy(b => b.Title, NaturalStringComparer.Instance)
                .ToList();

            BookCount = _books.Count;
            await ApplyReadingAsync(ct);
            if (IsSelecting)
            {
                foreach (var book in _books.Where(b => checkedIds.Contains(b.Id)))
                {
                    book.IsChecked = true;
                }

                UpdateChecked();
            }

            UpdateSummary();
            ApplyView();
            var keep = selectedId is null ? null : _books.FirstOrDefault(b => b.Id == selectedId);
            SelectBook(keep ?? (VisibleCards.FirstOrDefault() switch
            {
                BookCardViewModel b => b,
                SeriesCardViewModel s => s.Cover,
                UniverseCardViewModel u => u.Cover,
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
        LibraryFilter.AutoApproved => book.Book.Metadata.Review.MatchedBy == MatchedBy.Auto,
        LibraryFilter.Reading => book.IsReading,
        LibraryFilter.Read => book.IsRead,
        LibraryFilter.Unread => !book.IsReading && !book.IsRead,
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
        OpenUniverse = null;
        ApplyView();
    }

    /// <summary>Out of the series (back to its universe, if opened from one), or out of the universe.</summary>
    [RelayCommand]
    private void BackToSeries()
    {
        if (OpenSeries is not null)
        {
            OpenSeries = null;
        }
        else
        {
            OpenUniverse = null;
        }

        ApplyView();
    }

    private void OpenUniverseCard(UniverseCardViewModel universe)
    {
        OpenUniverse = universe;
        ApplyView();
        SelectBook(universe.Books[0]);
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

        if (OpenUniverse is { } universe)
        {
            books = books.Where(b => Universe.Contains(b.Book.Metadata, universe.Name)).ToList();
        }

        var noUniverses = false;
        if (OpenSeries is { } open)
        {
            VisibleCards = books.Where(b => string.Equals(b.Book.Metadata.Series?.Name, open.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        else if (ViewMode == LibraryViewMode.Universes && OpenUniverse is null)
        {
            // One card per universe; a book in two universes is in both.
            VisibleCards = Universe.Distinct(books.SelectMany(b => b.Book.Metadata.Universes))
                .Order(StringComparer.CurrentCultureIgnoreCase)
                .Select(name => new UniverseCardViewModel(name, books.Where(b => Universe.Contains(b.Book.Metadata, name)).ToList(), OpenUniverseCard))
                .ToList<ViewModelBase>();
            noUniverses = VisibleCards.Count == 0 && !_books.Any(b => b.Book.Metadata.Universes.Count > 0);
        }
        else if (ViewMode is LibraryViewMode.Series or LibraryViewMode.Universes)
        {
            VisibleCards = GroupBySeries(books);
        }
        else
        {
            VisibleCards = books;
        }

        ShowNoUniverses = noUniverses;
        HasResults = VisibleCards.Count > 0;
        UpdateContinueReading();
        RebuildRows();
    }

    /// <summary>A series becomes one card; books without a series stay on their own, in title order.</summary>
    private List<ViewModelBase> GroupBySeries(List<BookCardViewModel> books)
    {
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
                // A series with a single book is just that book (no stack, no "1 livro").
                var inSeries = books.Where(b => string.Equals(b.Book.Metadata.Series?.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                cards.Add(inSeries.Count == 1 ? inSeries[0] : new SeriesCardViewModel(name, inSeries, OpenSeriesCard));
            }
        }

        return cards;
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

    private void OnCardClicked(BookCardViewModel card)
    {
        if (IsSelecting)
        {
            card.IsChecked = !card.IsChecked;
            UpdateChecked();
        }
        else
        {
            SelectBook(card);
        }
    }

    private void UpdateChecked()
    {
        CheckedCount = _books.Count(b => b.IsChecked);
        CheckedText = string.Format(CultureInfo.CurrentCulture, Strings.Library_CheckedCount, CheckedCount);
    }

    [RelayCommand]
    private void ToggleSelecting()
    {
        IsSelecting = !IsSelecting;
        LoadChosenDestinations();
        if (!IsSelecting)
        {
            foreach (var book in _books)
            {
                book.IsChecked = false;
            }
        }

        UpdateChecked();
    }

    /// <summary>Checks every book shown (after filter and search).</summary>
    [RelayCommand]
    private void CheckAllVisible()
    {
        foreach (var card in VisibleCards)
        {
            foreach (var book in card switch { BookCardViewModel b => [b], SeriesCardViewModel s => s.Books, UniverseCardViewModel u => u.Books, _ => [] })
            {
                book.IsChecked = true;
            }
        }

        UpdateChecked();
    }

    /// <summary>"Livros que eu escolher" destinations, offered in the selection bar.</summary>
    public ObservableCollection<DestinationChoiceViewModel> ChosenDestinations { get; } = [];

    private void LoadChosenDestinations()
    {
        ChosenDestinations.Clear();
        foreach (var destination in _settings.Current.Destinations.Where(d => d.Which == DestinationScope.ChosenBooks))
        {
            ChosenDestinations.Add(new DestinationChoiceViewModel(destination.Id, destination.Name, SetChosenAsync));
        }
    }

    /// <summary>Adds the checked books to (or removes them from) a destination's list.</summary>
    private async Task SetChosenAsync(string destinationId, bool include)
    {
        if (_settings.Current.Destinations.FirstOrDefault(d => d.Id == destinationId) is not { } destination)
        {
            return;
        }

        var ids = _books.Where(b => b.IsChecked).Select(b => b.Id).ToList();
        var chosen = include ? destination.ChosenBookIds.Union(ids).ToList() : destination.ChosenBookIds.Except(ids).ToList();
        var updated = destination with { ChosenBookIds = chosen };
        await _settings.SaveAsync(_settings.Current with
        {
            Destinations = _settings.Current.Destinations.Select(d => d.Id == destinationId ? updated : d).ToList(),
        });

        foreach (var id in ids)
        {
            _session.Destinations?.OnBookChanged(id);
        }

        CheckedText = string.Format(CultureInfo.CurrentCulture, include ? Strings.Library_IncludedIn : Strings.Library_RemovedFrom, ids.Count, destination.Name);
    }

    [RelayCommand(CanExecute = nameof(HasChecked))]
    private void EditChecked() => RequestNavigation(NavPage.Editor, _books.Where(b => b.IsChecked).Select(b => b.Id).ToList());

    [RelayCommand]
    private void ReviewSelected()
    {
        if (Selected?.Card is { } card)
        {
            RequestNavigation(NavPage.Review, [card.Id]);
        }
    }

    /// <summary>"Enviar ao Kindle": puts the book in the Kindle's list (it is prepared at once), or takes it out.</summary>
    [RelayCommand]
    private async Task ToggleKindleAsync()
    {
        if (Selected?.Card is not { } card)
        {
            return;
        }

        if (_settings.Current.Destinations.FirstOrDefault(d => d.Type == KindleDestinationType.TypeId) is not { } kindle)
        {
            // No Kindle yet: the Destinos page is where one is added.
            RequestNavigation(NavPage.Destinations);
            return;
        }

        var inList = kindle.ChosenBookIds.Contains(card.Id);
        var ids = inList ? kindle.ChosenBookIds.Where(id => id != card.Id).ToList() : kindle.ChosenBookIds.Append(card.Id).ToList();
        await _settings.SaveAsync(_settings.Current with
        {
            Destinations = _settings.Current.Destinations.Select(d => d.Id == kindle.Id ? d with { ChosenBookIds = ids } : d).ToList(),
        });
        _session.Destinations?.OnBookChanged(card.Id);
        UpdateKindleButton();
        if (Selected is { } details)
        {
            details.Copies.Clear();
            await LoadCopiesAsync(details);
        }
    }

    /// <summary>"Enviar ao Kindle", or "Na lista do Kindle" when the book is in it.</summary>
    [ObservableProperty]
    public partial string KindleButtonText { get; private set; } = Strings.Details_SendKindle;

    private void UpdateKindleButton() =>
        KindleButtonText = Selected?.Card is { } card
                           && _settings.Current.Destinations.Any(d => d.Type == KindleDestinationType.TypeId && d.ChosenBookIds.Contains(card.Id))
            ? Strings.Details_KindleInList
            : Strings.Details_SendKindle;

    [RelayCommand]
    private void EditSelected()
    {
        if (Selected?.Card is { } card)
        {
            RequestNavigation(NavPage.Editor, [card.Id]);
        }
    }

    /// <summary>
    /// The checked books, or every book still to review. A batch operation (CLAUDE.md, rule 7):
    /// it asks first, since safe matches are approved automatically.
    /// </summary>
    [RelayCommand]
    private void FetchMetadata()
    {
        _fetchIds = IsSelecting && HasChecked
            ? _books.Where(b => b.IsChecked).Select(b => b.Id).ToList()
            : _books.Where(b => b.Book.Metadata.Review.Status == ReviewStatus.Pending).Select(b => b.Id).ToList();
        FetchConfirmation = _fetchIds.Count == 0
            ? Strings.Library_FetchNothing
            : string.Format(CultureInfo.CurrentCulture, Strings.Library_FetchConfirm, _fetchIds.Count);
    }

    [RelayCommand]
    private async Task ConfirmFetchAsync(CancellationToken ct)
    {
        FetchConfirmation = null;
        if (_fetchIds.Count > 0 && _session.Identification is { } queue)
        {
            await queue.EnqueueAsync(_fetchIds, force: true, ct);
            await Status.RefreshAsync();
        }

        _fetchIds = [];
    }

    [RelayCommand]
    private void CancelFetch()
    {
        FetchConfirmation = null;
        _fetchIds = [];
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
        Selected = new BookDetailsViewModel(card, library, _covers, file => _reader.Open(ReaderRequest.For(library, card.Book, file)), this);
        UpdateKindleButton();
        _ = LoadCopiesAsync(Selected);
    }

    private async Task LoadCopiesAsync(BookDetailsViewModel details)
    {
        if (_session.Destinations is not { } destinations)
        {
            return;
        }

        try
        {
            var kindles = destinations.Destinations.OfType<KindleDestination>().ToList();
            foreach (var (destination, state, path) in await destinations.CopiesOfAsync(details.Card.Book))
            {
                // A Kindle copy is prepared, sent or still waiting, not "em dia".
                if (kindles.FirstOrDefault(k => k.Config.Id == destination.Id) is { } kindle)
                {
                    if ((await destinations.Kindle.StatusAsync(kindle, CancellationToken.None)).GetValueOrDefault(details.Card.Id) is { } status)
                    {
                        details.Copies.Add(new CopyRow(destination.Name, KindleText(status.State),
                            status.State == KindleBookState.Waiting ? StatusKind.Warn : StatusKind.Ok, null));
                    }

                    continue;
                }

                var kind = state switch
                {
                    CopyState.UpToDate => StatusKind.Ok,
                    CopyState.Outdated or CopyState.NotGenerated => StatusKind.Warn,
                    _ => StatusKind.Neutral,
                };
                details.Copies.Add(new CopyRow(destination.Name, DestinationText.Copy(state), kind, path));
            }
        }
        catch (Exception ex) when (LibrarySession.IsExpectedFailure(ex))
        {
            _logger.LogWarning(ex, "Could not read the copies of {Book}", details.Card.Id);
        }
    }

    private static string KindleText(KindleBookState state) => state switch
    {
        KindleBookState.Ready => Strings.Copy_KindleReady,
        KindleBookState.ReadyToReplace => Strings.Copy_KindleReady,
        KindleBookState.Sent => Strings.Copy_KindleSent,
        _ => Strings.Copy_KindleWaiting,
    };

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
        RequestNavigation(NavPage.Inbox);
    }

    private void OnBookImported(object? sender, string bookId) => _reload.Trigger();

    // ---- Reading status ----

    private void OnReadingChanged(object? sender, string bookId) => _reloadReading.Trigger();

    /// <summary>Double click on a card, or a book in "Continuar lendo": the file where it was left, else the main one.</summary>
    private void OpenCard(BookCardViewModel card)
    {
        if (IsSelecting || _session.Current is not { } library)
        {
            return;
        }

        var files = card.Book.Metadata.Files;
        var file = files.FirstOrDefault(f => card.Reading is { File: var name } && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? card.Book.MainFile;
        if (file is not null)
        {
            _reader.Open(ReaderRequest.For(library, card.Book, file));
        }
    }

    /// <summary>Reads the statuses again after a book was read or marked (counts, strip, filter).</summary>
    public async Task RefreshReadingAsync(CancellationToken ct = default)
    {
        try
        {
            await ApplyReadingAsync(ct);
            UpdateSummary();
            if (Filter is LibraryFilter.Reading or LibraryFilter.Read or LibraryFilter.Unread)
            {
                ApplyView();
            }
            else
            {
                // The grid stays as it is (no jump while a book is read next to it).
                UpdateContinueReading();
            }
        }
        catch (Exception ex) when (LibrarySession.IsExpectedFailure(ex))
        {
            _logger.LogWarning(ex, "Could not read the reading statuses");
        }
    }

    private void UpdateContinueReading() =>
        ShowContinueReading = ContinueReading.Count > 0 && Filter == LibraryFilter.All && string.IsNullOrWhiteSpace(SearchText) && !IsDrilledIn;

    private async Task ApplyReadingAsync(CancellationToken ct)
    {
        var readings = _reading.Store is { } store ? await store.GetAllAsync(ct) : new Dictionary<string, BookReading>();
        foreach (var book in _books)
        {
            book.Reading = readings.GetValueOrDefault(book.Id);
        }

        ContinueReading.Clear();
        foreach (var book in _books.Where(b => b.IsReading).OrderByDescending(b => b.Reading!.UpdatedAt).Take(ContinueReadingCount))
        {
            ContinueReading.Add(new ContinueReadingViewModel(book, OpenCard));
        }
    }

    public async Task SetReadAsync(BookCardViewModel card, bool read)
    {
        if (_reading.Store is not { } store)
        {
            return;
        }

        try
        {
            await store.SetReadAsync(card.Id, read);
        }
        catch (Exception ex) when (LibrarySession.IsExpectedFailure(ex))
        {
            _logger.LogWarning(ex, "Could not mark {Book}", card.Id);
        }

        await RefreshReadingAsync();
    }

    public async Task ClearProgressAsync(BookCardViewModel card)
    {
        if (_reading.Store is not { } store)
        {
            return;
        }

        try
        {
            await store.ClearProgressAsync(card.Id);
        }
        catch (Exception ex) when (LibrarySession.IsExpectedFailure(ex))
        {
            _logger.LogWarning(ex, "Could not clear the progress of {Book}", card.Id);
        }

        await RefreshReadingAsync();
    }

    public override void Dispose()
    {
        if (_session.Queue is { } queue)
        {
            queue.BookImported -= OnBookImported;
        }

        if (_session.Identification is { } identification)
        {
            identification.BookUpdated -= OnBookImported;
        }

        _reading.Changed -= OnReadingChanged;
        _reload.Dispose();
        _reloadReading.Dispose();
        Status.Dispose();
    }
}

/// <summary>A "Livros que eu escolher" destination in the selection bar.</summary>
public sealed partial class DestinationChoiceViewModel(string id, string name, Func<string, bool, Task> set) : ViewModelBase
{
    public string IncludeText { get; } = string.Format(CultureInfo.CurrentCulture, Strings.Library_IncludeInDestination, name);
    public string RemoveText { get; } = string.Format(CultureInfo.CurrentCulture, Strings.Library_RemoveFromDestination, name);

    [RelayCommand]
    private Task IncludeAsync() => set(id, true);

    [RelayCommand]
    private Task RemoveAsync() => set(id, false);
}
