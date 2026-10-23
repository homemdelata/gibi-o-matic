using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
using GibiOMatic.Conversion.Images;
using GibiOMatic.Library;
using GibiOMatic.Library.Import;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.App.ViewModels;

public enum StatusKind
{
    Neutral,
    Busy,
    Ok,
    Warn,
    Bad,
    Duplicate,
}

public sealed partial class DecisionOptionViewModel(ImportDecision decision, string label, Action<ImportDecision> pick) : ViewModelBase
{
    public ImportDecision Decision { get; } = decision;
    public string Label { get; } = label;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Pick() => pick(Decision);
}

/// <summary>A file in the inbox: what it is, what will happen to it and how it went.</summary>
public sealed partial class InboxItemViewModel : ViewModelBase
{
    private readonly Func<long, ImportDecision, Task> _setDecision;
    private readonly ICoverLoader _covers;
    private readonly string _thumbnailPath;
    private bool _thumbnailRequested;
    private Bitmap? _thumbnail;

    public InboxItemViewModel(InboxEntry entry, string thumbnailPath, ICoverLoader covers, Func<long, ImportDecision, Task> setDecision)
    {
        Id = entry.Item.Id;
        FileName = Path.GetFileName(entry.Item.SourcePath);
        IsAuto = entry.Item.Auto;
        _thumbnailPath = thumbnailPath;
        _covers = covers;
        _setDecision = setDecision;
        Update(entry);
    }

    public long Id { get; }
    public string FileName { get; }

    /// <summary>Came from the watched folder: imported without confirmation.</summary>
    public bool IsAuto { get; }

    /// <summary>Where the file is now (it moves to _importados once processed).</summary>
    [ObservableProperty]
    public partial string SourcePath { get; private set; }

    public InboxEntry Entry { get; private set; } = null!;

    [ObservableProperty]
    public partial string FormatLabel { get; private set; } = "—";

    [ObservableProperty]
    public partial string SizeText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string? Title { get; private set; }

    [ObservableProperty]
    public partial string? MetaLine { get; private set; }

