using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
using GibiOMatic.Core;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Ids;
using GibiOMatic.Core.Secrets;
using GibiOMatic.Core.Settings;
using GibiOMatic.Destinations;
using GibiOMatic.Destinations.Komga;
using GibiOMatic.Destinations.Sync;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.App.ViewModels;

public enum DestinationsMode
{
    List,
    Wizard,
    Detail,
}

/// <summary>A destination card in the list (SPEC §12, Destinos).</summary>
public sealed partial class DestinationCardViewModel(DestinationSummary summary, SyncResult? lastResult, Action<DestinationCardViewModel, bool> open)
    : ViewModelBase, ISyncActivity
{
    public DestinationSummary Summary { get; } = summary;
    public string Id => Summary.Config.Id;
    public string Name => Summary.Config.Name;
    public string TypeLine => Strings.Destination_KomgaType;
    public string WhichText => Summary.Config.Which == DestinationScope.AllBooks ? Strings.Destination_AllBooks : Strings.Destination_ChosenBooks;
    public string WhenText => Summary.Config.When == DestinationTiming.Automatic ? Strings.Destination_Automatic : Strings.Destination_Manual;
    public string? Folder => Summary.Config.Folder;

    public StatusKind StatusKind => Summary.Status.Health != DestinationHealth.Ready || lastResult?.Failures.Count > 0
        ? StatusKind.Bad
        : !Summary.Confirmed || Summary.Pending > 0 ? StatusKind.Warn : StatusKind.Ok;

    public string StatusText => lastResult?.Failures.Count > 0 && Summary.Status.Health == DestinationHealth.Ready
        ? string.Format(CultureInfo.CurrentCulture, Strings.Destination_StatusLastErrors, lastResult.Failures.Count, DestinationText.Status(Summary))
        : DestinationText.Status(Summary);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial string? ProgressText { get; set; }

    [ObservableProperty]
    public partial double ProgressValue { get; set; }

    [RelayCommand]
    private void Configure() => open(this, false);

    private bool CanSyncNow => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanSyncNow))]
    private void SyncNow() => open(this, true);
}

/// <summary>What the card and the detail show while a destination is being updated.</summary>
public interface ISyncActivity
{
    bool IsRunning { get; set; }
    string? ProgressText { get; set; }
    double ProgressValue { get; set; }
}

/// <summary>A line of a destination's history.</summary>
public sealed record HistoryRow(string When, string Text, string? Detail, bool IsError);

/// <summary>Texts about destinations shared by the page, the sidebar and the book panel.</summary>
public static class DestinationText
{
    public static string Status(DestinationSummary s)
    {
        if (s.Status.Health == DestinationHealth.NotConfigured)
        {
            return Strings.Destination_StatusNoFolder;
        }

        if (s.Status.Health == DestinationHealth.Unavailable)
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.Destination_StatusUnavailable, s.Config.Folder);
        }

        if (!s.Confirmed)
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.Destination_StatusFirstSync, s.Included);
        }

        return s.Pending > 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.Destination_StatusPending, s.Pending, s.UpToDate)
            : string.Format(CultureInfo.CurrentCulture, Strings.Destination_StatusUpToDate, s.UpToDate);
    }

    /// <summary>"Gerando Saga v01 (3 de 10)…"; the last step is the server notice.</summary>
    public static void Report(ISyncActivity activity, SyncProgress progress)
    {
        activity.IsRunning = true;
        activity.ProgressValue = progress.Total == 0 ? 0 : (double)progress.Done / progress.Total;
        activity.ProgressText = progress.Done >= progress.Total || string.IsNullOrEmpty(progress.Title)
            ? Strings.Sync_Finishing
            : string.Format(CultureInfo.CurrentCulture, Strings.Sync_Progress, progress.Title, progress.Done + 1, progress.Total);
    }

    public static HistoryRow History(DestinationLogEntry e)
    {
        var when = e.At.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        var item = e.Path ?? e.Title;
        return e.Kind switch
        {
            DestinationLogKind.Created => new(when, Strings.History_Created, item, false),
            DestinationLogKind.Updated => new(when, Strings.History_Updated, item, false),
            DestinationLogKind.Deleted => new(when, Strings.History_Deleted, item, false),
            DestinationLogKind.Failed => new(when, string.Format(CultureInfo.CurrentCulture, Strings.History_Failed, item), e.Message, true),
            DestinationLogKind.ScanRequested => new(when, Strings.History_ScanRequested, null, false),
            DestinationLogKind.ScanFailed => new(when, Strings.History_ScanFailed, e.Message, true),
            _ => new(when, Finished(e.Message), null, e.Message?.EndsWith("/0", StringComparison.Ordinal) == false),
        };
    }

    private static string Finished(string? counts)
    {
        var parts = (counts ?? string.Empty).Split('/');
        return parts.Length == 3
            ? string.Format(CultureInfo.CurrentCulture, Strings.History_Finished, parts[0], parts[1], parts[2])
            : Strings.History_FinishedPlain;
    }

    public static string Copy(CopyState state) => state switch
    {
        CopyState.UpToDate => Strings.Copy_UpToDate,
        CopyState.Outdated => Strings.Copy_Outdated,
        CopyState.NotGenerated => Strings.Copy_NotGenerated,
        _ => Strings.Copy_NotIncluded,
    };
}

