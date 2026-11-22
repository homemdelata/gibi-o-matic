using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
using GibiOMatic.Conversion;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Core.Quality;
using GibiOMatic.Core.Settings;
using GibiOMatic.Library;
using GibiOMatic.Library.Editing;
using GibiOMatic.Library.Identification;
using GibiOMatic.Library.Index;
using GibiOMatic.Metadata;
using GibiOMatic.Metadata.Assisted;
using GibiOMatic.Metadata.Http;
using GibiOMatic.Metadata.Identification;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.App.ViewModels;

/// <summary>A book in the Review queue.</summary>
public sealed partial class ReviewItemViewModel(BookCardViewModel card, BookIdentification identification, string issue, Action<ReviewItemViewModel> select,
    IBookFileActions? files = null)
    : ViewModelBase
{
    public BookCardViewModel Card { get; } = card;
    public BookIdentification Identification { get; } = identification;
    public string BookId => Card.Id;
    public string Title => Card.Title;

    /// <summary>What is wrong, in a few words ("Title, authors and series").</summary>
    public string Issue { get; } = issue;

    /// <summary>The quality score of the metadata the book has now, before any candidate is applied.</summary>
    public int Score => Card.Report.Score;

    /// <summary>"Current score: 35 of 100, for the metadata the book has now".</summary>
    public string ScoreTip => string.Format(CultureInfo.CurrentCulture, Strings.Review_ScoreTip, Score);

    /// <summary>The candidate the comparison opens with, when there is one.</summary>
    public MetadataCandidate? Best { get; } = ReviewPageViewModel.ShownCandidates(identification.Result).FirstOrDefault();

    public bool HasBest => Best is not null;

    public bool IsBestFromClaude => Best?.Assisted is not null;

    public bool IsBestFromSearch => Best is { Assisted: null };

    /// <summary>"90%".</summary>
    public string? BestText => Best is { } best ? best.Confidence.ToString("0%", CultureInfo.CurrentCulture) : null;

    public CheckStatus BestLevel => Best is { } best ? IdentificationText.LevelOf(best.Confidence) : CheckStatus.Bad;

    /// <summary>"Best result: 90% confidence", saying when Claude found it.</summary>
    public string? BestTip => Best is { } best
        ? string.Format(CultureInfo.CurrentCulture, IsBestFromClaude ? Strings.Review_BestTipClaude : Strings.Review_BestTip, best.Confidence)
        : null;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Claude is looking at this book (or it waits for Claude).</summary>
    [ObservableProperty]
    public partial bool IsClaudeRunning { get; set; }

    [RelayCommand]
    private void Select() => select(this);

    public string RevealText => BookFileText.Reveal;

    [RelayCommand]
    private void Reveal() => files?.Reveal(Card);

    [RelayCommand]
    private void AskRemove() => files?.AskRemove(Card);
}

public sealed partial class CandidateViewModel : ViewModelBase
{
    private readonly Action<CandidateViewModel> _pick;
    private readonly Func<string?, Task<Bitmap?>> _loadCover;

    public CandidateViewModel(MetadataCandidate candidate, Action<CandidateViewModel> pick, Func<string?, Task<Bitmap?>> loadCover)
    {
        Candidate = candidate;
        _pick = pick;
        _loadCover = loadCover;
        _ = LoadCoverAsync();
    }

    public MetadataCandidate Candidate { get; private set; }

    /// <summary>The source's cover, to compare with the book's own cover.</summary>
    [ObservableProperty]
    public partial Bitmap? Cover { get; private set; }

    private async Task LoadCoverAsync()
    {
        if (Cover is null && Candidate.Record.CoverUrl is { } url)
        {
            Cover = await _loadCover(url);
        }
    }

    public string Title => Candidate.Record.Title ?? "—";

    public string MetaLine => string.Join(" · ", new[]
    {
        Candidate.Record.Publisher,
        Candidate.Record.Date?.Year.ToString(CultureInfo.InvariantCulture),
        Candidate.Record.Kind switch
        {
            BookKind.Issue => Strings.Kind_Issue,
            BookKind.Tpb => Strings.Kind_Tpb,
            BookKind.Omnibus => Strings.Kind_Omnibus,
            _ => null,
        },
        Candidate.Record.IsOneShot ? Strings.Field_OneShot : null,
    }.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>
    /// "ComicVine: Spawn Origins Collection #19 · The Kingdom, Part 2": the record as the source names
    /// it, so a wrong reading of it shows; just "Source: ComicVine" for results stored without it.
    /// </summary>
    public string SourceText => Candidate.Record.SourceName is { } name
        ? string.Format(CultureInfo.CurrentCulture, Strings.Review_SourceRecord, IdentificationText.SourceName(Candidate.Record.Source), name)
        : string.Format(CultureInfo.CurrentCulture, Strings.Review_Source, IdentificationText.SourceName(Candidate.Record.Source));

    /// <summary>"128 pages" when the source knows (Open Library, Google Books; ComicVine does not have it).</summary>
    public string? PagesText => Candidate.Record.PageCount is { } pages
        ? string.Format(CultureInfo.CurrentCulture, Strings.Review_Pages, pages)
        : null;

    public string ConfidenceText => string.Format(CultureInfo.CurrentCulture, Strings.Review_Confidence, Candidate.Confidence);

    public CheckStatus Level => IdentificationText.LevelOf(Candidate.Confidence);

    /// <summary>The candidate came from Claude (SPEC §8.4).</summary>
    public bool IsFromClaude => Candidate.Assisted is not null;

    /// <summary>"Claude (haiku, quick search)".</summary>
    public string? ClaudeLine => Candidate.Assisted is { } a ? ClaudeText.Source(a) : null;

    public bool CoverDiffers => Candidate.Assisted?.CoverMatches == false;

    /// <summary>"The cover matches the book's.", or why it differs.</summary>
    public string? CoverText => Candidate.Assisted switch
    {
        { CoverMatches: true } => Strings.Review_CoverMatches,
        { CoverMatches: false } a => string.IsNullOrWhiteSpace(a.CoverExplanation) ? Strings.Review_CoverDiffers : $"{Strings.Review_CoverDiffers} {a.CoverExplanation}",
        _ => null,
    };

    public bool EditionMissing => Candidate.Assisted?.EditionMissing == true;

    public IReadOnlyList<string> Evidence => Candidate.Assisted?.Evidence ?? [];

    public string? DoubtsText => Candidate.Assisted?.Doubts is { Length: > 0 } doubts
        ? string.Format(CultureInfo.CurrentCulture, Strings.Review_ClaudeDoubts, doubts)
        : null;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public void Update(MetadataCandidate detailed)
    {
        Candidate = detailed;
        OnPropertyChanged(string.Empty);
        _ = LoadCoverAsync();
    }

    [RelayCommand]
    private void Pick() => _pick(this);
}

/// <summary>A row of the "field · current · proposed" table, with its check box.</summary>
public sealed partial class FieldRowViewModel(FieldChange change, Action changed, bool unticked = false, string? source = null) : ViewModelBase
{
    /// <summary>"Source: credits page page-004", for a field of Claude's answer.</summary>
    public string? SourceTip { get; } = source is null ? null : string.Format(CultureInfo.CurrentCulture, Strings.Review_FieldSource, source);