    [ObservableProperty]
    public partial string? NoteLine { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial StatusKind StatusKind { get; private set; }

    [ObservableProperty]
    public partial bool HasDecisions { get; private set; }

    [ObservableProperty]
    public partial double? Progress { get; set; }

    public ObservableCollection<DecisionOptionViewModel> Decisions { get; } = [];

    public Bitmap? Thumbnail
    {
        get
        {
            if (!_thumbnailRequested && Entry.Item.Analysis is { HasThumbnail: true })
            {
                _thumbnailRequested = true;
                _ = LoadThumbnailAsync();
            }

            return _thumbnail;
        }
    }

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(SourcePath))]
    public void Update(InboxEntry entry)
    {
        Entry = entry;
        SourcePath = entry.Item.SourcePath;
        var item = entry.Item;
        var a = item.Analysis;

        if (a is not null)
        {
            FormatLabel = QualityText.FormatLabel(a.Format);
            SizeText = QualityText.SizeOf(a.Bytes);
            Title = a.Title;
            var m = a.Metadata;
            var parts = new List<string>();
            if (m.Publisher is { } publisher)
            {
                parts.Add(publisher);
            }

            if (m.Date is { } date)
            {
                parts.Add(date.Year.ToString(CultureInfo.InvariantCulture));
            }

            if (m.Series is { } series)
            {
                parts.Add(m.Number is { } n ? $"{series} #{n}" : series);
            }

            var writers = m.Credits.Select(c => c.Name).Distinct().Take(3).ToList();
            if (writers.Count > 0)
            {
                parts.Add(string.Join(", ", writers));
            }

            MetaLine = parts.Count > 0 ? string.Join(" · ", parts) : Strings.Inbox_NoEmbeddedMetadata;
        }

        NoteLine = entry.Duplicate switch
        {
            DuplicateKind.SameFileInLibrary => string.Format(CultureInfo.CurrentCulture, Strings.Inbox_DupSameFileLibrary, entry.DuplicateTitle),
            DuplicateKind.SameFileInList => Strings.Inbox_DupSameFileList,
            DuplicateKind.SameBookInLibrary => string.Format(CultureInfo.CurrentCulture, Strings.Inbox_DupSameBookLibrary, entry.DuplicateTitle,
                a?.Match?.MainFormat is { } f ? QualityText.FormatLabel(f) : "?"),
            DuplicateKind.SameBookInList => string.Format(CultureInfo.CurrentCulture, Strings.Inbox_DupSameBookList, entry.DuplicateTitle),
            _ => a?.Metadata.LooksConverted == true ? Strings.Inbox_LooksConverted : null,
        };

        (StatusText, StatusKind) = item.State switch
        {
            InboxItemState.Pending => (Strings.Inbox_StatePending, StatusKind.Neutral),
            InboxItemState.Analyzing => (Strings.Inbox_StateAnalyzing, StatusKind.Busy),
            InboxItemState.Ready => ReadyStatus(entry),
            InboxItemState.Queued => (Strings.Inbox_StateQueued, StatusKind.Neutral),
            InboxItemState.Importing => (Strings.Inbox_StateImporting, StatusKind.Busy),
            InboxItemState.Imported => (Strings.Inbox_StateImported, StatusKind.Ok),
            InboxItemState.Skipped => (Reason(Strings.Inbox_StateSkipped, item.Message, item.MessageDetail), StatusKind.Neutral),
            _ => (Reason(Strings.Inbox_StateFailed, item.Message, item.MessageDetail), StatusKind.Bad),
        };

        if (item.State is not (InboxItemState.Analyzing or InboxItemState.Importing))
        {
            Progress = null;
        }

        // Only items waiting for confirmation with a real choice show the selector.
        var choices = item.State == InboxItemState.Ready && entry.Duplicate is DuplicateKind.SameBookInLibrary or DuplicateKind.SameBookInList
            ? entry.AllowedDecisions
            : [];
        if (!choices.SequenceEqual(Decisions.Select(d => d.Decision)))
        {
            Decisions.Clear();
            foreach (var decision in choices)
            {
                Decisions.Add(new DecisionOptionViewModel(decision, LabelOf(decision), d => _ = _setDecision(Id, d)));
            }
        }

        foreach (var option in Decisions)
        {
            option.IsSelected = option.Decision == entry.Decision;
        }

        HasDecisions = Decisions.Count > 0;

        // The analysis may have produced the thumbnail after the view first asked for it.
        if (!_thumbnailRequested && a is { HasThumbnail: true })
        {
            OnPropertyChanged(nameof(Thumbnail));
        }
    }

    private static (string, StatusKind) ReadyStatus(InboxEntry entry) => (entry.Duplicate, entry.Decision) switch
    {
        (DuplicateKind.SameFileInLibrary or DuplicateKind.SameFileInList, _) => (Strings.Inbox_StateAlreadyThere, StatusKind.Neutral),
        (_, ImportDecision.Skip) => (Strings.Inbox_StateWillSkip, StatusKind.Neutral),
        (not null, _) => (Strings.Inbox_StateDuplicate, StatusKind.Duplicate),
        _ => (Strings.Inbox_StateReady, StatusKind.Ok),
    };

    public static string LabelOf(ImportDecision decision) => decision switch
    {
        ImportDecision.Replace => Strings.Inbox_DecisionReplace,
        ImportDecision.KeepBoth => Strings.Inbox_DecisionKeepBoth,
        ImportDecision.Skip => Strings.Inbox_DecisionSkip,
        _ => Strings.Inbox_DecisionImport,
    };

    private static string Reason(string state, InboxMessage message, string? detail)
    {
        var reason = message switch
        {
            InboxMessage.NotFound => Strings.Inbox_ReasonNotFound,
            InboxMessage.UnsupportedFormat => Strings.Inbox_ReasonUnsupported,
            InboxMessage.SourceChanged => Strings.Inbox_ReasonSourceChanged,
            InboxMessage.AlreadyInLibrary => Strings.Inbox_ReasonAlreadyInLibrary,
            InboxMessage.DuplicateInList => Strings.Inbox_ReasonDuplicateInList,
            InboxMessage.SkippedByUser => null,
            InboxMessage.IoError => detail,
            _ => null,
        };
        return reason is null ? state : $"{state} · {reason}";
    }

    private async Task LoadThumbnailAsync()
    {
        _thumbnail = await _covers.LoadAsync(_thumbnailPath, CoverImages.ThumbnailWidth / 2);
        OnPropertyChanged(nameof(Thumbnail));
    }
}

