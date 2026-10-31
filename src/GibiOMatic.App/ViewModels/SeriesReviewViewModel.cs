using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
using GibiOMatic.Core.Settings;
using GibiOMatic.Core.Text;
using GibiOMatic.Library.Identification;
using GibiOMatic.Metadata;
using GibiOMatic.Metadata.Http;
using GibiOMatic.Metadata.Identification;
using GibiOMatic.Metadata.Sources;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.App.ViewModels;

/// <summary>An entry of the group list: a group, or the fixed "No group" at the end.</summary>
public sealed partial class SeriesGroupItemViewModel(ReviewGroup? group, int count, SeriesReviewViewModel owner) : ViewModelBase
{
    /// <summary>Null for "No group".</summary>
    public ReviewGroup? Group { get; } = group;

    public bool IsNoGroup => Group is null;

    public string Name => Group?.Name ?? Strings.Series_NoGroup;

    public string CountText => SeriesReviewViewModel.Books(count);

    public string? WarningText => Group is not { HasWarnings: true } g
        ? null
        : string.Join(" · ", new[]
        {
            g.Diverging > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.Series_Diverge, g.Diverging) : null,
            g.NumberRepeated ? Strings.Series_NumberRepeated : null,
            g.NumberMissing ? Strings.Series_NumberMissing : null,
        }.OfType<string>());

    public bool HasWarnings => WarningText is not null;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private Task Select() => owner.OpenAsync(this);

    /// <summary>A book dragged onto the entry joins the group (or goes to No group).</summary>
    [RelayCommand]
    private Task Drop(string bookId) => owner.MoveBookAsync(bookId, this);
}

/// <summary>A ComicVine volume found by the group's books, to choose as the group's series.</summary>
public sealed partial class VolumeOptionViewModel : ViewModelBase
{
    private readonly Action<VolumeOptionViewModel> _pick;

    public VolumeOptionViewModel(CvVolume volume, int usedBy, int groupSize, string? coverUrl, Action<VolumeOptionViewModel> pick, Func<string?, Task<Bitmap?>> loadCover)
    {
        Volume = volume;
        UsedBy = usedBy;
        _groupSize = groupSize;
        _pick = pick;
        _ = LoadAsync(loadCover, coverUrl);
    }

    private async Task LoadAsync(Func<string?, Task<Bitmap?>> load, string? url) => Cover = await load(url);

    public CvVolume Volume { get; }

    /// <summary>Books of the group whose best candidate is in this volume.</summary>
    public int UsedBy { get; }

    public string Name => Volume.Name;

    public string MetaLine => string.Join(" · ", new[]
    {
        Volume.Publisher,
        Volume.StartYear?.ToString(CultureInfo.InvariantCulture),
        Volume.IssueCount > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.Series_Issues, Volume.IssueCount) : null,
    }.Where(p => !string.IsNullOrEmpty(p)));

    private readonly int _groupSize;

    /// <summary>"Best match for 1 of 2 books": the other books may still have an issue in it.</summary>
    public string UsedByText => string.Format(CultureInfo.CurrentCulture, Strings.Series_BestFor, UsedBy, _groupSize);

    [ObservableProperty]
    public partial Bitmap? Cover { get; private set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Pick() => _pick(this);
}

/// <summary>An issue of the group's volume, offered to a book without a match.</summary>
public sealed partial class IssueOptionViewModel(CvIssueSummary issue, Action<CvIssueSummary> pick) : ViewModelBase
{
    public string Text => string.Join(" · ", new[]
    {
        "#" + issue.IssueNumber,
        issue.Name,
        issue.Date?.Year.ToString(CultureInfo.InvariantCulture),
    }.Where(p => !string.IsNullOrEmpty(p)));

    [RelayCommand]
    private void Pick() => pick(issue);
}

/// <summary>A row of the open group's table: one book and what it will get.</summary>
public sealed partial class SeriesRowViewModel : ViewModelBase
{
    private readonly SeriesReviewViewModel _owner;

    public SeriesRowViewModel(SeriesPlanRow plan, SeriesReviewViewModel owner)
    {
        Plan = plan;
        _owner = owner;
    }

