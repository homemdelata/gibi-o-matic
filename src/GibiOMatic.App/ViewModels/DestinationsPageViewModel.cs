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
using GibiOMatic.Destinations.Kindle;
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

/// <summary>A destination card in the list (SPEC §12, Destinations).</summary>
/// <summary>
/// A destination in the list. It shows up as soon as the page opens ("Verificando…"); the summary
/// arrives later, because checking a network folder that does not answer takes up to a minute.
/// </summary>
public sealed partial class DestinationCardViewModel(DestinationConfig config, Action<DestinationCardViewModel, bool> open)
    : ViewModelBase, ISyncActivity
{
    private SyncResult? _lastResult;

    public DestinationConfig Config { get; } = config;
    public string Id => Config.Id;
    public string Name => Config.Name;
    public bool IsKindle => Config.Type == KindleDestinationType.TypeId;
    public string TypeLine => IsKindle ? Strings.Destination_KindleType : Strings.Destination_KomgaType;

    /// <summary>Komga: "Atualizar agora"; Kindle: "Enviar" (opens the list and, with the device connected, the confirmation).</summary>
    public string ActionText => IsKindle ? Strings.Kindle_SendShort : Strings.Destinations_SyncNow;
    public string WhichText => Config.Which == DestinationScope.AllBooks ? Strings.Destination_AllBooks : Strings.Destination_ChosenBooks;
    public string WhenText => Config.When == DestinationTiming.Automatic ? Strings.Destination_Automatic : Strings.Destination_Manual;
    public string? Folder => Config.Folder;

    /// <summary>Null while the status is being checked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusKind), nameof(StatusText))]
    public partial DestinationSummary? Summary { get; private set; }

    public StatusKind StatusKind => Summary is not { } summary
        ? StatusKind.Neutral
        : summary.Status.Health != DestinationHealth.Ready || _lastResult?.Failures.Count > 0
            ? StatusKind.Bad
            : !summary.Confirmed || summary.Pending > 0 ? StatusKind.Warn : StatusKind.Ok;

    /// <summary>The folder chip: a Kindle's local folder says little, its status line says more.</summary>
    public string? FolderChip => IsKindle ? null : Folder;

    public string StatusText => Summary is not { } summary
        ? Strings.Destination_Checking
        : _lastResult?.Failures.Count > 0 && summary.Status.Health == DestinationHealth.Ready
            ? string.Format(CultureInfo.CurrentCulture, Strings.Destination_StatusLastErrors, _lastResult.Failures.Count, DestinationText.Status(summary))
            : DestinationText.Status(summary);

    public void Show(DestinationSummary summary, SyncResult? lastResult)
    {
        _lastResult = lastResult;
        Summary = summary;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand))]
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
    private void Configure() => open(this, false);

    private bool CanSyncNow => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanSyncNow))]
    private void SyncNow() => open(this, true);
}

/// <summary>What the card and the detail show while a destination is being updated.</summary>
public interface ISyncActivity
{
    bool IsRunning { get; set; }

    /// <summary>"Generating Saga v01 (3 of 10)…" and the bar of copies done.</summary>
    string? ProgressText { get; set; }
    double ProgressValue { get; set; }

    /// <summary>How far the copy being generated got (pages converted).</summary>
    double FileProgressValue { get; set; }
    string? FileProgressText { get; set; }
}

/// <summary>A line of a destination's history.</summary>
public sealed record HistoryRow(string When, string Text, string? Detail, bool IsError);