    public bool HasSource => SourceTip is not null;

    public FieldChange Change { get; } = change;
    public string Label => IdentificationText.FieldLabel(Change.Field);
    public string Current => Display(Change.Current);
    public string Proposed => Display(Change.Proposed);

    private string Display(string? value) => value switch
    {
        "cover:file" => Strings.Review_CoverFile,
        "cover:source" => Strings.Review_CoverSource,
        "oneshot:yes" => Strings.Review_OneShotYes,
        "oneshot:no" => Strings.Review_OneShotNo,
        _ => DisplayKind(value),
    };

    private string DisplayKind(string? value) => Change.Field == ProposalField.Kind && value is not null
        ? value switch
        {
            "issue" => Strings.Kind_Issue,
            "tpb" => Strings.Kind_Tpb,
            "omnibus" => Strings.Kind_Omnibus,
            "artbook" => Strings.Kind_Artbook,
            "book" => Strings.Kind_Book,
            _ => value,
        }
        : value ?? "—";

    /// <summary>Only rows that change something can be applied.</summary>
    public bool CanApply => Change.Changed;

    /// <summary>
    /// Changes are checked by default, conflicts included (they only get a warning), except the cover:
    /// the file's is kept unless chosen.
    /// </summary>
    [ObservableProperty]
    public partial bool IsApplied { get; set; } = change.Changed && change.Field != ProposalField.Cover && !unticked;

    partial void OnIsAppliedChanged(bool value) => changed();
}

/// <summary>
/// The Review (SPEC §7): books whose identification needs the user, most serious first; for each,
/// the candidates and a field-by-field comparison. "Approve and next" applies the checked fields.
/// </summary>
public sealed partial class ReviewPageViewModel : PageViewModel, IBookFileActions
{
    private readonly LibrarySession _session;
    private readonly ThumbnailCache _thumbnails;
    private readonly ICoverLoader _covers;
    private readonly CoverImageCache _remoteCovers;
    private readonly IBookFileReader _reader;
    private readonly IUrlOpener _urls;
    private readonly Dictionary<string, int?> _pageCounts = [];
    private readonly ILogger<ReviewPageViewModel> _logger;
    private readonly Debouncer _refresh;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private string? _pendingBookId;

    /// <summary>The books of the queue still under review (not an approved one opened from the Library).</summary>
    private IReadOnlyList<string> _toReview = [];
    private CatalogBook? _book;

    /// <summary>The box's text when it came from a search of the book itself (not typed); searching it unchanged searches the book again.</summary>
    private string? _untypedSearchText;
    private readonly IReaderLauncher _readerWindows;
    private readonly IFileRevealer _revealer;

    public ReviewPageViewModel(LibrarySession session, ThumbnailCache thumbnails, ICoverLoader covers, CoverImageCache remoteCovers,
        IBookFileReader reader, IUrlOpener urls, IUiThread ui, IReaderLauncher readerWindows, IFileRevealer revealer, BookEditor editor,
        VolumeMatcher matcher, ISettingsService settings, ILogger<ReviewPageViewModel> logger)
    {
        _settings = settings;
        IsBySeries = settings.Current.Review.BySeries;
        Series = new SeriesReviewViewModel(session, matcher, settings, remoteCovers, covers, readerWindows, urls, logger,
            bookId => _ = ReviewBookAsync(bookId), bookId => RequestNavigation(NavPage.Editor, [bookId]));
        SelectedSort = ReviewListView.Options.FirstOrDefault(o => o.Sort == settings.Current.Review.Sort) ?? ReviewListView.Options[0];
        Series.Arrange(SelectedSort.Sort, null);
        _revealer = revealer;
        RemovePrompt = new RemoveBookPromptViewModel(session, editor, logger) { Removed = id => _ = OnRemovedAsync(id) };
        _readerWindows = readerWindows;
        _remoteCovers = remoteCovers;
        _reader = reader;
        _urls = urls;
        _session = session;
        _thumbnails = thumbnails;
        _covers = covers;
        _logger = logger;
        _ui = ui;
        Status = new IdentificationStatusViewModel(session, ui);
        _refresh = new Debouncer(ui, TimeSpan.FromMilliseconds(500), async () =>
        {
            await RefreshAsync();
            if (IsBySeries)
            {
                await Series.RefreshAsync();
            }
        });
        if (session.Identification is { } queue)
        {
            queue.Changed += OnQueueChanged;
        }

        if (session.Assisted is { } assisted)
        {
            assisted.Changed += OnClaudeChanged;
        }
    }

    private readonly IUiThread _ui;

    // ---- Claude (F9, SPEC §8.4) ----