public sealed record DestinationTypeOption(string Id, string Name, string Help, bool IsAvailable, string? Tag);

/// <summary>The fields of a Komga destination, shared by the wizard and the detail.</summary>
public sealed partial class DestinationFormViewModel : ViewModelBase
{
    private readonly KomgaClient _komga;
    private readonly IFolderPicker _folders;
    private readonly BookMetadata _sample;

    public DestinationFormViewModel(DestinationConfig config, BookMetadata sample, KomgaClient komga, IFolderPicker folders)
    {
        _komga = komga;
        _folders = folders;
        _sample = sample;
        Name = config.Name;
        Folder = config.Folder;
        ServerUrl = config.ServerUrl;
        OneShotsFolder = config.OneShotsFolder;
        SeriesTemplate = config.SeriesTemplate ?? OutputNames.DefaultSeriesTemplate;
        OneShotTemplate = config.OneShotTemplate ?? OutputNames.DefaultOneShotTemplate;
        IncludeUnreviewed = config.IncludeUnreviewed;
        IncludeTextBooks = config.IncludeTextBooks;
        IsAllBooks = config.Which == DestinationScope.AllBooks;
        IsAutomatic = config.When == DestinationTiming.Automatic;
        _libraryId = config.KomgaLibraryId;
        UpdatePreview();
    }

    private string? _libraryId;

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string? Folder { get; set; }

    [ObservableProperty]
    public partial string? ServerUrl { get; set; }

    /// <summary>Typed by the user, stored in the secret store on save; never shown back.</summary>
    [ObservableProperty]
    public partial string? ApiKeyInput { get; set; }

    public ObservableCollection<KomgaLibrary> Libraries { get; } = [];

    [ObservableProperty]
    public partial KomgaLibrary? SelectedLibrary { get; set; }

    [ObservableProperty]
    public partial string? ServerStatus { get; private set; }

    [ObservableProperty]
    public partial bool ServerStatusIsError { get; private set; }

    [ObservableProperty]
    public partial string? OneShotsFolder { get; set; }

    [ObservableProperty]
    public partial string SeriesTemplate { get; set; }

    [ObservableProperty]
    public partial string OneShotTemplate { get; set; }

    [ObservableProperty]
    public partial bool IncludeUnreviewed { get; set; }