    public SeriesPlanRow Plan { get; private set; }

    public string BookId => Plan.BookId;

    public string FileName => Plan.Book.Book.MainFile?.DownloadedAs ?? Plan.Book.Book.MainFile?.Name ?? Plan.Book.Title;

    public string NumberText => Plan.Book.Number ?? "—";

    /// <summary>What the book matched today: "Deadly Class #3 · Reagan Youth", or "No candidate".</summary>
    public string TodayText => Plan.Book.Best is { } best ? best.Record.SourceName ?? best.Record.Title ?? "—" : Strings.Series_NoCandidate;

    public bool HasMatch => Plan.Candidate is not null;

    public bool IsNoMatch => !HasMatch;

    public string ResultTitle => Plan.NewTitle ?? (HasMatch ? Plan.Book.Title : Strings.Series_NoMatch);

    /// <summary>The issue it will get, and where it came from.</summary>
    public string? ResultIssue => Plan.Candidate is { } c
        ? Plan.Kind == VolumeMatchKind.OwnCandidate
            ? $"{c.Record.SourceName ?? c.Record.Title} ({Strings.Series_OwnCandidate})"
            : c.Record.SourceName ?? c.Record.Title
        : null;

    /// <summary>The row differs from what the book would get on its own: highlighted.</summary>
    public bool IsChanged => Plan.Changes;

    public bool CanChooseIssue => _owner.HasIssues;

    public IReadOnlyList<IssueOptionViewModel> Issues => _owner.IssueOptionsFor(this);

    [ObservableProperty]
    public partial Bitmap? Cover { get; private set; }

    public void Update(SeriesPlanRow plan)
    {
        var url = Plan.Candidate?.Record.CoverUrl;
        Plan = plan;
        OnPropertyChanged(string.Empty);
        if (plan.Candidate?.Record.CoverUrl != url || Cover is null)
        {
            _ = LoadCoverAsync();
        }
    }

    /// <summary>The book's own cover, to compare with the record's.</summary>
    [ObservableProperty]
    public partial Bitmap? BookCover { get; private set; }

    public async Task LoadBookCoverAsync() => BookCover = await _owner.LoadBookCoverAsync(Plan.Book.Book);

    public async Task LoadCoverAsync()
    {
        var url = Plan.Candidate?.Record.CoverUrl;
        var cover = await _owner.LoadCoverAsync(url);
        if (Plan.Candidate?.Record.CoverUrl == url)
        {
            Cover = cover;
        }
    }

    [RelayCommand]
    private void Open() => _owner.OpenBook(this);

    [RelayCommand]
    private void EditByHand() => _owner.EditByHand(this);

    [RelayCommand]
    private Task Remove() => _owner.RemoveAsync(this);
}

/// <summary>A book of the queue that can be added to the open group, or picked in No group.</summary>
public sealed partial class SeriesBookViewModel(ReviewGroupBook book, string? groupName, SeriesReviewViewModel owner) : ViewModelBase
{
    public ReviewGroupBook Book { get; } = book;

    public string Title => Book.Title;

    /// <summary>Where the book is now: a group's name, or "No group".</summary>
    public string Where => groupName ?? Strings.Series_NoGroup;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    [RelayCommand]
    private Task Add() => owner.AddAsync(this);

    [RelayCommand]
    private void Review() => owner.ReviewBook(this);
}

/// <summary>
/// The Review by series (SPEC §7.1): the queue grouped by series; for the open group, one series and
/// one title format for every book, a table that is the dry run, and one approval for all of them.
/// </summary>
public sealed partial class SeriesReviewViewModel : ViewModelBase
{
    /// <summary>Covers are decoded big enough for the hover preview.</summary>
    private const int CoverWidth = 400;

    private readonly LibrarySession _session;
    private readonly VolumeMatcher _matcher;
    private readonly ISettingsService _settings;
    private readonly CoverImageCache _remoteCovers;
    private readonly ICoverLoader _covers;
    private readonly IReaderLauncher _reader;
    private readonly IUrlOpener _urls;
    private readonly ILogger _logger;
    private readonly Action<string> _reviewBook;
    private readonly Action<string> _editBook;