    /// <summary>The user turned the assisted identification on in Settings.</summary>
    public bool ClaudeEnabled => _settings.Current.Claude.Enabled;

    /// <summary>A question to Claude about one book, which goes on while the user reviews other books.</summary>
    private sealed record ClaudeRun(CancellationTokenSource Cancel, string Text);

    /// <summary>The books Claude is looking at (or that wait for it), by book id.</summary>
    private readonly Dictionary<string, ClaudeRun> _claudeRuns = [];

    /// <summary>Why the last run of each book gave nothing, kept until the book is shown again.</summary>
    private readonly Dictionary<string, string> _claudeMessages = [];

    /// <summary>Claude is looking at the selected book: the comparison shows the wait and a Cancel button.</summary>
    [ObservableProperty]
    public partial bool IsClaudeRunning { get; private set; }

    /// <summary>Why the last run gave nothing ("Claude did not answer (timeout)").</summary>
    [ObservableProperty]
    public partial string? ClaudeMessage { get; private set; }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task AskClaudeAsync() => RunClaudeAsync(AssistedLevel.Quick);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task SearchDeeperAsync() => RunClaudeAsync(AssistedLevel.Deep);

    [RelayCommand]
    private void CancelClaude()
    {
        if (Selected is { } item && _claudeRuns.TryGetValue(item.BookId, out var run))
        {
            run.Cancel.Cancel();
        }
    }

    private async Task RunClaudeAsync(AssistedLevel level)
    {
        if (Selected is not { } item || _session.Assisted is not { } assisted || _claudeRuns.ContainsKey(item.BookId))
        {
            return;
        }

        if (!ClaudeEnabled)
        {
            ClaudeMessage = Strings.Claude_Off;
            return;
        }

        ClaudeMessage = null;
        _claudeMessages.Remove(item.BookId);
        Candidates.Clear();
        Rows.Clear();
        SelectedCandidate = null;
        ConfidenceText = null;
        AppliedSummary = null;

        // The run belongs to its book: the user may go on to other books, and back to this one, meanwhile.
        using var cancel = new CancellationTokenSource();
        _claudeRuns[item.BookId] = new ClaudeRun(cancel,
            string.Format(CultureInfo.CurrentCulture, level == AssistedLevel.Deep ? Strings.Review_ClaudeDeepWorking : Strings.Review_ClaudeWorking, item.Title));
        UpdateBusy();
        Library.Identification.AssistedBookResult? result = null;
        try
        {
            result = await assisted.RunBookAsync(item.BookId, level, cancel.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _claudeRuns.Remove(item.BookId);
            UpdateBusy();
        }

        var message = result switch
        {
            { Outcome: Library.Identification.AssistedOutcome.Failed } failed => ClaudeText.Failure(failed.Message),
            { Outcome: Library.Identification.AssistedOutcome.NotFound } => Strings.Review_ClaudeNotFound,
            _ => null,
        };
        if (message is not null)
        {
            _claudeMessages[item.BookId] = message;
        }

        // The book on screen is shown again only when it is the one Claude looked at; another one stays as it is.
        var showing = Selected?.BookId == item.BookId;
        await RefreshAsync(CancellationToken.None, showing ? item.BookId : null, reloadSelected: showing);
        if (showing)
        {
            ClaudeMessage = message;
            _claudeMessages.Remove(item.BookId);
        }
    }

    /// <summary>"Ask Claude (12)": the books of the queue without an answer from Claude yet.</summary>
    [ObservableProperty]
    public partial string? AskClaudeAllText { get; private set; }

    [ObservableProperty]
    public partial string? AskClaudeAllConfirmText { get; private set; }

    /// <summary>"Claude: 3 of 12 · Saga, Vol. 3" while a batch runs.</summary>
    [ObservableProperty]
    public partial string? ClaudeProgressText { get; private set; }

    private IReadOnlyList<string> _unanswered = [];
    private int _batchTotal;

    [RelayCommand]
    private void AskClaudeAll() => AskClaudeAllConfirmText = _unanswered.Count == 0
        ? null
        : string.Format(CultureInfo.CurrentCulture, Strings.Review_AskClaudeAllConfirm, _unanswered.Count, Math.Max(1, (int)Math.Ceiling(_unanswered.Count * 40 / 60.0)));

    [RelayCommand]
    private void CancelAskClaudeAll() => AskClaudeAllConfirmText = null;

    [RelayCommand]
    private void ConfirmAskClaudeAll()
    {
        AskClaudeAllConfirmText = null;
        if (_session.Assisted is { } assisted && _unanswered.Count > 0)
        {
            _batchTotal = assisted.Done + assisted.Pending + (assisted.CurrentTitle is null ? 0 : 1) + _unanswered.Count;
            assisted.Enqueue(_unanswered);
        }
    }

    [RelayCommand]
    private void StopClaudeBatch() => _session.Assisted?.CancelBatch();

    private void OnClaudeChanged(object? sender, EventArgs e) => _ui.Post(UpdateClaudeProgress);

    private void UpdateClaudeProgress()
    {
        if (_session.Assisted is not { } assisted)
        {
            return;
        }

        var batchRunning = assisted.Pending > 0 || (_batchTotal > 0 && assisted.CurrentTitle is not null && _claudeRuns.Count == 0);
        if (!batchRunning)
        {
            if (_batchTotal > 0)
            {
                _batchTotal = 0;
                _refresh.Trigger();
            }

            ClaudeProgressText = null;
            return;
        }

        _batchTotal = Math.Max(_batchTotal, assisted.Done + assisted.Pending + 1);
        ClaudeProgressText = string.Format(CultureInfo.CurrentCulture, Strings.Review_ClaudeProgress, Math.Min(assisted.Done + 1, _batchTotal), _batchTotal,
            assisted.CurrentTitle ?? "…");
    }

    public override string Title => Strings.Nav_Review;

    private readonly ISettingsService _settings;

    /// <summary>The Review by series (SPEC §7.1): the same queue, one series at a time.</summary>
    public SeriesReviewViewModel Series { get; }

    /// <summary>By series instead of By book; remembered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsByBook), nameof(ShowBookEmpty), nameof(ShowComparison))]
    public partial bool IsBySeries { get; private set; }

    public bool IsByBook => !IsBySeries;

    public bool ShowBookEmpty => IsByBook && IsEmpty;

    public bool ShowComparison => IsByBook && HasSelection;

    [RelayCommand]
    private Task ShowByBookAsync() => SetModeAsync(bySeries: false);

    [RelayCommand]
    private Task ShowBySeriesAsync() => SetModeAsync(bySeries: true);

    private async Task SetModeAsync(bool bySeries, string? bookId = null)
    {
        if (IsBySeries != bySeries)
        {
            IsBySeries = bySeries;
            await _settings.SaveAsync(_settings.Current with { Review = _settings.Current.Review with { BySeries = bySeries } });
        }

        if (bySeries)
        {
            await Series.RefreshAsync();
        }
        else
        {
            await RefreshAsync(CancellationToken.None, bookId);
        }
    }

    /// <summary>A book of "No group", reviewed on its own in the By book mode.</summary>
    private Task ReviewBookAsync(string bookId) => SetModeAsync(bySeries: false, bookId);

    public IdentificationStatusViewModel Status { get; }

    /// <summary>The books shown: the queue, filtered and in the chosen order.</summary>
    public ObservableCollection<ReviewItemViewModel> Items { get; } = [];

    /// <summary>Every book of the queue, whatever the filter.</summary>
    private IReadOnlyList<ReviewItemViewModel> _allItems = [];

    public IReadOnlyList<ReviewSortOption> SortOptions => ReviewListView.Options;

    /// <summary>The order of both lists, remembered.</summary>
    [ObservableProperty]
    public partial ReviewSortOption SelectedSort { get; set; } = ReviewListView.Options[0];

    /// <summary>Only the books (or groups) whose title, file name or group name has this text.</summary>
    [ObservableProperty]
    public partial string? FilterText { get; set; }

    /// <summary>"Nothing matches “xyz”." when the filter hides every book.</summary>
    [ObservableProperty]
    public partial string? FilterEmptyText { get; private set; }

    partial void OnSelectedSortChanged(ReviewSortOption value)
    {
        if (_settings.Current.Review.Sort != value.Sort)
        {
            _ = _settings.SaveAsync(_settings.Current with { Review = _settings.Current.Review with { Sort = value.Sort } });
        }

        _ = ArrangeAsync();
    }

    partial void OnFilterTextChanged(string? value) => _ = ArrangeAsync();

    /// <summary>Applies the order and the filter to both lists; the selected book stays selected if it is still shown.</summary>
    private async Task ArrangeAsync()
    {
        Series.Arrange(SelectedSort.Sort, FilterText);
        if (IsBySeries)
        {
            await Series.ShowGroupsAsync();
            return;
        }

        var keep = Selected?.BookId;
        ShowItems(keep);
        var next = Items.FirstOrDefault(i => i.BookId == keep) ?? Items.FirstOrDefault();
        if (next?.BookId != Selected?.BookId || next is null)
        {
            await SelectAsync(next, CancellationToken.None);
        }
        else
        {
            next.IsSelected = true;
            Selected = next;
        }
    }

    private void ShowItems(string? keep)
    {
        Items.Clear();
        foreach (var item in ReviewListView.Arrange(_allItems, SelectedSort.Sort, FilterText))
        {
            item.IsSelected = item.BookId == keep;
            Items.Add(item);
        }

        FilterEmptyText = _allItems.Count > 0 && Items.Count == 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.Review_FilterNothing, FilterText?.Trim())
            : null;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(ShowComparison))]
    public partial ReviewItemViewModel? Selected { get; private set; }