    [ObservableProperty]
    public partial bool IncludeTextBooks { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChosenBooks))]
    public partial bool IsAllBooks { get; set; }

    public bool IsChosenBooks
    {
        get => !IsAllBooks;
        set => IsAllBooks = !value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManual))]
    public partial bool IsAutomatic { get; set; }

    public bool IsManual
    {
        get => !IsAutomatic;
        set => IsAutomatic = !value;
    }

    [ObservableProperty]
    public partial bool ShowAdvanced { get; set; }

    [ObservableProperty]
    public partial string? Preview { get; private set; }

    [ObservableProperty]
    public partial string? OneShotPreview { get; private set; }

    /// <summary>Komga's one-shots folder differs from ours: one-shots would become normal series.</summary>
    [ObservableProperty]
    public partial string? OneShotsWarning { get; private set; }

    partial void OnOneShotsFolderChanged(string? value)
    {
        UpdatePreview();
        CheckOneShots();
    }

    partial void OnSeriesTemplateChanged(string value) => UpdatePreview();

    partial void OnOneShotTemplateChanged(string value) => UpdatePreview();

    partial void OnSelectedLibraryChanged(KomgaLibrary? value) => CheckOneShots();

    private void UpdatePreview()
    {
        var series = _sample.Series is null ? _sample with { Series = new SeriesInfo("Saga", "1"), OneShot = false } : _sample with { OneShot = false };
        Preview = OutputNames.KomgaPath(series, OneShotsFolder, Template(SeriesTemplate, OutputNames.DefaultSeriesTemplate)) + ".cbz";
        OneShotPreview = OutputNames.KomgaPath(series with { Series = null, OneShot = true, Title = "Plutona" }, OneShotsFolder, null,
            Template(OneShotTemplate, OutputNames.DefaultOneShotTemplate)) + ".cbz";
    }

    private static string? Template(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) || value == fallback ? null : value.Trim();

    private void CheckOneShots()
    {
        OneShotsWarning = SelectedLibrary is { } library && !string.Equals(library.OneShotsDirectory ?? string.Empty, OneShotsFolder ?? string.Empty, StringComparison.Ordinal)
            ? string.Format(CultureInfo.CurrentCulture, Strings.Destination_OneShotsMismatch, library.Name,
                string.IsNullOrEmpty(library.OneShotsDirectory) ? Strings.Destination_Empty : library.OneShotsDirectory)
            : null;
    }

    [RelayCommand]
    private async Task ChooseFolderAsync(CancellationToken ct)
    {
        if (await _folders.PickFolderAsync(Strings.Picker_DestinationFolder, ct) is { } folder)
        {
            Folder = folder;
        }
    }

    /// <summary>Lists the server's libraries (proves address and key) and checks the one-shots folder.</summary>
    [RelayCommand]
    private async Task TestServerAsync(CancellationToken ct)
    {
        ServerStatusIsError = false;
        ServerStatus = Strings.Destination_Testing;
        var key = ApiKeyInput ?? await StoredKeyAsync(ct);
        if (string.IsNullOrWhiteSpace(ServerUrl) || string.IsNullOrWhiteSpace(key))
        {
            ServerStatusIsError = true;
            ServerStatus = Strings.Destination_NeedUrlAndKey;
            return;
        }

        try
        {
            var libraries = await _komga.GetLibrariesAsync(ServerUrl.Trim(), key.Trim(), ct);
            Libraries.Clear();
            foreach (var library in libraries)
            {
                Libraries.Add(library);
            }

            SelectedLibrary = Libraries.FirstOrDefault(l => l.Id == _libraryId) ?? Libraries.FirstOrDefault();
            ServerStatus = string.Format(CultureInfo.CurrentCulture, Strings.Destination_ServerOk, libraries.Count);
        }
        catch (KomgaException ex)
        {
            ServerStatusIsError = true;
            ServerStatus = ex.Status is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                ? Strings.Destination_ServerKeyRejected
                : string.Format(CultureInfo.CurrentCulture, Strings.Destination_ServerError, ex.Message);
        }
    }

    /// <summary>Set by the owner: reads the key already stored for an existing destination.</summary>
    public Func<CancellationToken, Task<string?>> StoredKeyAsync { get; set; } = _ => Task.FromResult<string?>(null);

    public bool IsValid => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Folder);

    public DestinationConfig ToConfig(DestinationConfig current) => current with
    {
        Name = Name.Trim(),
        Folder = Folder?.Trim(),
        ServerUrl = string.IsNullOrWhiteSpace(ServerUrl) ? null : ServerUrl.Trim(),
        KomgaLibraryId = SelectedLibrary?.Id ?? (string.IsNullOrWhiteSpace(ServerUrl) ? null : _libraryId),
        OneShotsFolder = OneShotsFolder?.Trim() ?? string.Empty,
        SeriesTemplate = Template(SeriesTemplate, OutputNames.DefaultSeriesTemplate),
        OneShotTemplate = Template(OneShotTemplate, OutputNames.DefaultOneShotTemplate),
        IncludeUnreviewed = IncludeUnreviewed,
        IncludeTextBooks = IncludeTextBooks,
        Which = IsAllBooks ? DestinationScope.AllBooks : DestinationScope.ChosenBooks,
        When = IsAutomatic ? DestinationTiming.Automatic : DestinationTiming.Manual,
    };
}

