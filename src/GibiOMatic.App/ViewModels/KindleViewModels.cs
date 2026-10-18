using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
using GibiOMatic.Conversion.Kcc;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Settings;
using GibiOMatic.Destinations;
using GibiOMatic.Destinations.Kindle;
using GibiOMatic.Destinations.Sync;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.App.ViewModels;

/// <summary>A Kindle model in "Modelo", and the KCC profile it uses.</summary>
public sealed record KindleModel(string Profile, string Name, bool IsColor);

/// <summary>"1 livro" or "3 livros" in the Kindle's texts.</summary>
internal static class Plural
{
    public static string Format(int count, string one, string many, params object[] more) =>
        string.Format(CultureInfo.CurrentCulture, count == 1 ? one : many, [count, .. more]);
}

public static class KindleModels
{
    public static IReadOnlyList<KindleModel> All { get; } =
    [
        new("KCS", "Kindle Colorsoft", true),
        new("KSCS", "Kindle Scribe Colorsoft", true),
        new("KPW6", "Kindle Paperwhite (2024)", false),
        new("KPW5", "Kindle Paperwhite (2021)", false),
        new("K11", "Kindle (2022)", false),
        new("KS", "Kindle Scribe (2022, 2024)", false),
        new("KS3", "Kindle Scribe (2025)", false),
        new("KO", "Kindle Oasis", false),
    ];

    public static KindleModel Find(string? profile) =>
        All.FirstOrDefault(m => string.Equals(m.Profile, profile, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}

/// <summary>A choice of an advanced KCC option; <see cref="Value"/> null = KCC's default.</summary>
public sealed record KccChoice(int? Value, string Label);

public enum KindleTab
{
    Send,
    Device,
    Settings,
    History,
}

public enum KindleListFilter
{
    /// <summary>The books in the Kindle's list ("Marcados").</summary>
    Chosen,

    /// <summary>Already on the device.</summary>
    Sent,
}

/// <summary>The fields of a Kindle destination, shared by the wizard and the detail's settings.</summary>
public sealed partial class KindleFormViewModel : ViewModelBase
{
    private readonly KccInstaller _installer;
    private readonly IFilePicker _files;
    private readonly IUiThread _ui;
    private readonly ILogger _logger;

    public KindleFormViewModel(DestinationConfig config, KccInstaller installer, IFilePicker files, IUiThread ui, ILogger logger, string toolsDirectory)
    {
        _installer = installer;
        _files = files;
        _ui = ui;
        _logger = logger;
        ToolsDirectory = toolsDirectory;
        Name = config.Name;
        Folder = config.Folder;
        SelectedModel = KindleModels.Find(config.KindleProfile);
        Color = config.KindleColor;
        Manga = config.KindleManga;
        Splitter = Splitters.First(c => c.Value == config.KindleSplitter);
        Cropping = Croppings.First(c => c.Value == config.KindleCropping);
        ExtraArguments = config.KccArguments;
        KccPath = config.KccPath;
        UpdateKcc();
    }

    private string ToolsDirectory { get; }

    public IReadOnlyList<KindleModel> Models => KindleModels.All;

    public IReadOnlyList<KccChoice> Splitters { get; } =
    [
        new(null, Strings.Kindle_SplitDefault), new(0, Strings.Kindle_SplitSplit), new(1, Strings.Kindle_SplitRotate), new(2, Strings.Kindle_SplitBoth),
    ];

    public IReadOnlyList<KccChoice> Croppings { get; } =
    [
        new(null, Strings.Kindle_CropDefault), new(0, Strings.Kindle_CropNone), new(1, Strings.Kindle_CropMargins), new(2, Strings.Kindle_CropNumbers),
    ];

    [ObservableProperty]
    public partial string Name { get; set; }

    /// <summary>Where prepared copies wait for the device (per machine, never in the Drive).</summary>
    public string? Folder { get; }

    [ObservableProperty]
    public partial KindleModel SelectedModel { get; set; }

    partial void OnSelectedModelChanged(KindleModel value)
    {
        // A black-and-white Kindle gets grey pages; a colour one keeps the colours.
        if (value is not null)
        {
            Color = value.IsColor;
        }
    }

    [ObservableProperty]
    public partial bool Color { get; set; }

    [ObservableProperty]
    public partial bool Manga { get; set; }

    [ObservableProperty]
    public partial KccChoice Splitter { get; set; }

    [ObservableProperty]
    public partial KccChoice Cropping { get; set; }

    [ObservableProperty]
    public partial string? ExtraArguments { get; set; }

    [ObservableProperty]
    public partial bool ShowAdvanced { get; set; }

    /// <summary>Chosen by hand; null = the one the app downloaded, or one on the PATH.</summary>
    [ObservableProperty]
    public partial string? KccPath { get; set; }

    /// <summary>"KCC encontrado: …" or what is missing.</summary>
    [ObservableProperty]
    public partial string? KccStatus { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadKcc))]
    public partial bool HasKcc { get; private set; }