    public bool HasSelection => Selected is not null;

    /// <summary>"Remove from library", asked from the queue's right-click menu or the missing-file warning.</summary>
    public RemoveBookPromptViewModel RemovePrompt { get; }

    /// <summary>The selected book's main original is not on disk.</summary>
    [ObservableProperty]
    public partial bool IsFileMissing { get; private set; }

    [ObservableProperty]
    public partial string? MissingText { get; private set; }

    public string RevealText => BookFileText.Reveal;

    public void Reveal(BookCardViewModel card)
    {
        if (_session.Current is { } library)
        {
            _revealer.RevealBook(library, card.Book);
        }
    }

    public void AskRemove(BookCardViewModel card) => RemovePrompt.Ask(card.Id, card.Title);

    [RelayCommand]
    private void RevealSelected()
    {
        if (Selected is { } item)
        {
            Reveal(item.Card);
        }
    }

    [RelayCommand]
    private void AskRemoveSelected()
    {
        if (Selected is { } item)
        {
            AskRemove(item.Card);
        }
    }

    /// <summary>The removed book leaves the queue; the next one (or the previous, at the end) is shown.</summary>
    private async Task OnRemovedAsync(string bookId)
    {
        var index = Items.ToList().FindIndex(i => i.BookId == bookId);
        var next = index < 0 ? Selected?.BookId
            : Items.ElementAtOrDefault(index + 1)?.BookId ?? Items.ElementAtOrDefault(index - 1)?.BookId;
        if (Selected?.BookId == bookId)
        {
            Selected = null;
        }

        await Status.RefreshAsync();
        await RefreshAsync(CancellationToken.None, next);
    }

    /// <summary>Width at which covers are decoded for the hover preview.</summary>
    private const int LargeCoverWidth = 400;

    /// <summary>The selected book's cover, shown bigger when hovering it.</summary>
    [ObservableProperty]
    public partial Bitmap? LargeCover { get; private set; }

    /// <summary>Opens the book under review in the reader, to see which edition it is.</summary>
    [RelayCommand]
    private void OpenBook()
    {
        if (_book is { MainFile: { } file } book && _session.Current is { } library)
        {
            _readerWindows.Open(ReaderRequest.For(library, book, file));
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(ShowBookEmpty))]
    public partial bool HasItems { get; private set; }