/// <summary>"Adicionar destino": type → details → name and rules (SPEC §12).</summary>
public sealed partial class DestinationWizardViewModel : ViewModelBase
{
    private readonly Func<DestinationConfig, string?, Task> _create;
    private readonly Action _cancel;
    private readonly DestinationConfig _draft;

    public DestinationWizardViewModel(DestinationConfig draft, DestinationFormViewModel form, Func<DestinationConfig, string?, Task> create, Action cancel)
    {
        _draft = draft;
        Form = form;
        _create = create;
        _cancel = cancel;
        Types =
        [
            new(KomgaDestinationType.TypeId, "Komga", Strings.Wizard_KomgaHelp, true, Strings.Wizard_Selected),
            new("kindle", "Kindle", Strings.Wizard_KindleHelp, false, Strings.Wizard_NextPhase),
            new("kavita", "Kavita", Strings.Wizard_KavitaHelp, false, Strings.Wizard_Soon),
            new("folder", Strings.Wizard_FolderName, Strings.Wizard_FolderHelp, false, Strings.Wizard_Soon),
        ];
    }

    public DestinationFormViewModel Form { get; }

    public IReadOnlyList<DestinationTypeOption> Types { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(IsLastStep), nameof(Subtitle), nameof(Step2Done), nameof(Step3Done))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    public partial int Step { get; private set; } = 1;

    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;
    public bool IsLastStep => Step == 3;
    public bool Step2Done => Step >= 2;
    public bool Step3Done => Step >= 3;

    public string Subtitle => Step switch
    {
        1 => Strings.Wizard_Step1Subtitle,
        2 => Strings.Wizard_Step2Subtitle,
        _ => Strings.Wizard_Step3Subtitle,
    };

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [RelayCommand]
    private void Next()
    {
        Error = null;
        if (Step == 2 && string.IsNullOrWhiteSpace(Form.Folder))
        {
            Error = Strings.Wizard_NeedFolder;
            return;
        }

        Step = Math.Min(3, Step + 1);
    }

    private bool CanGoBack => Step > 1;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => Step = Math.Max(1, Step - 1);

    [RelayCommand]
    private void Cancel() => _cancel();

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (!Form.IsValid)
        {
            Error = Strings.Wizard_NeedNameAndFolder;
            return;
        }

        await _create(Form.ToConfig(_draft), Form.ApiKeyInput);
    }
}

/// <summary>A destination opened: sync with confirmation, progress, and its settings.</summary>
public sealed partial class DestinationDetailViewModel : ViewModelBase, ISyncActivity
{
    private readonly DestinationsPageViewModel _page;
    private SyncPlan? _plan;

    public DestinationDetailViewModel(DestinationConfig config, DestinationFormViewModel form, DestinationsPageViewModel page)
    {
        Config = config;
        Form = form;
        _page = page;
    }

    public DestinationConfig Config { get; private set; }

    public DestinationFormViewModel Form { get; }

    public string Title => Config.Name;

    [ObservableProperty]
    public partial string? StatusText { get; set; }