    /// <summary>KindleGen (from the Kindle Previewer) was not found where the KCC looks for it.</summary>
    [ObservableProperty]
    public partial bool KindleGenMissing { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadKcc))]
    public partial bool IsDownloading { get; private set; }

    [ObservableProperty]
    public partial double DownloadProgress { get; private set; }

    public bool CanDownloadKcc => KccInstaller.IsAvailable && !HasKcc && !IsDownloading;

    public string DownloadText { get; } = string.Format(CultureInfo.CurrentCulture, Strings.Kindle_DownloadKcc, KccInstaller.Version,
        QualityText.SizeOf(KccInstaller.DownloadBytes));

    partial void OnKccPathChanged(string? value) => UpdateKcc();

    /// <summary>The KCC was installed or chosen (the detail then prepares the waiting books).</summary>
    public event EventHandler? KccChanged;

    private void UpdateKcc()
    {
        var tool = KccTool.Locate(KccPath, ToolsDirectory);
        HasKcc = tool is not null;
        KindleGenMissing = OperatingSystem.IsWindows() && KccTool.KindleGenDirectory() is null;
        KccStatus = tool is not null
            ? string.Format(CultureInfo.CurrentCulture, Strings.Kindle_KccFound, tool)
            : KccPath is not null ? string.Format(CultureInfo.CurrentCulture, Strings.Kindle_KccMissingAt, KccPath) : Strings.Kindle_KccMissing;
    }

    [RelayCommand]
    private async Task ChooseKccAsync(CancellationToken ct)
    {
        if (await _files.PickExecutableAsync(Strings.Kindle_ChooseKccTitle, ct) is { } path)
        {
            KccPath = path;
            KccChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The official release, checked against its SHA-256 (the app does not ship the KCC).</summary>
    [RelayCommand]
    private async Task DownloadKccAsync(CancellationToken ct)
    {
        IsDownloading = true;
        KccStatus = Strings.Kindle_Downloading;
        try
        {
            await _installer.InstallAsync(new UiProgress(_ui, p => DownloadProgress = p), ct);
            KccPath = null;
            UpdateKcc();
            KccChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Downloading the KCC failed");
            KccStatus = string.Format(CultureInfo.CurrentCulture, Strings.Kindle_DownloadFailed, ex.Message);
        }
        finally
        {
            IsDownloading = false;
        }
    }

    public bool IsValid => !string.IsNullOrWhiteSpace(Name);

    public DestinationConfig ToConfig(DestinationConfig current) => current with
    {
        Name = Name.Trim(),
        KindleProfile = SelectedModel.Profile,
        KindleColor = Color,
        KindleManga = Manga,
        KindleSplitter = Splitter.Value,
        KindleCropping = Cropping.Value,
        KccArguments = string.IsNullOrWhiteSpace(ExtraArguments) ? null : ExtraArguments.Trim(),
        KccPath = string.IsNullOrWhiteSpace(KccPath) ? null : KccPath.Trim(),
    };

    private sealed class UiProgress(IUiThread ui, Action<double> report) : IProgress<double>
    {
        public void Report(double value) => ui.Post(() => report(value));
    }
}

/// <summary>A book in the Kindle's "Enviar" list.</summary>
public sealed partial class KindleBookRowViewModel : ViewModelBase
{
    private readonly Action<KindleBookRowViewModel, bool> _choose;
    private bool _loading = true;

    public KindleBookRowViewModel(BookMetadata book, bool chosen, KindleBookStatus? status, bool preparing, Action<KindleBookRowViewModel, bool> choose)
    {
        BookId = book.Id;
        Title = book.Title;
        MetaLine = string.Join(" · ", new[] { book.Publisher, book.Date?.Year.ToString(CultureInfo.InvariantCulture), book.Series is { } s ? QualityText.SeriesOf(s) : null }
            .Where(p => !string.IsNullOrEmpty(p)));
        SearchText = BookCardViewModel.Normalize(string.Join(' ', book.Title, book.Series?.Name, book.Publisher));
        Status = chosen ? status : null;
        _choose = choose;
        IsChosen = chosen;
        (StateText, StateKind) = Describe(chosen, status, preparing);
        _loading = false;
    }

    public string BookId { get; }
    public string Title { get; }
    public string MetaLine { get; }
    internal string SearchText { get; }
    public KindleBookStatus? Status { get; }
    public string? StateText { get; }
    public StatusKind StateKind { get; }

    public bool IsReady => Status?.State is KindleBookState.Ready or KindleBookState.ReadyToReplace;

    public bool IsSent => Status?.State is KindleBookState.Sent or KindleBookState.ReadyToReplace;

    /// <summary>In the Kindle's list: unticking takes it out (nothing is deleted from the device).</summary>
    [ObservableProperty]
    public partial bool IsChosen { get; set; }

    partial void OnIsChosenChanged(bool value)
    {
        if (!_loading)
        {
            _choose(this, value);
        }
    }

    private static (string?, StatusKind) Describe(bool chosen, KindleBookStatus? status, bool preparing) => (chosen, status?.State) switch
    {
        (false, _) => (null, StatusKind.Neutral),
        (_, KindleBookState.Ready) => (string.Format(CultureInfo.CurrentCulture, Strings.Kindle_StateReady, QualityText.SizeOf(status!.Bytes ?? 0)), StatusKind.Ok),
        (_, KindleBookState.ReadyToReplace) => (Strings.Kindle_StateReadyToReplace, StatusKind.Ok),
        (_, KindleBookState.Sent) => (string.Format(CultureInfo.CurrentCulture, Strings.Kindle_StateSent,
            status!.SentAt?.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)), StatusKind.Neutral),
        _ when preparing => (Strings.Kindle_StatePreparing, StatusKind.Busy),
        _ => (Strings.Kindle_StateWaiting, StatusKind.Warn),
    };
}