    public bool IsEmpty => !HasItems;

    /// <summary>"Search all again (12)": after a ComicVine key was set, or a source was down.</summary>
    [ObservableProperty]
    public partial string? SearchAllText { get; private set; }

    /// <summary>The batch asks first (CLAUDE.md, rule 7): the current suggestions are replaced.</summary>
    [ObservableProperty]
    public partial string? SearchAllConfirmText { get; private set; }

    [RelayCommand]
    private void AskSearchAll() => SearchAllConfirmText = _toReview.Count == 0
        ? null
        : string.Format(CultureInfo.CurrentCulture, Strings.Review_SearchAllConfirm, _toReview.Count);

    [RelayCommand]
    private void CancelSearchAll() => SearchAllConfirmText = null;

    [RelayCommand]
    private async Task SearchAllAsync(CancellationToken ct)
    {
        SearchAllConfirmText = null;
        if (_session.Identification is not { } queue)
        {
            return;
        }

        var ids = _toReview;
        _logger.LogInformation("Review: {Count} books sent to be searched again", ids.Count);
        await queue.EnqueueAsync(ids, force: true, ct);
        await Status.RefreshAsync();
        await RefreshAsync(ct);
    }

    [ObservableProperty]
    public partial string? EmptyText { get; private set; }

    [ObservableProperty]
    public partial string? FolderPath { get; private set; }

    [ObservableProperty]
    public partial string? SearchText { get; set; }

    /// <summary>The right book found in the browser: a ComicVine or Amazon link, or an ISBN.</summary>
    [ObservableProperty]
    public partial string? LinkText { get; set; }

    [ObservableProperty]
    public partial string? LinkError { get; private set; }

    /// <summary>"File: 132 pages · PDF", to compare with the candidates.</summary>
    [ObservableProperty]
    public partial string? FileInfoText { get; private set; }