    [ObservableProperty]
    public partial StatusKind StatusKind { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlan))]
    public partial string? PlanText { get; private set; }

    public bool HasPlan => PlanText is not null;

    public ObservableCollection<string> PlanItems { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncCommand))]
    public partial bool CanSync { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsRunning { get; set; }

    public bool IsIdle => !IsRunning;

    [ObservableProperty]
    public partial string? ProgressText { get; set; }

    /// <summary>The latest entries of the destination's history, newest first.</summary>
    public ObservableCollection<HistoryRow> History { get; } = [];

    [ObservableProperty]
    public partial double ProgressValue { get; set; }

    [ObservableProperty]
    public partial string? ResultText { get; set; }

    [ObservableProperty]
    public partial bool ResultIsError { get; set; }

    [ObservableProperty]
    public partial string? SaveMessage { get; private set; }

    [ObservableProperty]
    public partial bool ConfirmingRemove { get; private set; }

    /// <summary>"Atualizar agora": shows what would change before writing anything.</summary>
    [RelayCommand]
    public async Task ShowPlanAsync(CancellationToken ct)
    {
        _plan = await _page.PlanAsync(Config.Id, ct);
        PlanItems.Clear();
        if (_plan is null)
        {
            return;
        }

        foreach (var action in _plan.Actions.Take(40))
        {
            var verb = action.Kind switch
            {
                SyncActionKind.Create => Strings.Plan_Create,
                SyncActionKind.Update => Strings.Plan_Update,
                _ => Strings.Plan_Delete,
            };
            PlanItems.Add($"{verb} · {action.NewPath ?? action.OldPath}");
        }

        if (_plan.Actions.Count > 40)
        {
            PlanItems.Add(string.Format(CultureInfo.CurrentCulture, Strings.Plan_More, _plan.Actions.Count - 40));
        }

        PlanText = _plan.IsEmpty
            ? Strings.Plan_Nothing
            : string.Format(CultureInfo.CurrentCulture, Strings.Plan_Summary, _plan.Creates, _plan.Updates, _plan.Deletes);
        CanSync = !_plan.IsEmpty;
    }

    [RelayCommand(CanExecute = nameof(CanSync))]
    private void Sync()
    {
        if (_plan is null)
        {
            return;
        }

        _page.Run(_plan);
        IsRunning = true;
        ProgressValue = 0;
        ProgressText = Strings.Sync_Starting;
        CanSync = false;
        PlanText = null;
        PlanItems.Clear();
        ResultText = null;
    }

    [RelayCommand]
    private void CancelPlan()
    {
        _plan = null;
        PlanText = null;
        PlanItems.Clear();
        CanSync = false;
    }

    [RelayCommand]
    private async Task SaveAsync(CancellationToken ct)
    {
        if (!Form.IsValid)
        {
            SaveMessage = Strings.Wizard_NeedNameAndFolder;
            return;
        }

        Config = await _page.SaveAsync(Form.ToConfig(Config), Form.ApiKeyInput, ct);
        Form.ApiKeyInput = null;
        SaveMessage = Strings.Destination_Saved;
        OnPropertyChanged(nameof(Title));
    }

    [RelayCommand]
    private void AskRemove() => ConfirmingRemove = true;

    [RelayCommand]
    private void CancelRemove() => ConfirmingRemove = false;

    [RelayCommand]
    private Task ConfirmRemoveAsync(CancellationToken ct) => _page.RemoveAsync(Config, ct);

    [RelayCommand]
    private void Back() => _page.ShowList();

    [RelayCommand]
    private Task OpenLogsAsync() => _page.OpenLogsAsync();
}

/// <summary>
/// Destinos (SPEC §9, §12): cards with status and rules, the "Adicionar destino" wizard and the
/// detail of each destination. Batch syncs always show their plan first (CLAUDE.md, rule 7).
/// </summary>
public sealed partial class DestinationsPageViewModel : PageViewModel
{
    private readonly LibrarySession _session;
    private readonly ISettingsService _settings;
    private readonly ISecretStore _secrets;
    private readonly KomgaClient _komga;
    private readonly IFolderPicker _folders;
    private readonly IEnumerable<IDestinationType> _types;
    private readonly IUiThread _ui;
    private readonly ILogger<DestinationsPageViewModel> _logger;
    private readonly IUrlOpener _urls;
    private readonly AppPaths _paths;
    private readonly Debouncer _refresh;

    public DestinationsPageViewModel(LibrarySession session, ISettingsService settings, ISecretStore secrets, KomgaClient komga,
        IFolderPicker folders, IEnumerable<IDestinationType> types, IUiThread ui, IUrlOpener urls, AppPaths paths,
        ILogger<DestinationsPageViewModel> logger)
    {
        _urls = urls;
        _paths = paths;
        _session = session;
        _settings = settings;
        _secrets = secrets;
        _komga = komga;
        _folders = folders;
        _types = types;
        _ui = ui;
        _logger = logger;
        Subtitle = Strings.Destinations_Subtitle;
        _refresh = new Debouncer(ui, TimeSpan.FromMilliseconds(400), () => RefreshAsync());
        if (session.Destinations is { } service)
        {
            service.Changed += OnServiceChanged;
            service.ProgressChanged += OnProgress;
        }
    }