/// <summary>A book on the device, in the Kindle manager.</summary>
public sealed partial class KindleDeviceFileViewModel(KindleFile file, bool sentByApp) : ViewModelBase
{
    public KindleFile File { get; } = file;
    public string Name { get; } = file.Path;
    public string SizeText { get; } = QualityText.SizeOf(file.Bytes);
    public string? DateText { get; } = file.Modified?.ToString("d", CultureInfo.CurrentCulture);

    /// <summary>Sent by the app (other books on the device were put there some other way).</summary>
    public bool SentByApp { get; } = sentByApp;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

/// <summary>A line of the confirmation before sending.</summary>
public sealed record KindleSendRow(string Title, string DeviceFile, string KindText, bool IsBlocked);

/// <summary>
/// A Kindle destination opened (prototype DestinoKindle): the books of its list and their state,
/// sending to the connected device with confirmation, the device's books (deleted only by hand),
/// its settings and history.
/// </summary>
public sealed partial class KindleDetailViewModel : ViewModelBase, ISyncActivity
{
    private readonly DestinationService _service;
    private readonly KindleWatcher _watcher;
    private readonly IUiThread _ui;
    private readonly ILogger _logger;
    private readonly Func<DestinationConfig, Task<DestinationConfig>> _save;
    private readonly Func<DestinationConfig, CancellationToken, Task> _remove;
    private readonly Action _back;
    private readonly Func<string, Task> _openFolder;
    private List<KindleBookRowViewModel> _rows = [];
    private KindleSendPlan? _plan;

    public KindleDetailViewModel(DestinationConfig config, KindleFormViewModel form, DestinationService service, KindleWatcher watcher, IUiThread ui,
        ILogger logger, Func<DestinationConfig, Task<DestinationConfig>> save, Func<DestinationConfig, CancellationToken, Task> remove, Action back,
        Func<string, Task> openFolder)
    {
        Config = config;
        Form = form;
        _service = service;
        _watcher = watcher;
        _ui = ui;
        _logger = logger;
        _save = save;
        _remove = remove;
        _back = back;
        _openFolder = openFolder;
        form.KccChanged += async (_, _) =>
        {
            await SaveAsync(CancellationToken.None);
            _service.Refresh(Config.Id);
        };
        UpdateDevice();
    }