/// <summary>Texts about destinations shared by the page, the sidebar and the book panel.</summary>
public static class DestinationText
{
    public static string Status(DestinationSummary s)
    {
        if (s.Config.Type == KindleDestinationType.TypeId)
        {
            return s.Status.Health == DestinationHealth.NotConfigured
                ? Strings.Kindle_StatusNoKcc
                : s.Pending > 0
                    ? string.Format(CultureInfo.CurrentCulture, Strings.Kindle_StatusPreparing, s.Pending, s.UpToDate)
                    : s.Included == 0 ? Strings.Kindle_StatusEmpty : string.Format(CultureInfo.CurrentCulture, Strings.Kindle_StatusReady, s.UpToDate);
        }

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

    /// <summary>"Generating Saga v01 (3 of 10)…"; the last step is the server notice.</summary>
    public static void Report(ISyncActivity activity, SyncProgress progress)
    {
        activity.IsRunning = true;
        activity.ProgressValue = progress.Total == 0 ? 0 : (double)progress.Done / progress.Total;
        var finishing = progress.Done >= progress.Total || string.IsNullOrEmpty(progress.Title);
        activity.ProgressText = (finishing
            ? Strings.Sync_Finishing
            : string.Format(CultureInfo.CurrentCulture, Strings.Sync_Progress, progress.Title, progress.Done + 1, progress.Total))
            + (progress.Queued > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.Sync_Queued, progress.Queued) : string.Empty);
        activity.FileProgressValue = finishing ? 0 : progress.FileFraction;
        activity.FileProgressText = finishing ? null : string.Format(CultureInfo.CurrentCulture, Strings.Sync_FileProgress, progress.FileFraction);
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
            DestinationLogKind.Sent => new(when, Strings.History_Sent, item, false),
            DestinationLogKind.RemovedFromDevice => new(when, Strings.History_RemovedFromDevice, item, false),
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

public sealed partial class DestinationTypeOption(string id, string name, string help, bool isAvailable, string? tag, Action<DestinationTypeOption>? pick = null)
    : ViewModelBase
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Help { get; } = help;
    public bool IsAvailable { get; } = isAvailable;
    public string? Tag { get; } = tag;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Pick() => pick?.Invoke(this);
}

/// <summary>The fields of a Komga destination, shared by the wizard and the detail.</summary>
public sealed partial class DestinationFormViewModel : ViewModelBase
{
    private readonly KomgaClient _komga;
    private readonly IFolderPicker _folders;
    private readonly ILogger _logger;
    private readonly BookMetadata _sample;