    public override string Title => Strings.Nav_Destinations;

    public ObservableCollection<DestinationCardViewModel> Cards { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsList), nameof(IsWizard), nameof(IsDetail))]
    public partial DestinationsMode Mode { get; private set; }

    public bool IsList => Mode == DestinationsMode.List;
    public bool IsWizard => Mode == DestinationsMode.Wizard;
    public bool IsDetail => Mode == DestinationsMode.Detail;

    [ObservableProperty]
    public partial DestinationWizardViewModel? Wizard { get; private set; }

    [ObservableProperty]
    public partial DestinationDetailViewModel? Detail { get; private set; }

    public override async Task OnNavigatedToAsync(CancellationToken ct = default)
    {
        if (Mode != DestinationsMode.Wizard)
        {
            await RefreshAsync(ct);
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_session.Destinations is not { } service)
        {
            return;
        }

        try
        {
            var summaries = new List<DestinationSummary>();
            foreach (var destination in service.Destinations)
            {
                summaries.Add(await service.SummaryAsync(destination, ct));
            }

            Cards.Clear();
            foreach (var summary in summaries)
            {
                var card = new DestinationCardViewModel(summary, service.LastResult(summary.Config.Id), (c, sync) => _ = OpenAsync(c.Id, sync));
                ShowActivity(service, card, summary.Config.Id);
                Cards.Add(card);
            }

            if (Detail is { } detail && summaries.FirstOrDefault(s => s.Config.Id == detail.Config.Id) is { } current)
            {
                detail.StatusText = DestinationText.Status(current);
                detail.StatusKind = Cards.First(c => c.Id == current.Config.Id).StatusKind;
                ShowActivity(service, detail, current.Config.Id);
                detail.History.Clear();
                foreach (var entry in await service.HistoryAsync(current.Config.Id, 200, ct))
                {
                    detail.History.Add(DestinationText.History(entry));
                }

                if (service.LastResult(current.Config.Id) is { } result && !detail.IsRunning)
                {
                    detail.ResultIsError = result.Failures.Count > 0;
                    detail.ResultText = result.Failures.Count == 0
                        ? string.Format(CultureInfo.CurrentCulture, Strings.Sync_Done, result.Written, result.Deleted)
                        : string.Format(CultureInfo.CurrentCulture, Strings.Sync_DoneWithErrors, result.Written, result.Deleted, result.Failures.Count,
                            string.Join("; ", result.Failures.Take(3).Select(f => $"{f.Title}: {f.Error}")));
                }
            }
        }
        catch (Exception ex) when (LibrarySession.IsExpectedFailure(ex))
        {
            _logger.LogError(ex, "Could not read the destinations");
        }
    }

    [RelayCommand]
    private async Task AddDestinationAsync(CancellationToken ct)
    {
        var type = _types.First(t => t.Id == KomgaDestinationType.TypeId);
        var draft = type.CreateDefault(Ulid.NewUlid(), Cards.Count == 0 ? Strings.Wizard_DefaultKomgaName : $"{Strings.Wizard_DefaultKomgaName} {Cards.Count + 1}");
        Wizard = new DestinationWizardViewModel(draft, await FormAsync(draft, ct), CreateAsync, ShowList);
        Detail = null;
        Mode = DestinationsMode.Wizard;
    }

    private async Task<DestinationFormViewModel> FormAsync(DestinationConfig config, CancellationToken ct)
    {
        var sample = (await (_session.Index?.GetBooksAsync(ct) ?? Task.FromResult<IReadOnlyList<Library.Index.CatalogBook>>([])))
            .Select(b => b.Metadata)
            .FirstOrDefault(m => m.Series is not null && m.Review.Status == ReviewStatus.Approved)
            ?? new BookMetadata { Id = Ulid.NewUlid(), Title = "Saga, Vol. 1", Series = new SeriesInfo("Saga", "1"), Date = new PartialDate(2012), Publisher = "Image" };
        return new DestinationFormViewModel(config, sample, _komga, _folders)
        {
            StoredKeyAsync = token => _secrets.IsSupported ? _secrets.GetAsync(config.SecretName, token) : Task.FromResult<string?>(null),
        };
    }

    private async Task CreateAsync(DestinationConfig config, string? apiKey)
    {
        await SaveAsync(config, apiKey, CancellationToken.None);
        await OpenAsync(config.Id, showPlan: true);
    }

    /// <summary>Saves a destination (new or changed) and its key.</summary>
    public async Task<DestinationConfig> SaveAsync(DestinationConfig config, string? apiKey, CancellationToken ct)
    {
        var others = _settings.Current.Destinations.Where(d => d.Id != config.Id);
        await _settings.SaveAsync(_settings.Current with { Destinations = [.. others, config] }, ct);
        if (!string.IsNullOrWhiteSpace(apiKey) && _secrets.IsSupported)
        {
            await _secrets.SetAsync(config.SecretName, apiKey.Trim(), ct);
        }

        await RefreshAsync(ct);
        return config;
    }

    public async Task RemoveAsync(DestinationConfig config, CancellationToken ct)
    {
        await _settings.SaveAsync(_settings.Current with { Destinations = _settings.Current.Destinations.Where(d => d.Id != config.Id).ToList() }, ct);
        if (_secrets.IsSupported)
        {
            await _secrets.RemoveAsync(config.SecretName, ct);
        }

        if (_session.Destinations is { } service)
        {
            await service.ForgetAsync(config.Id, ct);
        }

        ShowList();
    }

    public async Task OpenAsync(string destinationId, bool showPlan)
    {
        if (_settings.Current.Destinations.FirstOrDefault(d => d.Id == destinationId) is not { } config)
        {
            return;
        }

        Detail = new DestinationDetailViewModel(config, await FormAsync(config, CancellationToken.None), this);
        Wizard = null;
        Mode = DestinationsMode.Detail;
        await RefreshAsync();
        if (showPlan)
        {
            await Detail.ShowPlanAsync(CancellationToken.None);
        }
    }

    public void ShowList()
    {
        Wizard = null;
        Detail = null;
        Mode = DestinationsMode.List;
        _refresh.Trigger();
    }

    public async Task<SyncPlan?> PlanAsync(string destinationId, CancellationToken ct) =>
        _session.Destinations is { } service ? await service.PlanAsync(destinationId, ct) : null;

    public void Run(SyncPlan plan) => _session.Destinations?.Run(plan);

    private void OnServiceChanged(object? sender, EventArgs e) => _refresh.Trigger();

    /// <summary>Running while its job works (or while a job it started waits in line).</summary>
    private static void ShowActivity(DestinationService service, ISyncActivity activity, string destinationId)
    {
        if (service.Current is { } progress && progress.DestinationId == destinationId)
        {
            DestinationText.Report(activity, progress);
        }
        else if (!(activity.IsRunning && service.IsBusy && service.Current is null))
        {
            activity.IsRunning = false;
        }
    }

    private void OnProgress(object? sender, SyncProgress? progress) => _ui.Post(() =>
    {
        var activities = Cards.Cast<ISyncActivity>().Select(a => (Activity: a, Id: ((DestinationCardViewModel)a).Id)).ToList();
        if (Detail is { } detail)
        {
            activities.Add((detail, detail.Config.Id));
        }

        foreach (var (activity, id) in activities)
        {
            if (progress is not null && progress.DestinationId == id)
            {
                DestinationText.Report(activity, progress);
            }
            else if (progress is null)
            {
                activity.IsRunning = false;
            }
        }

        if (progress is null)
        {
            _refresh.Trigger();
        }
    });

    /// <summary>The app's log files (errors with all their details), for debugging.</summary>
    public Task OpenLogsAsync()
    {
        Directory.CreateDirectory(_paths.LogsDirectory);
        return _urls.OpenAsync(new Uri(_paths.LogsDirectory + Path.DirectorySeparatorChar));
    }

    public override void Dispose()
    {
        if (_session.Destinations is { } service)
        {
            service.Changed -= OnServiceChanged;
            service.ProgressChanged -= OnProgress;
        }

        _refresh.Dispose();
    }
}