    public DestinationConfig Config { get; private set; }

    public KindleFormViewModel Form { get; }

    public string Title => Config.Name;

    private KindleDestination? Destination => _service.Find(Config.Id) as KindleDestination;

    // ---- Tabs ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSendTab), nameof(IsDeviceTab), nameof(IsSettingsTab), nameof(IsHistoryTab))]
    public partial KindleTab Tab { get; set; }

    public bool IsSendTab => Tab == KindleTab.Send;
    public bool IsDeviceTab => Tab == KindleTab.Device;
    public bool IsSettingsTab => Tab == KindleTab.Settings;
    public bool IsHistoryTab => Tab == KindleTab.History;

    [RelayCommand]
    private Task SetTabAsync(KindleTab tab)
    {
        Tab = tab;
        return tab == KindleTab.Device ? LoadDeviceAsync() : Task.CompletedTask;
    }

    // ---- Device ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyCanExecuteChangedFor(nameof(ShowSendPlanCommand))]
    public partial bool IsConnected { get; private set; }

    [ObservableProperty]
    public partial string? DeviceText { get; private set; }

    [ObservableProperty]
    public partial StatusKind DeviceKind { get; private set; }

    /// <summary>The watcher saw the Kindle come or go.</summary>
    public void UpdateDevice()
    {
        IsConnected = _watcher.Device is not null;
        DeviceKind = IsConnected ? StatusKind.Ok : StatusKind.Neutral;
        DeviceText = !_watcher.IsSupported
            ? Strings.Kindle_DeviceUnsupported
            : _watcher.Device is { } device ? string.Format(CultureInfo.CurrentCulture, Strings.Kindle_DeviceConnected, device.Name) : Strings.Kindle_DeviceAway;
        if (!IsConnected)
        {
            _plan = null;
            HasPlan = false;
            DeviceFiles.Clear();
        }
        else if (IsDeviceTab)
        {
            _ = LoadDeviceAsync();
        }
    }

    // ---- Send tab: the list ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChosenFilter), nameof(IsSentFilter))]
    public partial KindleListFilter Filter { get; set; }

    public bool IsChosenFilter => Filter == KindleListFilter.Chosen;
    public bool IsSentFilter => Filter == KindleListFilter.Sent;

    [RelayCommand]
    private void SetFilter(KindleListFilter filter) => Filter = filter;

    partial void OnFilterChanged(KindleListFilter value) => ApplyFilter();

    [ObservableProperty]
    public partial string? SearchText { get; set; }

    partial void OnSearchTextChanged(string? value) => ApplyFilter();

    public ObservableCollection<KindleBookRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial string? EmptyText { get; private set; }

    [ObservableProperty]
    public partial int ChosenCount { get; private set; }

    [ObservableProperty]
    public partial int SentCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyCanExecuteChangedFor(nameof(ShowSendPlanCommand))]
    public partial int ReadyCount { get; private set; }

    /// <summary>"3 prontos para enviar · cerca de 120 MB".</summary>
    [ObservableProperty]
    public partial string? ReadyText { get; private set; }

    public bool CanSend => IsConnected && ReadyCount > 0 && !IsSending;

    /// <summary>Reads the list and the state of each book (after a change, a run or a send).</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (Destination is not { } destination || _service.Find(Config.Id)?.Config is not { } config)
        {
            return;
        }