/// <summary>
/// The inbox (SPEC §6, §10). Files added by picker or drag and drop are analysed in the background
/// and imported only after "Importar"; files arriving in the watched folder go through on their own (F5).
/// </summary>
public sealed partial class InboxPageViewModel : PageViewModel
{
    private readonly LibrarySession _session;
    private readonly IFilePicker _filePicker;
    private readonly IFolderPicker _folderPicker;
    private readonly ThumbnailCache _thumbnails;
    private readonly ICoverLoader _covers;
    private readonly IUiThread _ui;
    private readonly ILogger<InboxPageViewModel> _logger;
    private readonly Debouncer _refresh;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public InboxPageViewModel(LibrarySession session, IFilePicker filePicker, IFolderPicker folderPicker, ThumbnailCache thumbnails,
        ICoverLoader covers, IUiThread ui, ILogger<InboxPageViewModel> logger)
    {
        _session = session;
        _filePicker = filePicker;
        _folderPicker = folderPicker;
        _thumbnails = thumbnails;
        _covers = covers;
        _ui = ui;
        _logger = logger;
        _refresh = new Debouncer(ui, TimeSpan.FromMilliseconds(200), () => RefreshAsync());
        Subtitle = Strings.Inbox_Subtitle;
        if (session.Queue is { } queue)
        {
            queue.Changed += OnQueueChanged;
            queue.ProgressChanged += OnProgressChanged;
            IsPaused = queue.IsPaused;
        }

        session.WatcherChanged += OnWatcherChanged;
        UpdateWatch();
    }

    // ---- Watched folder (F5) ----

    [ObservableProperty]
    public partial InboxWatchStatus WatchStatus { get; private set; }

    [ObservableProperty]
    public partial string? WatchTitle { get; private set; }

    [ObservableProperty]
    public partial string? WatchText { get; private set; }

    [ObservableProperty]
    public partial string? WatchFolder { get; private set; }

    public bool IsWatching => WatchStatus == InboxWatchStatus.Watching;

    private void OnWatcherChanged(object? sender, EventArgs e) => _ui.Post(UpdateWatch);

    private void UpdateWatch()
    {
        var watcher = _session.Watcher;
        WatchStatus = watcher?.Status ?? InboxWatchStatus.Off;
        WatchFolder = watcher?.Folder;
        (WatchTitle, WatchText) = WatchStatus switch
        {
            InboxWatchStatus.Watching => (Strings.Inbox_WatchingTitle,
                string.Format(CultureInfo.CurrentCulture, Strings.Inbox_WatchingHelp, InboxFolder.ImportedFolderName)),
            InboxWatchStatus.Missing => (Strings.Inbox_WatchMissingTitle,
                string.Format(CultureInfo.CurrentCulture, Strings.Inbox_WatchMissing, WatchFolder)),
            _ => (Strings.Inbox_WatchOffTitle, Strings.Inbox_WatchOff),
        };
        OnPropertyChanged(nameof(IsWatching));
    }

    [RelayCommand]
    private void ConfigureInbox() => RequestNavigation(NavPage.Settings);

    public override string Title => Strings.Nav_Inbox;

