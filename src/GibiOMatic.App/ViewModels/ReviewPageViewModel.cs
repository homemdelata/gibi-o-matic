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
using GibiOMatic.Library;
using GibiOMatic.Library.Identification;
using GibiOMatic.Library.Index;
using GibiOMatic.Metadata;
using GibiOMatic.Metadata.Http;
using GibiOMatic.Metadata.Identification;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.App.ViewModels;

/// <summary>A book in the Review queue.</summary>
public sealed partial class ReviewItemViewModel(BookCardViewModel card, BookIdentification identification, string issue, Action<ReviewItemViewModel> select)
    : ViewModelBase
{
    public BookCardViewModel Card { get; } = card;
    public BookIdentification Identification { get; } = identification;
    public string BookId => Card.Id;
    public string Title => Card.Title;

    /// <summary>What is wrong, in a few words ("Título, autores e série").</summary>
    public string Issue { get; } = issue;

    public int Score => Card.Report.Score;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Select() => select(this);
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

    public string SourceText => string.Format(CultureInfo.CurrentCulture, Strings.Review_Source, IdentificationText.SourceName(Candidate.Record.Source));

    /// <summary>"128 páginas" when the source knows (Open Library, Google Books; ComicVine does not have it).</summary>
    public string? PagesText => Candidate.Record.PageCount is { } pages
        ? string.Format(CultureInfo.CurrentCulture, Strings.Review_Pages, pages)
        : null;

    public string ConfidenceText => string.Format(CultureInfo.CurrentCulture, Strings.Review_Confidence, Candidate.Confidence);

    public CheckStatus Level => IdentificationText.LevelOf(Candidate.Confidence);

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

/// <summary>A row of the "campo · atual · proposto" table, with its check box.</summary>
public sealed partial class FieldRowViewModel(FieldChange change, Action changed) : ViewModelBase
{
    public FieldChange Change { get; } = change;
    public string Label => IdentificationText.FieldLabel(Change.Field);
    public string Current => Display(Change.Current);
    public string Proposed => Display(Change.Proposed);

    private string Display(string? value) => value switch
    {
        "cover:file" => Strings.Review_CoverFile,
        "cover:source" => Strings.Review_CoverSource,
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

    /// <summary>Changes are checked by default, except conflicts and the cover (the file's is kept unless chosen).</summary>
    [ObservableProperty]
    public partial bool IsApplied { get; set; } = change.Changed && !change.Conflict && change.Field != ProposalField.Cover;

    partial void OnIsAppliedChanged(bool value) => changed();
}

/// <summary>
/// The Review (SPEC §7): books whose identification needs the user, most serious first; for each,
/// the candidates and a field-by-field comparison. "Aprovar e próximo" applies the checked fields.
/// </summary>
public sealed partial class ReviewPageViewModel : PageViewModel
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
    private CatalogBook? _book;
    private readonly IReaderLauncher _readerWindows;

    public ReviewPageViewModel(LibrarySession session, ThumbnailCache thumbnails, ICoverLoader covers, CoverImageCache remoteCovers,
        IBookFileReader reader, IUrlOpener urls, IUiThread ui, IReaderLauncher readerWindows, ILogger<ReviewPageViewModel> logger)
    {
        _readerWindows = readerWindows;
        _remoteCovers = remoteCovers;
        _reader = reader;
        _urls = urls;
        _session = session;
        _thumbnails = thumbnails;
        _covers = covers;
        _logger = logger;
        Status = new IdentificationStatusViewModel(session, ui);
        _refresh = new Debouncer(ui, TimeSpan.FromMilliseconds(500), () => RefreshAsync());
        if (session.Identification is { } queue)
        {
            queue.Changed += OnQueueChanged;
        }
    }

    public override string Title => Strings.Nav_Review;

    public IdentificationStatusViewModel Status { get; }

    public ObservableCollection<ReviewItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial ReviewItemViewModel? Selected { get; private set; }

    public bool HasSelection => Selected is not null;

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
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool HasItems { get; private set; }

    public bool IsEmpty => !HasItems;

    [ObservableProperty]
    public partial string? EmptyText { get; private set; }

    [ObservableProperty]
    public partial string? FolderPath { get; private set; }

    [ObservableProperty]
    public partial string? SearchText { get; set; }

    /// <summary>A ComicVine page the user found in the browser.</summary>
    [ObservableProperty]
    public partial string? ComicVineLink { get; set; }

    [ObservableProperty]
    public partial string? LinkError { get; private set; }

    /// <summary>"Arquivo: 132 páginas · PDF", to compare with the candidates.</summary>
    [ObservableProperty]
    public partial string? FileInfoText { get; private set; }

    public ObservableCollection<CandidateViewModel> Candidates { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCandidate), nameof(ShowNoCandidateHint), nameof(ShowCandidates))]
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
    [NotifyPropertyChangedFor(nameof(ShowNoCandidateHint), nameof(ShowCandidates))]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand), nameof(SkipCommand), nameof(SearchAgainCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? BusyText { get; private set; }

    /// <summary>What was searched last, so "nothing found" says for what.</summary>
    [ObservableProperty]
    public partial string? NoCandidateText { get; private set; }

    public bool ShowNoCandidateHint => !IsBusy && !HasCandidate;

    public bool ShowCandidates => !IsBusy && HasCandidate;

    public bool IsIdle => !IsBusy;

    private void SetBusy(string? text)
    {
        BusyText = text;
        IsBusy = text is not null;
        OnPropertyChanged(nameof(IsIdle));
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

        await RefreshAsync(ct, bookId);
    }

    /// <summary>Reloads the queue; keeps the selected book (or picks <paramref name="selectBookId"/>).</summary>
    public async Task RefreshAsync(CancellationToken ct = default, string? selectBookId = null)
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
                    return new ReviewItemViewModel(card, i, IssueOf(i, card.Report), SelectItem);
                })
                .OrderBy(i => i.Identification.Skipped)
                .ThenBy(i => i.Score)
                .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var keep = selectBookId ?? Selected?.BookId;
            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(item);
            }

            HasItems = Items.Count > 0;
            Subtitle = string.Format(CultureInfo.CurrentCulture, Strings.Review_Subtitle_Count, Items.Count);
            var counts = await queue.GetCountsAsync(ct);
            EmptyText = string.Format(CultureInfo.CurrentCulture, Strings.Review_Empty, counts.AutoApproved, counts.Approved);
            var next = Items.FirstOrDefault(i => i.BookId == keep) ?? Items.FirstOrDefault();
            if (next?.BookId != Selected?.BookId || next is null)
            {
                await SelectAsync(next, ct);
            }
            else if (next is not null)
            {
                next.IsSelected = true;
                Selected = next;
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

    private void SelectItem(ReviewItemViewModel item) => _ = SelectAsync(item, CancellationToken.None);

    private async Task SelectAsync(ReviewItemViewModel? item, CancellationToken ct)
    {
        if (Selected is { } previous)
        {
            previous.IsSelected = false;
        }

        Selected = item;
        Candidates.Clear();
        Rows.Clear();
        SelectedCandidate = null;
        ConfidenceText = null;
        NotesText = null;
        AppliedSummary = null;
        if (item is null || _session.Current is not { } library)
        {
            _book = null;
            FolderPath = null;
            return;
        }

        item.IsSelected = true;
        _book = item.Card.Book;
        FolderPath = Path.Combine(library.RootPath, _book.Folder);
        ComicVineLink = null;
        LinkError = null;
        _ = ShowFileInfoAsync(item, library);
        var result = item.Identification.Result;
        SearchText = (result?.Query ?? BookQuery.From(_book.Metadata)).EditableText;
        NoCandidateText = string.Format(CultureInfo.CurrentCulture, Strings.Review_NothingFor, SearchText);
        NotesText = IdentificationText.Notes(item.Identification.Message);
        foreach (var candidate in result?.Candidates ?? [])
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
        foreach (var change in Proposal.Compare(_book.Metadata, candidate.Candidate.Record).Where(r => r.Proposed is not null))
        {
            Rows.Add(new FieldRowViewModel(change, UpdateSummary));
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

        if (Selected == item)
        {
            var format = QualityText.FormatLabel(main.Format);
            FileInfoText = pages is { } n
                ? string.Format(CultureInfo.CurrentCulture, Strings.Review_FilePages, n, format)
                : string.Format(CultureInfo.CurrentCulture, Strings.Review_FileFormat, format);
        }
    }

    /// <summary>Opens the ComicVine search for the current words in the browser.</summary>
    [RelayCommand]
    private async Task OpenComicVineAsync()
    {
        var words = string.IsNullOrWhiteSpace(SearchText) ? Selected?.Title : SearchText.Trim();
        if (!string.IsNullOrWhiteSpace(words))
        {
            await _urls.OpenAsync(new Uri("https://comicvine.gamespot.com/search/?i=&q=" + Uri.EscapeDataString(words)));
        }
    }

    /// <summary>"Esta é a certa": the book of a pasted ComicVine link becomes the candidate.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task UseComicVineLinkAsync(CancellationToken ct)
    {
        if (Selected is not { } item || _session.Identification is not { } queue)
        {
            return;
        }

        LinkError = null;
        if (Metadata.Identification.BookIdentifier.ParseComicVineLink(ComicVineLink) is null)
        {
            LinkError = Strings.Review_LinkInvalid;
            return;
        }

        Candidates.Clear();
        Rows.Clear();
        SelectedCandidate = null;
        ConfidenceText = null;
        SetBusy(Strings.Review_LoadingLink);
        try
        {
            var found = await queue.UseComicVineLinkAsync(item.BookId, ComicVineLink!.Trim(), ct);
            SetBusy(null);
            Selected = null;
            await RefreshAsync(ct, item.BookId);
            if (found == 0)
            {
                LinkError = Strings.Review_LinkNotFound;
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

    /// <summary>Downloads (once) and decodes a candidate's cover; <c>null</c> when it cannot be had.</summary>
    private async Task<Bitmap?> LoadRemoteCoverAsync(string? url) =>
        await _remoteCovers.GetAsync(url) is { } path ? await _covers.LoadAsync(path, 160) : null;

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

        var text = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
        Candidates.Clear();
        Rows.Clear();
        SelectedCandidate = null;
        ConfidenceText = null;
        AppliedSummary = null;
        SetBusy(string.Format(CultureInfo.CurrentCulture, Strings.Review_Searching, text ?? item.Title));
        try
        {
            await queue.SearchAgainAsync(item.BookId, text, ct);
            SetBusy(null);
            Selected = null;
            await RefreshAsync(ct, item.BookId);
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
        if (!IsBusy)
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

        _refresh.Dispose();
        Status.Dispose();
    }
}