    private ReviewGrouping _grouping = new([], []);
    private VolumeIssues? _issues;
    private CancellationTokenSource? _loading;
    private bool _settingSeries;

    public SeriesReviewViewModel(LibrarySession session, VolumeMatcher matcher, ISettingsService settings, CoverImageCache remoteCovers, ICoverLoader covers,
        IReaderLauncher reader, IUrlOpener urls, ILogger logger, Action<string> reviewBook, Action<string> editBook)
    {
        _session = session;
        _matcher = matcher;
        _settings = settings;
        _remoteCovers = remoteCovers;
        _covers = covers;
        _reader = reader;
        _urls = urls;
        _logger = logger;
        _reviewBook = reviewBook;
        _editBook = editBook;
        Template = settings.Current.Review.TitleTemplate;
    }

    /// <summary>"1 book", "6 books".</summary>
    public static string Books(int count) =>
        count == 1 ? Strings.Series_OneBook : string.Format(CultureInfo.CurrentCulture, Strings.Series_Books, count);

    private ReviewGroups? Groups => _session.Index is { } index && _session.Identification is { } queue ? new ReviewGroups(index, queue) : null;

    public ObservableCollection<SeriesGroupItemViewModel> GroupItems { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGroupOpen), nameof(IsNoGroupOpen), nameof(OtherGroups))]
    public partial SeriesGroupItemViewModel? Selected { get; private set; }

    public bool IsGroupOpen => Selected is { IsNoGroup: false };

    public bool IsNoGroupOpen => Selected is { IsNoGroup: true };

    [ObservableProperty]
    public partial bool HasGroups { get; private set; }

    // ---- The group list ----

    /// <summary>Recomputes the groups; keeps the open one (or opens <paramref name="openId"/>, or the first).</summary>
    public async Task RefreshAsync(string? openId = null, CancellationToken ct = default)
    {
        if (Groups is not { } groups)
        {
            return;
        }

        try
        {
            _grouping = await groups.GetAsync(ct);
        }
        catch (Exception ex) when (LibrarySession.IsExpectedFailure(ex))
        {
            _logger.LogError(ex, "Could not group the Review by series");
            return;
        }

        var keep = openId ?? Selected?.Group?.Id ?? (Selected?.IsNoGroup == true ? NoGroupId : null);
        GroupItems.Clear();
        foreach (var group in _grouping.Groups)
        {
            GroupItems.Add(new SeriesGroupItemViewModel(group, group.Books.Count, this));
        }

        GroupItems.Add(new SeriesGroupItemViewModel(null, _grouping.Ungrouped.Count, this));
        HasGroups = _grouping.Groups.Count > 0 || _grouping.Ungrouped.Count > 0;
        var next = GroupItems.FirstOrDefault(g => (g.Group?.Id ?? NoGroupId) == keep) ?? GroupItems[0];
        await OpenAsync(next, reload: next.Group?.Id != Selected?.Group?.Id || next.Group is null || BooksChanged(next));
    }

    private const string NoGroupId = "";

    private bool BooksChanged(SeriesGroupItemViewModel next) =>
        next.Group is { } g && !g.Books.Select(b => b.BookId).SequenceEqual(Rows.Select(r => r.BookId));

    public Task OpenAsync(SeriesGroupItemViewModel item) => OpenAsync(item, reload: true);

    private async Task OpenAsync(SeriesGroupItemViewModel item, bool reload)
    {
        foreach (var g in GroupItems)
        {
            g.IsSelected = g == item;
        }

        var previous = Selected;
        Selected = item;
        ConfirmText = null;
        AddSearch = null;
        IsAdding = false;
        MergeTarget = null;
        if (!reload && previous is not null)
        {
            // The same group with the same books: only its entry was recreated.
            return;
        }

        UngroupedBooks.Clear();
        if (item.Group is not { } group)
        {
            Rows.Clear();
            foreach (var book in _grouping.Ungrouped)
            {
                UngroupedBooks.Add(new SeriesBookViewModel(book, null, this));
            }

            return;
        }

        await OpenGroupAsync(group);
    }

    /// <summary>A book dropped on a group's entry.</summary>
    public async Task MoveBookAsync(string bookId, SeriesGroupItemViewModel target)
    {
        if (Groups is not { } groups || target.Group?.Books.Any(b => b.BookId == bookId) == true)
        {
            return;
        }

        if (target.Group is { } group)
        {
            await groups.AddAsync(group, [bookId]);
        }
        else
        {
            await groups.RemoveAsync([bookId]);
        }

        await RefreshAsync();
    }

    // ---- Corrections ----

    [RelayCommand]
    private async Task UngroupAsync()
    {
        if (Selected?.Group is { } group && Groups is { } groups)
        {
            await groups.UngroupAsync(group);
            await RefreshAsync(NoGroupId);
        }
    }

    /// <summary>The groups the open one can be merged with.</summary>
    public IReadOnlyList<SeriesGroupItemViewModel> OtherGroups => GroupItems.Where(g => g.Group is not null && g != Selected).ToList();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
    public partial SeriesGroupItemViewModel? MergeTarget { get; set; }

    private bool CanMerge => MergeTarget?.Group is not null;

    [RelayCommand(CanExecute = nameof(CanMerge))]
    private async Task MergeAsync()
    {
        if (Selected?.Group is { } group && MergeTarget?.Group is { } into && Groups is { } groups)
        {
            await groups.MergeAsync(group, into);
            await RefreshAsync(into.Id);
        }
    }

    /// <summary>"Add to group": the search box over the other books of the queue.</summary>
    [ObservableProperty]
    public partial bool IsAdding { get; private set; }

    [ObservableProperty]
    public partial string? AddSearch { get; set; }

    public ObservableCollection<SeriesBookViewModel> AddCandidates { get; } = [];

    partial void OnAddSearchChanged(string? value) => FillAddCandidates();

    [RelayCommand]
    private void ToggleAdding()
    {
        IsAdding = !IsAdding;
        FillAddCandidates();
    }

    private void FillAddCandidates()
    {
        AddCandidates.Clear();
        if (!IsAdding || Selected?.Group is not { } open)
        {
            return;
        }

        var words = AddSearch?.Trim();
        var others = _grouping.Ungrouped.Select(b => (Book: b, Group: (string?)null))
            .Concat(_grouping.Groups.Where(g => g.Id != open.Id).SelectMany(g => g.Books.Select(b => (Book: b, Group: (string?)g.Name))));
        foreach (var (book, group) in others
                     .Where(x => string.IsNullOrEmpty(words) || x.Book.Title.Contains(words, StringComparison.CurrentCultureIgnoreCase))
                     .OrderBy(x => x.Book.Title, StringComparer.CurrentCultureIgnoreCase)
                     .Take(50))
        {
            AddCandidates.Add(new SeriesBookViewModel(book, group, this));
        }
    }

    public async Task AddAsync(SeriesBookViewModel book)
    {
        if (Selected?.Group is { } group && Groups is { } groups)
        {
            await groups.AddAsync(group, [book.Book.BookId]);
            await RefreshAsync(group.Id);
            IsAdding = true;
            FillAddCandidates();
        }
    }

    public ObservableCollection<SeriesBookViewModel> UngroupedBooks { get; } = [];

    [RelayCommand]
    private async Task CreateGroupAsync()
    {
        var chosen = UngroupedBooks.Where(b => b.IsChecked).Select(b => b.Book.BookId).ToList();
        if (chosen.Count > 0 && Groups is { } groups)
        {
            await RefreshAsync(await groups.CreateAsync(chosen));
        }
    }

    public void ReviewBook(SeriesBookViewModel book) => _reviewBook(book.Book.BookId);

    // ---- 1. Series ----

    public ObservableCollection<VolumeOptionViewModel> Volumes { get; } = [];

    [ObservableProperty]
    public partial string? SeriesName { get; set; }

    [ObservableProperty]
    public partial string? SeriesPublisher { get; set; }

    [ObservableProperty]
    public partial string? SeriesYear { get; set; }

    /// <summary>The volume the books are matched in; null for a series typed by hand.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVolume))]
    public partial CvVolume? Volume { get; private set; }

    public bool HasVolume => Volume is not null;

    public bool HasIssues => _issues is { Issues.Count: > 0 };

    [ObservableProperty]
    public partial string? SeriesLink { get; set; }

    [ObservableProperty]
    public partial string? SeriesError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(AskApproveCommand), nameof(ApproveCommand), nameof(UseLinkCommand))]
    public partial string? BusyText { get; private set; }

    public bool IsIdle => BusyText is null;

    private GroupSeries? Series => string.IsNullOrWhiteSpace(SeriesName)
        ? null
        : new GroupSeries(SeriesName.Trim(), string.IsNullOrWhiteSpace(SeriesPublisher) ? null : SeriesPublisher.Trim(),
            int.TryParse(SeriesYear, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) ? year : null, Volume?.Id, Volume?.IssueCount ?? 0);

    private async Task OpenGroupAsync(ReviewGroup group)
    {
        Volumes.Clear();
        SeriesError = null;
        SeriesLink = null;
        KeepTitles = false;
        foreach (var option in VolumeOptions(group))
        {
            Volumes.Add(option);
        }

        // The series chosen before (books left without a match), else the volume most of the books found.
        var saved = Groups is { } groups ? await groups.GetSeriesAsync(group.Id) : null;
        if (saved is not null)
        {
            var volume = saved.VolumeId is { } id
                ? Volumes.FirstOrDefault(v => v.Volume.Id == id)?.Volume ?? new CvVolume(id, saved.Name, saved.StartYear, saved.Publisher, saved.IssueCount)
                : null;
            await SetSeriesAsync(volume, saved);
        }
        else if (Volumes.FirstOrDefault() is { } first)
        {
            await SetSeriesAsync(first.Volume, null);
        }
        else
        {
            await SetSeriesAsync(null, new GroupSeries(group.Name, null, null));
        }
    }

    private IEnumerable<VolumeOptionViewModel> VolumeOptions(ReviewGroup group)
    {
        var found = group.Books
            .SelectMany(b => (b.Identification.Result?.Candidates ?? []).Select((c, i) => (Book: b, Candidate: c, IsBest: i == 0)))
            .Where(x => x.Candidate.Record.ComicVineVolumeId is not null)
            .GroupBy(x => x.Candidate.Record.ComicVineVolumeId!.Value);
        return found
            .Select(g =>
            {
                var record = g.First().Candidate.Record;
                var name = record.VolumeName ?? NameFromSource(record.SourceName) ?? record.SeriesName ?? record.Title ?? string.Empty;
                var volume = new CvVolume(g.Key, name, record.VolumeStartYear, record.Publisher, record.VolumeIssueCount ?? 0);
                return (Volume: volume, Best: g.Where(x => x.IsBest).Select(x => x.Book.BookId).Distinct().Count(),
                    Any: g.Select(x => x.Book.BookId).Distinct().Count(), Cover: record.CoverUrl);
            })
            .OrderByDescending(x => x.Best)
            .ThenByDescending(x => x.Any)
            .ThenBy(x => x.Volume.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(12)
            .Select(x => new VolumeOptionViewModel(x.Volume, x.Best, group.Books.Count, x.Cover, v => _ = SetSeriesAsync(v.Volume, null, remember: true), LoadCoverAsync));
    }

    /// <summary>"Deadly Class #3 · Reagan Youth" → "Deadly Class" (results stored before the volume name was kept).</summary>
    private static string? NameFromSource(string? source)
    {
        if (source is null)
        {
            return null;
        }

        var head = source.Split(" · ")[0];
        var hash = head.LastIndexOf(" #", StringComparison.Ordinal);
        return hash < 0 ? head : head[..hash];
    }

    /// <summary>Chooses the series: a volume (its issues are read once), or a series typed by hand.</summary>
    private async Task SetSeriesAsync(CvVolume? volume, GroupSeries? series, bool remember = false)
    {
        if (Selected?.Group is not { } group)
        {
            return;
        }

        _settingSeries = true;
        try
        {
            Volume = volume;
            SeriesName = series?.Name ?? volume?.Name;
            SeriesPublisher = series?.Publisher ?? volume?.Publisher;
            SeriesYear = (series?.StartYear ?? volume?.StartYear)?.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _settingSeries = false;
        }

        foreach (var option in Volumes)
        {
            option.IsSelected = option.Volume.Id == volume?.Id;
        }

        SeriesError = null;
        _issues = null;
        if (volume is not null)
        {
            _loading?.Cancel();
            using var loading = _loading = new CancellationTokenSource();
            BusyText = string.Format(CultureInfo.CurrentCulture, Strings.Series_LoadingVolume, volume.Name);
            try
            {
                _issues = await _matcher.GetIssuesAsync(volume, loading.Token);
            }
            catch (OperationCanceledException) when (loading.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is MetadataSourceException or HttpRequestException)
            {
                _logger.LogWarning(ex, "Could not read the issues of volume {VolumeId}", volume.Id);
                SeriesError = Strings.Series_VolumeFailed;
            }
            finally
            {
                if (_loading == loading)
                {
                    _loading = null;
                    BusyText = null;
                }
            }
        }

        if (remember && Series is { } chosen && Groups is { } groups)
        {
            await groups.SetSeriesAsync(group.Id, chosen);
        }

        BuildRows(group);
    }

    partial void OnSeriesNameChanged(string? value) => SeriesTyped();

    partial void OnSeriesPublisherChanged(string? value) => SeriesTyped();

    partial void OnSeriesYearChanged(string? value) => SeriesTyped();

    /// <summary>Typed by hand: the volume (if any) still gives the records; the titles follow the name.</summary>
    private void SeriesTyped()
    {
        if (!_settingSeries && Selected?.Group is { } group)
        {
            BuildRows(group);
        }
    }

    /// <summary>No ComicVine volume: each book keeps its own best candidate.</summary>
    [RelayCommand]
    private Task TypeByHandAsync() => SetSeriesAsync(null, Series ?? new GroupSeries(Selected?.Name ?? string.Empty, null, null), remember: true);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task UseLinkAsync(CancellationToken ct)
    {
        SeriesError = null;
        if (BookIdentifier.ParseComicVineLink(SeriesLink) is null)
        {
            SeriesError = Strings.Review_LinkInvalid;
            return;
        }

        BusyText = Strings.Review_LoadingLink;
        CvVolume? volume;
        try
        {
            volume = await _matcher.FromLinkAsync(SeriesLink, ct);
        }
        catch (Exception ex) when (ex is MetadataSourceException or HttpRequestException)
        {
            _logger.LogWarning(ex, "Could not load the ComicVine link");
            SeriesError = Strings.Series_VolumeFailed;
            return;
        }
        finally
        {
            BusyText = null;
        }

        if (volume is null)
        {
            SeriesError = Strings.Review_LinkNotFound;
            return;
        }

        await SetSeriesAsync(volume, null, remember: true);
    }

    [RelayCommand]
    private Task OpenComicVineAsync() =>
        _urls.OpenAsync(new Uri("https://comicvine.gamespot.com/search/?i=volume&q=" + Uri.EscapeDataString(SeriesName ?? Selected?.Name ?? string.Empty)));

    // ---- 2. Title format ----

    [ObservableProperty]
    public partial string Template { get; set; }

    [ObservableProperty]
    public partial bool KeepTitles { get; set; }

    [ObservableProperty]
    public partial string? PreviewText { get; private set; }

    [ObservableProperty]
    public partial string? TemplateError { get; private set; }

    partial void OnTemplateChanged(string value)
    {
        if (Selected?.Group is { } group)
        {
            BuildRows(group);
        }
    }

    partial void OnKeepTitlesChanged(bool value)
    {
        if (Selected?.Group is { } group)
        {
            BuildRows(group);
        }
    }

    // ---- 3. Books ----

    public ObservableCollection<SeriesRowViewModel> Rows { get; } = [];

    /// <summary>Records chosen by the user ("Choose issue"), by book, until the group changes.</summary>
    private readonly Dictionary<string, MetadataCandidate> _chosen = [];

    [ObservableProperty]
    public partial string? SummaryText { get; private set; }

    private void BuildRows(ReviewGroup group)
    {
        if (Rows.Count > 0 && !group.Books.Select(b => b.BookId).SequenceEqual(Rows.Select(r => r.BookId)))
        {
            _chosen.Clear();
        }

        var unknown = TitleTemplate.UnknownFields(Template ?? string.Empty);
        TemplateError = unknown.Count == 0 ? null : string.Format(CultureInfo.CurrentCulture, Strings.Series_UnknownField, string.Join(", ", unknown));
        var series = Series ?? new GroupSeries(group.Name, null, null);
        var template = KeepTitles || string.IsNullOrWhiteSpace(Template) ? null : Template;
        var issues = _issues ?? new VolumeIssues(Volume ?? new CvVolume(0, series.Name, null, null, 0), []);
        var plans = group.Books.Select(b =>
        {
            var match = _chosen.TryGetValue(b.BookId, out var chosen)
                ? new VolumeBookMatch(b.BookId, VolumeMatchKind.Chosen, chosen)
                : VolumeMatcher.Match(issues, new VolumeBook(b.Book.Metadata, b.Number, b.Best));
            return SeriesPlan.Row(b, match, series, template);
        }).ToList();

        var same = Rows.Select(r => r.BookId).SequenceEqual(plans.Select(p => p.BookId));
        if (!same)
        {
            Rows.Clear();
        }

        for (var i = 0; i < plans.Count; i++)
        {
            if (same)
            {
                Rows[i].Update(plans[i]);
            }
            else
            {
                var row = new SeriesRowViewModel(plans[i], this);
                Rows.Add(row);
                _ = row.LoadCoverAsync();
                _ = row.LoadBookCoverAsync();
            }
        }

        PreviewText = plans.FirstOrDefault(p => p.NewTitle is not null)?.NewTitle is { } title
            ? string.Format(CultureInfo.CurrentCulture, Strings.Series_Preview, title)
            : null;
        SummaryText = string.Format(CultureInfo.CurrentCulture, Strings.Series_Summary, plans.Count(p => p.Candidate is not null), plans.Count);
        ConfirmText = null;
        AskApproveCommand.NotifyCanExecuteChanged();
        ApproveCommand.NotifyCanExecuteChanged();
    }

    public IReadOnlyList<IssueOptionViewModel> IssueOptionsFor(SeriesRowViewModel row) =>
        _issues is { } issues
            ? issues.Issues.Select(i => new IssueOptionViewModel(i, picked => ChooseIssue(row, picked))).ToList()
            : [];

    private void ChooseIssue(SeriesRowViewModel row, CvIssueSummary issue)
    {
        if (_issues is { } issues && Selected?.Group is { } group)
        {
            _chosen[row.BookId] = VolumeMatcher.CandidateFor(issue, issues.Volume, row.Plan.Book.Book.Metadata);
            BuildRows(group);
        }
    }

    public Task<Bitmap?> LoadCoverAsync(string? url) => LoadRemoteCoverAsync(url);

    public async Task<Bitmap?> LoadBookCoverAsync(Library.Index.CatalogBook book) => _session.Current is { } library
        ? await _covers.LoadAsync(Path.Combine(library.RootPath, book.Folder, Library.LibraryLayout.CoverFileName), CoverWidth)
        : null;

    private async Task<Bitmap?> LoadRemoteCoverAsync(string? url) =>
        url is not null && await _remoteCovers.GetAsync(url) is { } path ? await _covers.LoadAsync(path, CoverWidth) : null;

    public void OpenBook(SeriesRowViewModel row)
    {
        if (_session.Current is { } library && row.Plan.Book.Book is { MainFile: { } file } book)
        {
            _reader.Open(ReaderRequest.For(library, book, file));
        }
    }

    public void EditByHand(SeriesRowViewModel row) => _editBook(row.BookId);

    public async Task RemoveAsync(SeriesRowViewModel row)
    {
        if (Groups is { } groups)
        {
            await groups.RemoveAsync([row.BookId]);
            await RefreshAsync();
        }
    }

    // ---- Approval ----

    /// <summary>"Approve 6 books? 2 change volume, 1 without a match stays in the Review": the table is the dry run.</summary>
    [ObservableProperty]
    public partial string? ConfirmText { get; private set; }

    private bool CanApprove => IsIdle && Series is not null && Rows.Any(r => r.HasMatch);

    [RelayCommand(CanExecute = nameof(CanApprove))]
    private void AskApprove() => ConfirmText = string.Format(CultureInfo.CurrentCulture, Strings.Series_Confirm,
        Rows.Count(r => r.HasMatch), Rows.Count(r => r.Plan.ChangesVolume), Rows.Count(r => r.IsNoMatch));

    [RelayCommand]
    private void CancelApprove() => ConfirmText = null;

    /// <summary>The last approved group, while it can be undone from here.</summary>
    [ObservableProperty]
    public partial string? ApprovedText { get; private set; }

    private string? _lastBatchId;

    [RelayCommand(CanExecute = nameof(CanApprove))]
    private async Task ApproveAsync(CancellationToken ct)
    {
        ConfirmText = null;
        if (Selected?.Group is not { } group || Series is not { } series || _session.Identification is not { } queue || Groups is not { } groups)
        {
            return;
        }

        var plans = Rows.Select(r => r.Plan).ToList();
        var total = plans.Count(p => p.Candidate is not null);
        var index = GroupItems.IndexOf(Selected);
        BusyText = string.Format(CultureInfo.CurrentCulture, Strings.Series_Approving, 0, total);
        HistoryBatchResult result;
        try
        {
            // The books left without a match stay in the group, with the series already chosen.
            await groups.SetSeriesAsync(group.Id, series, ct);
            var noMatch = plans.Where(p => p.Candidate is null).Select(p => p.BookId).ToList();
            if (noMatch.Count > 0)
            {
                await groups.PinAsync(noMatch, group.Id, ct);
            }

            var batch = await queue.ApproveGroupAsync(series.Name, series, plans,
                new Progress<int>(done => BusyText = string.Format(CultureInfo.CurrentCulture, Strings.Series_Approving, done, total)), ct);
            result = new HistoryBatchResult(batch?.Id, batch?.Entries.Count ?? 0);
        }
        finally
        {
            BusyText = null;
        }

        if (!string.Equals(Template, _settings.Current.Review.TitleTemplate, StringComparison.Ordinal) && !KeepTitles)
        {
            await _settings.SaveAsync(_settings.Current with { Review = _settings.Current.Review with { TitleTemplate = Template } }, ct);
        }

        _lastBatchId = result.BatchId;
        ApprovedText = result.BatchId is null ? null : string.Format(CultureInfo.CurrentCulture, Strings.Series_Approved, result.Count, group.Name);
        UndoCommand.NotifyCanExecuteChanged();

        // The next group opens.
        await RefreshAsync(GroupItems.ElementAtOrDefault(index + 1)?.Group?.Id);
    }

    private sealed record HistoryBatchResult(string? BatchId, int Count);

    private bool CanUndo => _lastBatchId is not null;

    /// <summary>"Undo": the whole group goes back to the Review.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync(CancellationToken ct)
    {
        if (_lastBatchId is not { } batchId || _session.Identification is not { } queue)
        {
            return;
        }

        _lastBatchId = null;
        UndoCommand.NotifyCanExecuteChanged();
        var restored = await queue.UndoBatchAsync(batchId, ct);
        ApprovedText = string.Format(CultureInfo.CurrentCulture, Strings.Series_Undone, restored.Count);
        await RefreshAsync();
    }

    [RelayCommand]
    private void DismissApproved()
    {
        ApprovedText = null;
        _lastBatchId = null;
        UndoCommand.NotifyCanExecuteChanged();
    }
}