    public ObservableCollection<CandidateViewModel> Candidates { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCandidate), nameof(ShowNoCandidateHint), nameof(ShowCandidates), nameof(ShowClaudeEvidence))]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand))]
    public partial CandidateViewModel? SelectedCandidate { get; private set; }

    public bool HasCandidate => SelectedCandidate is not null;

    private bool CanApprove => HasCandidate && !IsBusy;

    public ObservableCollection<FieldRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial string? ConfidenceText { get; private set; }

    [ObservableProperty]
    public partial CheckStatus ConfidenceLevel { get; private set; }

    [ObservableProperty]
    public partial string? NotesText { get; private set; }

    [ObservableProperty]
    public partial string? AppliedSummary { get; private set; }

    /// <summary>A search or a details request is running (it can take seconds: the sources are spaced).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoCandidateHint), nameof(ShowCandidates), nameof(ShowClaudeEvidence))]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand), nameof(SkipCommand), nameof(SearchAgainCommand), nameof(AskClaudeCommand), nameof(SearchDeeperCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? BusyText { get; private set; }

    /// <summary>What was searched last, so "nothing found" says for what.</summary>
    [ObservableProperty]
    public partial string? NoCandidateText { get; private set; }

    public bool ShowNoCandidateHint => !IsBusy && !HasCandidate;

    public bool ShowCandidates => !IsBusy && HasCandidate;

    /// <summary>What Claude saw, for the candidate shown; hidden while the book waits, as the candidates are.</summary>
    public bool ShowClaudeEvidence => ShowCandidates && SelectedCandidate?.IsFromClaude == true;

    public bool IsIdle => !IsBusy;

    /// <summary>The search, link or approval running for the selected book, if any.</summary>
    private string? _busyText;

    private void SetBusy(string? text)
    {
        _busyText = text;
        UpdateBusy();
    }

    /// <summary>
    /// The comparison waits while something runs for the book on screen: a search, a link, an approval,
    /// or Claude looking at it. Claude's runs belong to their books, so another book shows as it is.
    /// </summary>
    private void UpdateBusy()
    {
        var claude = Selected is { } item && _claudeRuns.TryGetValue(item.BookId, out var run) ? run : null;
        BusyText = _busyText ?? claude?.Text;
        IsBusy = BusyText is not null;
        IsClaudeRunning = _busyText is null && claude is not null;
        OnPropertyChanged(nameof(IsIdle));
        foreach (var i in _allItems)
        {
            i.IsClaudeRunning = _claudeRuns.ContainsKey(i.BookId);
        }
    }

    public override void SetBooks(IReadOnlyList<string> bookIds) => _pendingBookId = bookIds.FirstOrDefault();

    public override async Task OnNavigatedToAsync(CancellationToken ct = default)
    {
        await Status.RefreshAsync();
        var bookId = _pendingBookId;
        _pendingBookId = null;

        // "Revisar metadados" on a book that was never identified (or is approved): search now.
        if (bookId is not null && _session.Identification is { } queue)
        {
            var identification = await queue.GetAsync(bookId, ct);
            if (identification?.State is null or IdentificationState.Queued or IdentificationState.AutoApproved or IdentificationState.Approved)
            {
                SetBusy(Strings.Review_SearchingSources);
                try
                {
                    await queue.SearchAgainAsync(bookId, null, ct);
                }
                finally
                {
                    SetBusy(null);
                }
            }
        }

        if (IsBySeries && bookId is not null)
        {
            // A book sent to the Review from elsewhere is shown on its own.
            IsBySeries = false;
        }

        await RefreshAsync(ct, bookId);
        if (IsBySeries)
        {
            await Series.RefreshAsync(ct: ct);
        }
    }

    /// <summary>Reloads the queue; keeps the selected book (or picks <paramref name="selectBookId"/>).</summary>
    /// <param name="reloadSelected">Shows the selected book again even when it stays selected (its result changed).</param>
    public async Task RefreshAsync(CancellationToken ct = default, string? selectBookId = null, bool reloadSelected = false)
    {
        if (_session.Identification is not { } queue || _session.Index is not { } index || _session.Current is not { } library)
        {
            return;
        }

        // A queue change and a navigation may ask at the same time; one refresh at a time.
        await _refreshGate.WaitAsync(ct);
        try
        {
            var reviewItems = await queue.GetReviewItemsAsync(ct);
            var books = (await index.GetBooksAsync(ct)).ToDictionary(b => b.Metadata.Id);
            var context = QualityContext.From(books.Values.Select(b => b.Metadata));
            var items = reviewItems
                .Where(i => books.ContainsKey(i.BookId) && (books[i.BookId].Metadata.Review.Status == ReviewStatus.Pending || i.BookId == selectBookId))
                .Select(i =>
                {
                    var book = books[i.BookId];
                    var card = new BookCardViewModel(book, QualityAnalyzer.Analyze(book.Metadata, context), library, _thumbnails, _covers, _ => { });
                    return new ReviewItemViewModel(card, i, IssueOf(i, card.Report), SelectItem, this) { IsClaudeRunning = _claudeRuns.ContainsKey(i.BookId) };
                })
                .ToList();

            var keep = selectBookId ?? Selected?.BookId;
            _allItems = items;
            ShowItems(keep);
            HasItems = items.Count > 0;
            _toReview = items.Where(i => books[i.BookId].Metadata.Review.Status == ReviewStatus.Pending).Select(i => i.BookId).ToList();
            SearchAllText = _toReview.Count == 0 ? null : string.Format(CultureInfo.CurrentCulture, Strings.Review_SearchAll, _toReview.Count);
            OnPropertyChanged(nameof(ClaudeEnabled));
            if (ClaudeEnabled && _session.Assisted is { } assisted)
            {
                var answered = await assisted.Store.GetAnsweredAsync(ct);
                _unanswered = _toReview.Where(id => !answered.Contains(id)).ToList();
            }
            else
            {
                _unanswered = [];
            }

            AskClaudeAllText = _unanswered.Count == 0 ? null : string.Format(CultureInfo.CurrentCulture, Strings.Review_AskClaudeAll, _unanswered.Count);
            if (AskClaudeAllText is null)
            {
                AskClaudeAllConfirmText = null;
            }

            if (SearchAllText is null)
            {
                SearchAllConfirmText = null;
            }
            Subtitle = string.Format(CultureInfo.CurrentCulture, Strings.Review_Subtitle_Count, items.Count);
            var counts = await queue.GetCountsAsync(ct);
            EmptyText = string.Format(CultureInfo.CurrentCulture, Strings.Review_Empty, counts.AutoApproved, counts.Approved);
            var next = Items.FirstOrDefault(i => i.BookId == keep) ?? Items.FirstOrDefault();
            if (reloadSelected || next?.BookId != Selected?.BookId || next is null)
            {
                await SelectAsync(next, ct);
            }
            else if (next is not null)
            {
                next.IsSelected = true;
                Selected = next;
                if (LargeCover is null)
                {
                    // The first try may have failed (Google Drive waking up); failures are not cached.
                    _ = LoadLargeCoverAsync(next, library);
                }
            }
        }
        catch (Exception ex) when (LibrarySession.IsExpectedFailure(ex))
        {
            _logger.LogError(ex, "Could not load the Review");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static string IssueOf(BookIdentification identification, QualityReport report)
    {
        if (identification.State == IdentificationState.NoMatch)
        {
            return Strings.Review_NoCandidate;
        }

        if (identification.State == IdentificationState.Failed)
        {
            return Strings.Review_SearchFailed;
        }

        var fields = new[] { QualityField.Title, QualityField.Authors, QualityField.Date, QualityField.Series }
            .Where(report.HasProblemIn)
            .Select(f => f switch
            {
                QualityField.Title => Strings.Details_Title,
                QualityField.Authors => Strings.Details_Authors,
                QualityField.Date => Strings.Details_Date,
                _ => Strings.Details_Series,
            })
            .ToList();
        return fields.Count == 0 ? Strings.Review_CheckMatch : string.Join(", ", fields);
    }

    /// <summary>
    /// The candidates the comparison shows, in order: Claude's record and the algorithm's same record
    /// are shown once, as Claude's (with its evidence).
    /// </summary>
    internal static IEnumerable<MetadataCandidate> ShownCandidates(IdentificationResult? result)
    {
        var candidates = result?.Candidates ?? [];
        var fromClaude = candidates.Where(c => c.Assisted is not null).Select(c => c.Record.ComicVineId).OfType<string>().ToHashSet();
        return candidates.Where(c => c.Assisted is not null || c.Record.ComicVineId is null || !fromClaude.Contains(c.Record.ComicVineId));
    }

    private void SelectItem(ReviewItemViewModel item) => _ = SelectAsync(item, CancellationToken.None);

    private async Task SelectAsync(ReviewItemViewModel? item, CancellationToken ct)
    {
        if (Selected is { } previous)
        {
            previous.IsSelected = false;
        }

        Selected = item;
        LargeCover = null;
        Candidates.Clear();
        Rows.Clear();
        SelectedCandidate = null;
        ConfidenceText = null;
        NotesText = null;
        AppliedSummary = null;
        if (item?.BookId != _book?.Metadata.Id)
        {
            // A run that ended while another book was on screen says why it gave nothing now.
            ClaudeMessage = item is not null && _claudeMessages.Remove(item.BookId, out var message) ? message : null;
        }

        UpdateBusy();

        if (item is null || _session.Current is not { } library)
        {
            _book = null;
            FolderPath = null;
            IsFileMissing = false;
            return;
        }

        item.IsSelected = true;
        _book = item.Card.Book;
        FolderPath = Path.Combine(library.RootPath, _book.Folder);
        IsFileMissing = _session.CheckFile(_book);
        MissingText = BookFileText.Missing(_book);
        LinkText = null;
        LinkError = null;
        _ = ShowFileInfoAsync(item, library);
        _ = LoadLargeCoverAsync(item, library);
        var result = item.Identification.Result;

        // The box shows the search that found these candidates: the words typed, exactly as typed,
        // or else the book's own reading. That reading may come from an older version ("RoboCop #3"),
        // so searching it unchanged searches the book itself, with the current title parser.
        var query = result?.Query;
        var shown = query?.TypedText ?? query?.EditableText ?? BookQuery.From(_book.Metadata).EditableText;
        _untypedSearchText = query?.TypedText is null ? shown : null;
        SearchText = shown;
        NoCandidateText = string.Format(CultureInfo.CurrentCulture, Strings.Review_NothingFor, SearchText);
        NotesText = IdentificationText.Notes(item.Identification.Message);

        // Candidates found by an older version of the identification ("RoboCop #3" for "RoboCop Vol. 3 -
        // Last Stand #8") would be found differently now: the book is searched again as it opens.
        // Typed searches stay as they are, and so do approved books opened from the Library.
        if (result is { IsFromOlderAlgorithm: true, Query.TypedText: null } && _book.Metadata.Review.Status == ReviewStatus.Pending && IsIdle)
        {
            _ = SearchAgainCommand.ExecuteAsync(null);
            return;
        }

        foreach (var candidate in ShownCandidates(result))
        {
            Candidates.Add(new CandidateViewModel(candidate, c => _ = PickAsync(c, CancellationToken.None), LoadRemoteCoverAsync));
        }

        if (Candidates.Count > 0)
        {
            await PickAsync(Candidates[0], ct);
        }
        else
        {
            ConfidenceText = Strings.Review_NoCandidate;
            ConfidenceLevel = CheckStatus.Bad;
        }
    }

    private async Task PickAsync(CandidateViewModel candidate, CancellationToken ct)
    {
        foreach (var c in Candidates)
        {
            c.IsSelected = c == candidate;
        }

        SelectedCandidate = candidate;
        if (!candidate.Candidate.Record.IsDetailed && _session.Identification is { } queue)
        {
            try
            {
                SetBusy(Strings.Review_LoadingDetails);
                candidate.Update(await queue.CompleteAsync(candidate.Candidate, ct));
            }
            catch (Exception ex) when (ex is Metadata.Http.MetadataSourceException or HttpRequestException)
            {
                NotesText = IdentificationText.Notes("comicvine:Unavailable");
                _logger.LogWarning(ex, "Could not load the candidate details");
            }
            finally
            {
                SetBusy(null);
            }
        }

        if (_book is null || SelectedCandidate != candidate)
        {
            return;
        }

        ConfidenceText = $"{candidate.ConfidenceText} · {IdentificationText.Reasons(candidate.Candidate)}";
        ConfidenceLevel = candidate.Level;
        Rows.Clear();
        var assisted = candidate.Candidate.Assisted;
        foreach (var change in Proposal.Compare(_book.Metadata, candidate.Candidate.Record).Where(r => r.Proposed is not null))
        {
            Rows.Add(new FieldRowViewModel(change, UpdateSummary, assisted?.Unticked.Contains(change.Field) == true,
                assisted is null ? null : ClaudeText.FieldSource(assisted, change.Field)));
        }

        UpdateSummary();
    }

    /// <summary>Pages of the main original: recorded at import, or counted now for older books.</summary>
    private async Task ShowFileInfoAsync(ReviewItemViewModel item, LibraryInfo library)
    {
        FileInfoText = null;
        if (item.Card.Book.MainFile is not { } main)
        {
            return;
        }

        if (!_pageCounts.TryGetValue(item.BookId, out var pages))
        {
            pages = main.Pages;
            if (pages is null)
            {
                var path = Path.Combine(library.RootPath, item.Card.Book.Folder, main.Name);
                pages = File.Exists(path) ? (await _reader.ReadAsync(path, main.Format)).Metadata.PageCount : null;
            }

            _pageCounts[item.BookId] = pages;
        }

        if (Selected?.BookId == item.BookId)
        {
            var format = QualityText.FormatLabel(main.Format);
            FileInfoText = pages is { } n
                ? string.Format(CultureInfo.CurrentCulture, Strings.Review_FilePages, n, format)
                : string.Format(CultureInfo.CurrentCulture, Strings.Review_FileFormat, format);
        }
    }

    /// <summary>Opens the ComicVine search for the current words in the browser.</summary>
    [RelayCommand]
    private Task OpenComicVineAsync() => OpenSearchAsync("https://comicvine.gamespot.com/search/?i=&q=");

    /// <summary>Opens Amazon's book search (print editions carry the ISBN in their link).</summary>
    [RelayCommand]
    private Task OpenAmazonAsync() => OpenSearchAsync("https://www.amazon.com/s?i=stripbooks&k=");

    /// <summary>Opens the Goodreads search (the ISBN is in the edition details).</summary>
    [RelayCommand]
    private Task OpenGoodreadsAsync() => OpenSearchAsync("https://www.goodreads.com/search?q=");

    private async Task OpenSearchAsync(string prefix)
    {
        var words = string.IsNullOrWhiteSpace(SearchText) ? Selected?.Title : SearchText.Trim();
        if (!string.IsNullOrWhiteSpace(words))
        {
            await _urls.OpenAsync(new Uri(prefix + Uri.EscapeDataString(words)));
        }
    }

    /// <summary>"Usar este": the book of a pasted ComicVine link, or of an ISBN (pasted, or from an Amazon link), becomes the candidate.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task UseLinkAsync(CancellationToken ct)
    {
        if (Selected is not { } item || _session.Identification is not { } queue)
        {
            return;
        }

        LinkError = null;
        var reference = BookReference.Parse(LinkText);
        var problem = reference.Kind switch
        {
            BookReferenceKind.Unknown => Strings.Review_LinkInvalid,
            BookReferenceKind.AmazonKindle => Strings.Review_LinkKindle,
            BookReferenceKind.Goodreads => Strings.Review_LinkGoodreads,
            _ => null,
        };
        if (problem is not null)
        {
            LinkError = problem;
            return;
        }

        Candidates.Clear();
        Rows.Clear();
        SelectedCandidate = null;
        ConfidenceText = null;
        var byIsbn = reference.Kind == BookReferenceKind.Isbn;
        SetBusy(byIsbn ? string.Format(CultureInfo.CurrentCulture, Strings.Review_LoadingIsbn, reference.Isbn) : Strings.Review_LoadingLink);
        try
        {
            var found = byIsbn
                ? (await queue.SearchByIsbnAsync(item.BookId, reference.Isbn!, ct)).Candidates.Any(c => c.Reasons.Contains(MatchReason.Isbn))
                : await queue.UseComicVineLinkAsync(item.BookId, LinkText!.Trim(), ct) > 0;
            SetBusy(null);
            Selected = null;
            await RefreshAsync(ct, item.BookId);
            if (!found)
            {
                LinkError = byIsbn ? string.Format(CultureInfo.CurrentCulture, Strings.Review_IsbnNotFound, reference.Isbn) : Strings.Review_LinkNotFound;
            }
        }
        catch (MetadataSourceException ex)
        {
            _logger.LogWarning(ex, "Could not load the ComicVine link");
            Selected = null;
            await RefreshAsync(ct, item.BookId);
            LinkError = IdentificationText.Notes($"comicvine:{ex.Error}");
        }
        finally
        {
            SetBusy(null);
        }
    }

    /// <summary>
    /// Downloads (once) and decodes a candidate's cover, big enough for the hover preview;
    /// <c>null</c> when it cannot be had.
    /// </summary>
    private async Task<Bitmap?> LoadRemoteCoverAsync(string? url) =>
        await _remoteCovers.GetAsync(url) is { } path ? await _covers.LoadAsync(path, LargeCoverWidth) : null;

    /// <summary>The book's own cover at preview size; the thumbnail is too small to show bigger.</summary>
    private async Task LoadLargeCoverAsync(ReviewItemViewModel item, LibraryInfo library)
    {
        var cover = await _covers.LoadAsync(Path.Combine(library.RootPath, item.Card.Book.Folder, LibraryLayout.CoverFileName), LargeCoverWidth);
        // By id: a refresh while loading replaces the selected item with a new one for the same book.
        if (Selected?.BookId == item.BookId)
        {
            LargeCover = cover;
        }
    }

    private void UpdateSummary() =>
        AppliedSummary = string.Format(CultureInfo.CurrentCulture, Strings.Review_AppliedSummary, Rows.Count(r => r.IsApplied), Rows.Count(r => r.CanApply));

    [RelayCommand(CanExecute = nameof(CanApprove))]
    private async Task ApproveAsync(CancellationToken ct)
    {
        if (Selected is not { } item || SelectedCandidate is not { } candidate || _session.Identification is not { } queue)
        {
            return;
        }

        var fields = Rows.Where(r => r.IsApplied).Select(r => r.Change.Field).ToHashSet();
        var index = Items.IndexOf(item);
        // Downloading the full-size cover takes a moment.
        SetBusy(fields.Contains(ProposalField.Cover) ? Strings.Review_DownloadingCover : null);
        try
        {
            await queue.ApproveAsync(item.BookId, candidate.Candidate, fields, ct);
        }
        finally
        {
            SetBusy(null);
        }

        await MoveToNextAsync(index, ct);
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task SkipAsync(CancellationToken ct)
    {
        if (Selected is not { } item || _session.Identification is not { } queue)
        {
            return;
        }

        var index = Items.IndexOf(item);
        await queue.SkipAsync(item.BookId, ct);
        await MoveToNextAsync(index, ct);
    }

    private async Task MoveToNextAsync(int index, CancellationToken ct)
    {
        var nextId = Items.Count > index + 1 ? Items[index + 1].BookId : null;
        Selected = null;
        await RefreshAsync(ct, nextId);
    }

    [RelayCommand]
    private void EditByHand()
    {
        if (Selected is { } item)
        {
            RequestNavigation(NavPage.Editor, [item.BookId]);
        }
    }

    /// <summary>
    /// Clears the old result right away and says what is being searched, so the wait (seconds,
    /// the sources are spaced) never looks like "nothing found".
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task SearchAgainAsync(CancellationToken ct)
    {
        if (Selected is not { } item || _session.Identification is not { } queue)
        {
            return;
        }

        // Unchanged text from a search of the book itself searches the book again, with the current
        // title parser and its publisher and year (typed text has neither).
        var typed = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
        var text = typed == _untypedSearchText ? null : typed;
        Candidates.Clear();
        Rows.Clear();
        SelectedCandidate = null;
        ConfidenceText = null;
        AppliedSummary = null;
        SetBusy(string.Format(CultureInfo.CurrentCulture, Strings.Review_Searching, text ?? item.Title));
        try
        {
            await queue.SearchAgainAsync(item.BookId, text, ct);

            // Busy until the list holds the new result, which is shown even if the book was clicked meanwhile.
            await RefreshAsync(ct, item.BookId, reloadSelected: true);
            SearchText = text ?? SearchText;
            NoCandidateText = string.Format(CultureInfo.CurrentCulture, Strings.Review_NothingFor, text ?? item.Title);
        }
        finally
        {
            SetBusy(null);
        }
    }

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        // New books in the review appear on their own; the one being reviewed stays put.
        if (!IsBusy && Series.IsIdle)
        {
            _refresh.Trigger();
        }
    }

    public override void Dispose()
    {
        if (_session.Identification is { } queue)
        {
            queue.Changed -= OnQueueChanged;
        }

        if (_session.Assisted is { } assisted)
        {
            assisted.Changed -= OnClaudeChanged;
        }

        foreach (var run in _claudeRuns.Values)
        {
            run.Cancel.Cancel();
        }

        _refresh.Dispose();
        Status.Dispose();
    }
}