    public DestinationFormViewModel(DestinationConfig config, BookMetadata sample, KomgaClient komga, IFolderPicker folders, ILogger logger)
    {
        _komga = komga;
        _logger = logger;
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
                : ex.NotApi
                    ? Strings.Destination_ServerNotApi
                    : string.Format(CultureInfo.CurrentCulture, Strings.Destination_ServerError, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Whatever the server does, the test only reports it.
            _logger.LogError(ex, "Testing the Komga server failed");
            ServerStatusIsError = true;
            ServerStatus = string.Format(CultureInfo.CurrentCulture, Strings.Destination_ServerError, ex.Message);
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

/// <summary>"Add destination": type → details → name and rules (SPEC §12).</summary>
public sealed partial class DestinationWizardViewModel : ViewModelBase
{
    private readonly Func<DestinationConfig, string?, Task> _create;
    private readonly Action _cancel;
    private readonly DestinationConfig _draft;
    private readonly DestinationConfig _kindleDraft;

    public DestinationWizardViewModel(DestinationConfig draft, DestinationFormViewModel form, DestinationConfig kindleDraft, KindleFormViewModel kindleForm,
        Func<DestinationConfig, string?, Task> create, Action cancel)
    {
        _draft = draft;
        _kindleDraft = kindleDraft;
        Form = form;
        KindleForm = kindleForm;
        _create = create;
        _cancel = cancel;
        Types =
        [
            new(KomgaDestinationType.TypeId, "Komga", Strings.Wizard_KomgaHelp, true, null, PickType),
            new(KindleDestinationType.TypeId, "Kindle", Strings.Wizard_KindleHelp, true, null, PickType),
            new("kavita", "Kavita", Strings.Wizard_KavitaHelp, false, Strings.Wizard_Soon),
            new("folder", Strings.Wizard_FolderName, Strings.Wizard_FolderHelp, false, Strings.Wizard_Soon),
        ];
        Types[0].IsSelected = true;
    }

    public DestinationFormViewModel Form { get; }

    public KindleFormViewModel KindleForm { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsKindle), nameof(IsKomga), nameof(IsLastStep), nameof(IsKomgaStep2), nameof(IsKindleStep2), nameof(StepCount))]
    public partial string Type { get; private set; } = KomgaDestinationType.TypeId;

    public bool IsKindle => Type == KindleDestinationType.TypeId;
    public bool IsKomga => !IsKindle;

    /// <summary>A Kindle takes only its own page: it always gets the books chosen by hand.</summary>
    public int StepCount => IsKindle ? 2 : 3;

    public bool IsKomgaStep2 => IsStep2 && IsKomga;
    public bool IsKindleStep2 => IsStep2 && IsKindle;

    private void PickType(DestinationTypeOption option)
    {
        foreach (var type in Types)
        {
            type.IsSelected = type == option;
        }

        Type = option.Id;
    }

    public IReadOnlyList<DestinationTypeOption> Types { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(IsLastStep), nameof(Subtitle), nameof(Step2Done), nameof(Step3Done),
        nameof(IsKomgaStep2), nameof(IsKindleStep2))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    public partial int Step { get; private set; } = 1;

    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;
    public bool IsLastStep => Step == StepCount;
    public bool Step2Done => Step >= 2;
    public bool Step3Done => Step >= 3;

    public string Subtitle => Step switch
    {
        1 => Strings.Wizard_Step1Subtitle,
        2 when IsKindle => Strings.Wizard_KindleSubtitle,
        2 => Strings.Wizard_Step2Subtitle,
        _ => Strings.Wizard_Step3Subtitle,
    };

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [RelayCommand]
    private void Next()
    {
        Error = null;
        if (Step == 2 && IsKomga && string.IsNullOrWhiteSpace(Form.Folder))
        {
            Error = Strings.Wizard_NeedFolder;
            return;
        }

        Step = Math.Min(StepCount, Step + 1);
    }

    private bool CanGoBack => Step > 1;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => Step = Math.Max(1, Step - 1);

    [RelayCommand]
    private void Cancel() => _cancel();

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (IsKindle)
        {
            if (!KindleForm.IsValid)
            {
                Error = Strings.Wizard_NeedName;
                return;
            }

            await _create(KindleForm.ToConfig(_kindleDraft), null);
            return;
        }

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
    public partial double FileProgressValue { get; set; }

    [ObservableProperty]
    public partial string? FileProgressText { get; set; }

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
/// Destinations (SPEC §9, §12): cards with status and rules, the "Add destination" wizard and the
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
    private readonly KindleWatcher _kindle;
    private readonly KccInstaller _kccInstaller;
    private readonly IFilePicker _files;
    private readonly Debouncer _refresh;

    public DestinationsPageViewModel(LibrarySession session, ISettingsService settings, ISecretStore secrets, KomgaClient komga,
        IFolderPicker folders, IEnumerable<IDestinationType> types, IUiThread ui, IUrlOpener urls, AppPaths paths,
        KindleWatcher kindle, KccInstaller kccInstaller, IFilePicker files, ILogger<DestinationsPageViewModel> logger)
    {
        _kindle = kindle;
        _kccInstaller = kccInstaller;
        _files = files;
        kindle.Changed += OnKindleChanged;
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
    [NotifyPropertyChangedFor(nameof(IsList), nameof(IsWizard), nameof(IsDetail), nameof(IsKindleDetail))]
    public partial DestinationsMode Mode { get; private set; }

    public bool IsList => Mode == DestinationsMode.List;
    public bool IsWizard => Mode == DestinationsMode.Wizard;
    public bool IsDetail => Mode == DestinationsMode.Detail && Detail is not null;
    public bool IsKindleDetail => Mode == DestinationsMode.Detail && KindleDetail is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetail), nameof(IsKindleDetail))]
    public partial KindleDetailViewModel? KindleDetail { get; private set; }