    public ObservableCollection<InboxItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems), nameof(IsEmpty))]
    public partial ImportSummary Summary { get; private set; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>Imported files leave the list; they count only in the summary.</summary>
    public bool HasItems => Summary.Total - Summary.Imported > 0;

    public bool IsEmpty => !HasItems;

    [ObservableProperty]
    public partial string? ListHeader { get; private set; }

    [ObservableProperty]
    public partial string? ConfirmSummary { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand), nameof(SkipAllCommand))]
    public partial bool CanImport { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearFinishedCommand))]
    public partial bool HasFinished { get; private set; }

    /// <summary>Shown once the queue is done: how many were imported, skipped and failed.</summary>
    [ObservableProperty]
    public partial string? DoneSummary { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseLabel))]
    public partial bool IsPaused { get; private set; }

    public string PauseLabel => IsPaused ? Strings.Inbox_Resume : Strings.Inbox_Pause;

    [ObservableProperty]
    public partial bool IsWorking { get; private set; }

    [ObservableProperty]
    public partial string? ProgressText { get; private set; }

    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    public override Task OnNavigatedToAsync(CancellationToken ct = default) => RefreshAsync(ct);

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_session.Queue is not { } queue || _session.Current is not { } library)
        {
            return;
        }

        // A queue event and a user action may refresh at the same time; interleaved refreshes
        // would add the same file twice.
        await _refreshGate.WaitAsync(ct);
        try
        {
            var entries = (await queue.GetEntriesAsync(ct)).Where(e => e.Item.State != InboxItemState.Imported).ToList();
            var byId = Items.ToDictionary(i => i.Id);
            var ids = entries.Select(e => e.Item.Id).ToHashSet();
            for (var i = Items.Count - 1; i >= 0; i--)
            {
                if (!ids.Contains(Items[i].Id))
                {
                    Items.RemoveAt(i);
                }
            }

            foreach (var entry in entries)
            {
                if (byId.TryGetValue(entry.Item.Id, out var existing))
                {
                    existing.Update(entry);
                }
                else
                {
                    Items.Add(new InboxItemViewModel(entry, _thumbnails.InboxThumbnailPath(library, entry.Item.Id), _covers, (id, decision) => queue.SetDecisionAsync(id, decision)));
                }
            }

            Summary = await queue.GetSummaryAsync(ct);
            IsPaused = queue.IsPaused;
            UpdateTexts(entries);
        }
        catch (Exception ex) when (LibrarySession.IsExpectedFailure(ex))
        {
            _logger.LogError(ex, "Could not read the inbox");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void UpdateTexts(IReadOnlyList<InboxEntry> entries)
    {
        var s = Summary;
        ListHeader = !HasItems ? null : string.Format(CultureInfo.CurrentCulture, Strings.Inbox_ListHeader, s.Total - s.Imported, s.Waiting, s.Ready);

        var ready = entries.Where(e => e.Item.State == InboxItemState.Ready).ToList();
        CanImport = ready.Count > 0;
        ConfirmSummary = ready.Count == 0
            ? null
            : string.Format(CultureInfo.CurrentCulture, Strings.Inbox_ConfirmSummary,
                ready.Count(e => e.Decision == ImportDecision.Import),
                ready.Count(e => e.Decision == ImportDecision.Replace),
                ready.Count(e => e.Decision == ImportDecision.KeepBoth),
                ready.Count(e => e.Decision == ImportDecision.Skip));

        var finished = s.Imported + s.Skipped + s.Failed;
        HasFinished = finished > 0;
        DoneSummary = finished > 0 && !s.HasWork && s.Ready == 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.Inbox_DoneSummary, s.Imported, s.Skipped, s.Failed)
            : null;
        IsWorking = s.HasWork && !IsPaused;
        if (!IsWorking)
        {
            ProgressText = IsPaused && s.HasWork ? Strings.Inbox_PausedNote : null;
        }
    }

    [RelayCommand]
    private async Task AddFilesAsync(CancellationToken ct) =>
        await AddPathsAsync(await _filePicker.PickBooksAsync(Strings.Picker_ImportTitle, ct), ct);

    [RelayCommand]
    private async Task AddFolderAsync(CancellationToken ct)
    {
        var folder = await _folderPicker.PickFolderAsync(Strings.Picker_ImportFolderTitle, ct);
        if (folder is not null)
        {
            await AddPathsAsync([folder], ct);
        }
    }

    [RelayCommand]
    private Task DropFilesAsync(string[] paths, CancellationToken ct) => AddPathsAsync(paths, ct);

    private async Task AddPathsAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (paths.Count == 0 || _session.Queue is not { } queue)
        {
            return;
        }

        await queue.AddAsync(paths, ct);
        await RefreshAsync(ct);
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync(CancellationToken ct)
    {
        if (_session.Queue is { } queue)
        {
            await queue.ConfirmAsync(ct);
            await RefreshAsync(ct);
        }
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task SkipAllAsync(CancellationToken ct)
    {
        if (_session.Queue is { } queue)
        {
            await queue.SkipAllReadyAsync(ct);
            await RefreshAsync(ct);
        }
    }

    [RelayCommand(CanExecute = nameof(HasFinished))]
    private async Task ClearFinishedAsync(CancellationToken ct)
    {
        if (_session.Queue is { } queue)
        {
            await queue.ClearFinishedAsync(ct);
            await RefreshAsync(ct);
        }
    }

    [RelayCommand]
    private async Task TogglePauseAsync(CancellationToken ct)
    {
        if (_session.Queue is not { } queue)
        {
            return;
        }

        if (queue.IsPaused)
        {
            await queue.ResumeAsync(ct);
        }
        else
        {
            await queue.PauseAsync(ct);
        }

        await RefreshAsync(ct);
    }

    private void OnQueueChanged(object? sender, EventArgs e) => _refresh.Trigger();

    private void OnProgressChanged(object? sender, ImportProgress? progress) => _ui.Post(() =>
    {
        if (progress is null)
        {
            return;
        }

        IsWorking = true;
        ProgressValue = progress.Fraction;
        var phase = progress.Phase == InboxItemState.Analyzing ? Strings.Inbox_ProgressAnalyzing : Strings.Inbox_ProgressImporting;
        ProgressText = string.Format(CultureInfo.CurrentCulture, phase, progress.FileName, progress.Fraction);
        if (Items.FirstOrDefault(i => i.Id == progress.ItemId) is { } item)
        {
            item.Progress = progress.Fraction;
        }
    });

    public override void Dispose()
    {
        _session.WatcherChanged -= OnWatcherChanged;
        if (_session.Queue is { } queue)
        {
            queue.Changed -= OnQueueChanged;
            queue.ProgressChanged -= OnProgressChanged;
        }

        _refresh.Dispose();
    }
}