        Config = config;
        try
        {
            var index = (await _service.Kindle.StatusAsync(destination, ct));
            var books = await IndexBooksAsync(ct);
            var preparing = _service.Current is { } current && current.DestinationId == Config.Id ? current.Title : null;
            // Books enter the list from the Library (right-click or the panel); unticking one here takes it out.
            _rows = books
                .Where(b => config.ChosenBookIds.Contains(b.Id) && b.Kind != BookKind.Book && b.Files.Any(f => f.Role == FileRole.Original))
                .Select(b => new KindleBookRowViewModel(b, config.ChosenBookIds.Contains(b.Id), index.GetValueOrDefault(b.Id),
                    preparing is not null && b.Title == preparing, ChooseBook))
                .OrderBy(r => r.Title, Conversion.NaturalStringComparer.Instance)
                .ToList();
            ChosenCount = _rows.Count(r => r.IsChosen);
            SentCount = _rows.Count(r => r.IsChosen && r.IsSent);
            var ready = _rows.Where(r => r.IsChosen && r.IsReady).ToList();
            ReadyCount = ready.Count;
            ReadyText = ready.Count == 0
                ? null
                : Plural.Format(ready.Count, Strings.Kindle_ReadySummaryOne, Strings.Kindle_ReadySummary, QualityText.SizeOf(ready.Sum(r => r.Status?.Bytes ?? 0)));
            ApplyFilter();
        }
        catch (Exception ex) when (LibrarySession.IsExpectedFailure(ex))
        {
            _logger.LogError(ex, "Could not read the Kindle list");
        }
    }

    /// <summary>Set by the page: reads the books of the library.</summary>
    public Func<CancellationToken, Task<IReadOnlyList<BookMetadata>>> IndexBooksAsync { get; init; } = _ => Task.FromResult<IReadOnlyList<BookMetadata>>([]);

    private void ApplyFilter()
    {
        var search = string.IsNullOrWhiteSpace(SearchText) ? null : BookCardViewModel.Normalize(SearchText.Trim());
        Rows.Clear();
        foreach (var row in _rows.Where(r => Filter switch
                 {
                     KindleListFilter.Sent => r.IsSent,
                     _ => true,
                 } && (search is null || r.SearchText.Contains(search, StringComparison.Ordinal))))
        {
            Rows.Add(row);
        }

        EmptyText = Rows.Count > 0 ? null : Filter switch
        {
            KindleListFilter.Chosen when search is null => Strings.Kindle_EmptyList,
            KindleListFilter.Sent when search is null => Strings.Kindle_EmptySent,
            _ => Strings.Library_NoResults,
        };
    }

    /// <summary>Ticking a book puts it in the list (it is prepared right away); unticking takes it out.</summary>
    private void ChooseBook(KindleBookRowViewModel row, bool chosen) => _ = ChooseBookAsync(row.BookId, chosen);

    private async Task ChooseBookAsync(string bookId, bool chosen)
    {
        var ids = chosen ? Config.ChosenBookIds.Append(bookId).Distinct().ToList() : Config.ChosenBookIds.Where(id => id != bookId).ToList();
        Config = await _save(Config with { ChosenBookIds = ids });
        _service.OnBookChanged(bookId);
        await RefreshAsync();
    }

    // ---- Send tab: sending ----

    [ObservableProperty]
    public partial bool HasPlan { get; private set; }

    [ObservableProperty]
    public partial string? PlanText { get; private set; }

    public ObservableCollection<KindleSendRow> PlanRows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyCanExecuteChangedFor(nameof(ShowSendPlanCommand))]
    public partial bool IsSending { get; private set; }

    [ObservableProperty]
    public partial string? SendProgressText { get; private set; }

    [ObservableProperty]
    public partial double SendProgressValue { get; private set; }

    [ObservableProperty]
    public partial string? SendResultText { get; private set; }

    [ObservableProperty]
    public partial bool SendResultIsError { get; private set; }

    /// <summary>What would be copied (new, replacing, or blocked by a file the app did not send), before anything is.</summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task ShowSendPlanAsync(CancellationToken ct)
    {
        if (Destination is not { } destination)
        {
            return;
        }

        SendResultText = null;
        try
        {
            _plan = await Task.Run(async () =>
            {
                using var device = _watcher.Connect();
                return await _service.Kindle.PlanAsync(destination, device, ct);
            }, ct);
        }
        catch (KindleDeviceException ex)
        {
            ShowSendError(ex.Message);
            return;
        }

        PlanRows.Clear();
        foreach (var item in _plan.Items)
        {
            PlanRows.Add(new KindleSendRow(item.Title, item.DeviceFile, item.Kind switch
            {
                KindleSendKind.New => QualityText.SizeOf(item.Bytes),
                KindleSendKind.Replace => Strings.Kindle_PlanReplace,
                _ => Strings.Kindle_PlanBlocked,
            }, item.Kind == KindleSendKind.Blocked));
        }

        PlanText = _plan.ToSend.Count == 0
            ? Strings.Kindle_PlanNothing
            : Plural.Format(_plan.ToSend.Count, Strings.Kindle_PlanSummaryOne, Strings.Kindle_PlanSummary, QualityText.SizeOf(_plan.Bytes));
        HasPlan = true;
    }

    [RelayCommand]
    private void CancelSend()
    {
        _plan = null;
        HasPlan = false;
    }

    [RelayCommand]
    private async Task ConfirmSendAsync(CancellationToken ct)
    {
        if (_plan is not { } plan || Destination is not { } destination)
        {
            return;
        }

        HasPlan = false;
        IsSending = true;
        SendProgressValue = 0;
        try
        {
            var progress = new UiProgress<KindleSendStep>(_ui, step =>
            {
                SendProgressValue = step.Total == 0 ? 1 : (step.Done + step.FileFraction) / step.Total;
                SendProgressText = step.Done >= step.Total
                    ? Strings.Sync_Finishing
                    : string.Format(CultureInfo.CurrentCulture, Strings.Kindle_Sending, step.Title, step.Done + 1, step.Total);
            });
            var result = await Task.Run(async () =>
            {
                using var device = _watcher.Connect();
                return await _service.Kindle.SendAsync(destination, plan, device, progress, ct);
            }, ct);
            SendResultIsError = result.Failures.Count > 0;
            SendResultText = result.Failures.Count == 0
                ? Plural.Format(result.Sent, Strings.Kindle_SentDoneOne, Strings.Kindle_SentDone)
                : string.Format(CultureInfo.CurrentCulture, Strings.Kindle_SentWithErrors, result.Sent, result.Failures.Count,
                    string.Join("; ", result.Failures.Take(3).Select(f => $"{f.Title}: {f.Error}")));
        }
        catch (KindleDeviceException ex)
        {
            ShowSendError(ex.Message);
        }
        finally
        {
            IsSending = false;
            _plan = null;
            await RefreshAsync(CancellationToken.None);
            await LoadHistoryAsync();
            Sent?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Books went to the device (the shell's banner updates).</summary>
    public event EventHandler? Sent;

    private void ShowSendError(string message)
    {
        SendResultIsError = true;
        SendResultText = string.Format(CultureInfo.CurrentCulture, Strings.Kindle_DeviceError, message);
    }

    // ---- Device tab: the manager ----

    public ObservableCollection<KindleDeviceFileViewModel> DeviceFiles { get; } = [];

    [ObservableProperty]
    public partial string? DeviceSpaceText { get; private set; }

    [ObservableProperty]
    public partial string? DeviceMessage { get; private set; }

    [ObservableProperty]
    public partial bool DeviceMessageIsError { get; private set; }

    [ObservableProperty]
    public partial bool IsLoadingDevice { get; private set; }

    [ObservableProperty]
    public partial bool ConfirmingDelete { get; private set; }

    [ObservableProperty]
    public partial string? DeleteText { get; private set; }

    [RelayCommand]
    private Task RefreshDeviceAsync()
    {
        _watcher.Poll();
        UpdateDevice();
        return LoadDeviceAsync();
    }

    private async Task LoadDeviceAsync()
    {
        DeviceFiles.Clear();
        ConfirmingDelete = false;
        if (!IsConnected || Destination is not { } destination)
        {
            DeviceMessage = null;
            return;
        }

        IsLoadingDevice = true;
        try
        {
            var (files, space) = await Task.Run(() =>
            {
                using var device = _watcher.Connect();
                return (device.ListBooks(), device.Space());
            });
            var sent = KindleSender.SentFiles(destination);
            foreach (var file in files)
            {
                DeviceFiles.Add(new KindleDeviceFileViewModel(file, sent.Contains(file.Path)));
            }

            DeviceSpaceText = space is null ? null : string.Format(CultureInfo.CurrentCulture, Strings.Kindle_Space, QualityText.SizeOf(space.Free));
            DeviceMessageIsError = false;
            DeviceMessage = files.Count == 0 ? Strings.Kindle_DeviceEmpty : null;
        }
        catch (KindleDeviceException ex)
        {
            DeviceMessageIsError = true;
            DeviceMessage = string.Format(CultureInfo.CurrentCulture, Strings.Kindle_DeviceError, ex.Message);
        }
        finally
        {
            IsLoadingDevice = false;
        }
    }

    [RelayCommand]
    private void AskDelete()
    {
        var chosen = DeviceFiles.Where(f => f.IsChecked).ToList();
        if (chosen.Count == 0)
        {
            DeviceMessageIsError = false;
            DeviceMessage = Strings.Kindle_DeleteNothing;
            return;
        }

        DeleteText = Plural.Format(chosen.Count, Strings.Kindle_DeleteConfirmOne, Strings.Kindle_DeleteConfirm,
            string.Join(", ", chosen.Take(5).Select(f => f.Name)) + (chosen.Count > 5 ? "…" : string.Empty));
        ConfirmingDelete = true;
    }

    [RelayCommand]
    private void CancelDelete() => ConfirmingDelete = false;

    /// <summary>Deletes only what the user ticked; books the app had sent also leave the list.</summary>
    [RelayCommand]
    private async Task ConfirmDeleteAsync(CancellationToken ct)
    {
        ConfirmingDelete = false;
        var files = DeviceFiles.Where(f => f.IsChecked).Select(f => f.File).ToList();
        if (files.Count == 0 || Destination is not { } destination)
        {
            return;
        }

        try
        {
            var books = await Task.Run(async () =>
            {
                using var device = _watcher.Connect();
                return await _service.Kindle.DeleteAsync(destination, device, files, ct);
            }, ct);
            if (books.Count > 0)
            {
                Config = await _save(Config with { ChosenBookIds = Config.ChosenBookIds.Except(books).ToList() });
            }

            DeviceMessageIsError = false;
            DeviceMessage = Plural.Format(files.Count, Strings.Kindle_DeletedOne, Strings.Kindle_Deleted);
        }
        catch (KindleDeviceException ex)
        {
            DeviceMessageIsError = true;
            DeviceMessage = string.Format(CultureInfo.CurrentCulture, Strings.Kindle_DeviceError, ex.Message);
        }

        var message = (DeviceMessage, DeviceMessageIsError);
        await LoadDeviceAsync();
        (DeviceMessage, DeviceMessageIsError) = message;
        await RefreshAsync(CancellationToken.None);
        await LoadHistoryAsync();
    }

    // ---- Preparing (ISyncActivity) ----

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial string? ProgressText { get; set; }

    [ObservableProperty]
    public partial double ProgressValue { get; set; }

    [ObservableProperty]
    public partial double FileProgressValue { get; set; }

    [ObservableProperty]
    public partial string? FileProgressText { get; set; }

    [RelayCommand]
    private void PrepareAgain() => _service.Refresh(Config.Id);

    [RelayCommand]
    private Task OpenFolderAsync() => Config.Folder is { } folder ? _openFolder(folder) : Task.CompletedTask;

    // ---- History ----

    public ObservableCollection<HistoryRow> History { get; } = [];

    public async Task LoadHistoryAsync()
    {
        var history = await _service.HistoryAsync(Config.Id, 200);
        History.Clear();
        foreach (var entry in history)
        {
            History.Add(DestinationText.History(entry));
        }
    }

    // ---- Settings ----

    [ObservableProperty]
    public partial string? SaveMessage { get; private set; }

    [ObservableProperty]
    public partial bool ConfirmingRemove { get; private set; }

    [RelayCommand]
    private async Task SaveAsync(CancellationToken ct)
    {
        if (!Form.IsValid)
        {
            SaveMessage = Strings.Wizard_NeedNameAndFolder;
            return;
        }

        var before = Destination?.Options;
        Config = await _save(Form.ToConfig(Config));
        OnPropertyChanged(nameof(Title));
        SaveMessage = Strings.Destination_Saved;

        // Other KCC options change every copy: they are prepared again (the ones on the device are replaced when sent).
        if (Destination?.Options != before)
        {
            _service.Refresh(Config.Id);
        }

        await RefreshAsync(ct);
    }

    [RelayCommand]
    private void AskRemove() => ConfirmingRemove = true;

    [RelayCommand]
    private void CancelRemove() => ConfirmingRemove = false;

    [RelayCommand]
    private Task ConfirmRemoveAsync(CancellationToken ct) => _remove(Config, ct);

    [RelayCommand]
    private void Back() => _back();

    private sealed class UiProgress<T>(IUiThread ui, Action<T> report) : IProgress<T>
    {
        public void Report(T value) => ui.Post(() => report(value));
    }
}