    [ObservableProperty]
    public partial DestinationWizardViewModel? Wizard { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetail), nameof(IsKindleDetail))]
    public partial DestinationDetailViewModel? Detail { get; private set; }

    /// <summary>Coming back to Destinations shows the list, not the destination (or wizard) left open.</summary>
    public override void OnNavigatedFrom()
    {
        if (!IsList)
        {
            ShowList();
        }
    }

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

        // The cards show up right away; each summary arrives on its own, so a network drive that
        // does not answer (up to a minute) holds only its own card.
        var byId = Cards.ToDictionary(c => c.Id);
        Cards.Clear();
        var loads = new List<Task>();
        foreach (var destination in service.Destinations)
        {
            if (!byId.TryGetValue(destination.Config.Id, out var card) || card.Config != destination.Config)
            {
                card = new DestinationCardViewModel(destination.Config, (c, sync) => _ = OpenAsync(c.Id, sync));
            }

            ShowActivity(service, card, destination.Config.Id);
            Cards.Add(card);
            loads.Add(LoadCardAsync(service, destination, card, ct));
        }

        try
        {
            if (KindleDetail is { } kindle)
            {
                ShowActivity(service, kindle, kindle.Config.Id);
                await kindle.RefreshAsync(ct);
                await kindle.LoadHistoryAsync();
            }
        }
        catch (Exception ex) when (LibrarySession.IsExpectedFailure(ex))
        {
            _logger.LogError(ex, "Could not read the destinations");
        }

        await Task.WhenAll(loads);
    }

    private async Task LoadCardAsync(DestinationService service, IDestination destination, DestinationCardViewModel card, CancellationToken ct)
    {
        try
        {
            var summary = await service.SummaryAsync(destination, ct);
            card.Show(summary, service.LastResult(summary.Config.Id));
            if (Detail is { } detail && detail.Config.Id == summary.Config.Id)
            {
                detail.StatusText = DestinationText.Status(summary);
                detail.StatusKind = card.StatusKind;
                ShowActivity(service, detail, summary.Config.Id);
                // Read before clearing: two refreshes at once would otherwise both add their rows.
                var history = await service.HistoryAsync(summary.Config.Id, 200, ct);
                detail.History.Clear();
                foreach (var entry in history)
                {
                    detail.History.Add(DestinationText.History(entry));
                }

                if (service.LastResult(summary.Config.Id) is { } result && !detail.IsRunning)
                {
                    detail.ResultIsError = result.Failures.Count > 0;
                    detail.ResultText = result.Failures.Count == 0
                        ? string.Format(CultureInfo.CurrentCulture, Strings.Sync_Done, result.Written, result.Deleted)
                        : string.Format(CultureInfo.CurrentCulture, Strings.Sync_DoneWithErrors, result.Written, result.Deleted, result.Failures.Count,
                            string.Join("; ", result.Failures.Take(3).Select(f => $"{f.Title}: {f.Error}")));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (LibrarySession.IsExpectedFailure(ex))
        {
            _logger.LogError(ex, "Could not read the destination {DestinationId}", destination.Config.Id);
        }
    }

    [RelayCommand]
    private async Task AddDestinationAsync(CancellationToken ct)
    {
        var type = _types.First(t => t.Id == KomgaDestinationType.TypeId);
        var draft = type.CreateDefault(Ulid.NewUlid(), Cards.Count == 0 ? Strings.Wizard_DefaultKomgaName : $"{Strings.Wizard_DefaultKomgaName} {Cards.Count + 1}");
        var kindles = Cards.Count(c => c.IsKindle);
        var kindleDraft = _types.First(t => t.Id == KindleDestinationType.TypeId)
            .CreateDefault(Ulid.NewUlid(), kindles == 0 ? Strings.Wizard_DefaultKindleName : $"{Strings.Wizard_DefaultKindleName} {kindles + 1}");
        Wizard = new DestinationWizardViewModel(draft, await FormAsync(draft, ct), kindleDraft, KindleForm(kindleDraft), CreateAsync, ShowList);
        Detail = null;
        KindleDetail = null;
        Mode = DestinationsMode.Wizard;
    }

    private KindleFormViewModel KindleForm(DestinationConfig config) => new(config, _kccInstaller, _files, _ui, _logger, _paths.ToolsDirectory);

    private async Task<DestinationFormViewModel> FormAsync(DestinationConfig config, CancellationToken ct)
    {
        var sample = (await (_session.Index?.GetBooksAsync(ct) ?? Task.FromResult<IReadOnlyList<Library.Index.CatalogBook>>([])))
            .Select(b => b.Metadata)
            .FirstOrDefault(m => m.Series is not null && m.Review.Status == ReviewStatus.Approved)
            ?? new BookMetadata { Id = Ulid.NewUlid(), Title = "Saga, Vol. 1", Series = new SeriesInfo("Saga", "1"), Date = new PartialDate(2012), Publisher = "Image" };
        return new DestinationFormViewModel(config, sample, _komga, _folders, _logger)
        {
            StoredKeyAsync = token => _secrets.IsSupported ? _secrets.GetAsync(config.SecretName, token) : Task.FromResult<string?>(null),
        };
    }

    private async Task CreateAsync(DestinationConfig config, string? apiKey)
    {
        await SaveAsync(config, apiKey, CancellationToken.None);
        if (config.Type == KindleDestinationType.TypeId && _session.Destinations is { } service)
        {
            // A Kindle prepares its books as they are chosen: no first sync to confirm.
            await service.MarkConfirmedAsync(config.Id);
        }

        await OpenAsync(config.Id, showPlan: config.Type != KindleDestinationType.TypeId);
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

        _session.Destinations?.NotifyDestinationsChanged();
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
            service.NotifyDestinationsChanged();
        }

        ShowList();
    }

    public async Task OpenAsync(string destinationId, bool showPlan)
    {
        if (_settings.Current.Destinations.FirstOrDefault(d => d.Id == destinationId) is not { } config)
        {
            return;
        }

        if (config.Type == KindleDestinationType.TypeId)
        {
            await OpenKindleAsync(config, showPlan);
            return;
        }

        Detail = new DestinationDetailViewModel(config, await FormAsync(config, CancellationToken.None), this);
        KindleDetail = null;
        Wizard = null;
        Mode = DestinationsMode.Detail;
        await RefreshAsync();
        if (showPlan)
        {
            await Detail.ShowPlanAsync(CancellationToken.None);
        }
    }

    /// <summary>The Kindle's page; <paramref name="showPlan"/> asks to send at once when the device is connected.</summary>
    private async Task OpenKindleAsync(DestinationConfig config, bool showPlan)
    {
        if (_session.Destinations is not { } service)
        {
            return;
        }

        var detail = new KindleDetailViewModel(config, KindleForm(config), service, _kindle, _ui, _logger,
            async c => await SaveAsync(c, null, CancellationToken.None), RemoveAsync, ShowList,
            folder =>
            {
                Directory.CreateDirectory(folder);
                return _urls.OpenAsync(new Uri(folder + Path.DirectorySeparatorChar));
            })
        {
            IndexBooksAsync = async ct => (await (_session.Index?.GetBooksAsync(ct) ?? Task.FromResult<IReadOnlyList<Library.Index.CatalogBook>>([])))
                .Select(b => b.Metadata).ToList(),
        };
        detail.Sent += (_, _) => KindleSent?.Invoke(this, EventArgs.Empty);
        KindleDetail = detail;
        Detail = null;
        Wizard = null;
        Mode = DestinationsMode.Detail;
        await RefreshAsync();
        if (showPlan && detail.CanSend)
        {
            await detail.ShowSendPlanCommand.ExecuteAsync(null);
        }
    }

    /// <summary>Books went to the Kindle (the shell's banner updates).</summary>
    public event EventHandler? KindleSent;

    private void OnKindleChanged(object? sender, EventArgs e)
    {
        KindleDetail?.UpdateDevice();
        _refresh.Trigger();
    }

    public void ShowList()
    {
        Wizard = null;
        Detail = null;
        KindleDetail = null;
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
        if (service.CurrentOf(destinationId) is { } progress)
        {
            DestinationText.Report(activity, progress);
        }
        else if (!service.IsBusyOf(destinationId))
        {
            activity.IsRunning = false;
        }
    }

    private void OnProgress(object? sender, SyncProgress progress) => _ui.Post(() =>
    {
        var activities = Cards.Cast<ISyncActivity>().Select(a => (Activity: a, Id: ((DestinationCardViewModel)a).Id)).ToList();
        if (Detail is { } detail)
        {
            activities.Add((detail, detail.Config.Id));
        }

        if (KindleDetail is { } kindle)
        {
            activities.Add((kindle, kindle.Config.Id));
        }

        // Each destination works on its own: only its card and detail follow its progress.
        foreach (var (activity, id) in activities.Where(a => a.Id == progress.DestinationId))
        {
            if (progress.IsFinished)
            {
                activity.IsRunning = false;
            }
            else
            {
                DestinationText.Report(activity, progress);
            }
        }

        if (progress.IsFinished)
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
        _kindle.Changed -= OnKindleChanged;
        if (_session.Destinations is { } service)
        {
            service.Changed -= OnServiceChanged;
            service.ProgressChanged -= OnProgress;
        }

        _refresh.Dispose();
    }
}
