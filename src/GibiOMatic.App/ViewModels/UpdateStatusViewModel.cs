using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;

namespace GibiOMatic.App.ViewModels;

/// <summary>
/// The app's version and its updates (F6): the banner at the top of the shell ("Version 0.7.0
/// is available.") and the card in Settings → General. Nothing is installed without the user asking.
/// </summary>
public sealed partial class UpdateStatusViewModel : ViewModelBase, IDisposable
{
    private readonly UpdateChecker _checker;
    private bool _dismissed;

    public UpdateStatusViewModel(UpdateChecker checker)
    {
        _checker = checker;
        checker.Changed += OnChanged;
        Update();
    }

    public string VersionText => string.Format(CultureInfo.CurrentCulture, Strings.Update_Version, _checker.CurrentVersion);

    public bool IsSupported => _checker.State != UpdateState.Unsupported;

    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    [ObservableProperty]
    public partial bool StatusIsError { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    public partial bool CanCheck { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial bool CanInstall { get; private set; }

    [ObservableProperty]
    public partial bool IsDownloading { get; private set; }

    [ObservableProperty]
    public partial double Progress { get; private set; }

    [ObservableProperty]
    public partial bool ShowBanner { get; private set; }

    [ObservableProperty]
    public partial string? BannerText { get; private set; }

    private void OnChanged(object? sender, EventArgs e) => Update();

    private void Update()
    {
        if (_checker.State == UpdateState.Checking)
        {
            _dismissed = false;
        }

        var version = _checker.Available?.Version;
        StatusIsError = _checker.State == UpdateState.Failed;
        StatusText = _checker.State switch
        {
            UpdateState.Unsupported => Strings.Update_Unsupported,
            UpdateState.Checking => Strings.Update_Checking,
            UpdateState.UpToDate => Strings.Update_UpToDate,
            UpdateState.Available => string.Format(CultureInfo.CurrentCulture, Strings.Update_Available, version),
            UpdateState.Downloading => string.Format(CultureInfo.CurrentCulture, Strings.Update_Downloading, version, _checker.DownloadProgress),
            UpdateState.Failed => Strings.Update_Failed,
            _ => null,
        };
        CanCheck = _checker.State is UpdateState.Idle or UpdateState.UpToDate or UpdateState.Failed or UpdateState.Available;
        CanInstall = _checker.State == UpdateState.Available;
        IsDownloading = _checker.State == UpdateState.Downloading;
        Progress = _checker.DownloadProgress / 100.0;
        BannerText = StatusText;
        ShowBanner = !_dismissed && _checker.State is UpdateState.Available or UpdateState.Downloading;
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckAsync() => _checker.CheckAsync();

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task InstallAsync() => _checker.InstallAsync();

    /// <summary>"Not now": the banner comes back with the next check that finds a version.</summary>
    [RelayCommand]
    private void Dismiss()
    {
        _dismissed = true;
        ShowBanner = false;
    }

    public void Dispose() => _checker.Changed -= OnChanged;
}
