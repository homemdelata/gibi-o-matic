using CommunityToolkit.Mvvm.ComponentModel;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Core.Settings;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.App.ViewModels;

/// <summary>
/// Decides what the window shows: the welcome screen until a library is open, then the shell.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly LibrarySession _session;
    private readonly Func<WelcomeViewModel> _createWelcome;
    private readonly Func<ShellViewModel> _createShell;
    private readonly ILogger<MainWindowViewModel> _logger;

    public MainWindowViewModel(
        ISettingsService settings,
        LibrarySession session,
        Func<WelcomeViewModel> createWelcome,
        Func<ShellViewModel> createShell,
        ILogger<MainWindowViewModel> logger)
    {
        _settings = settings;
        _session = session;
        _createWelcome = createWelcome;
        _createShell = createShell;
        _logger = logger;
        _session.Changed += OnLibraryChanged;
    }

    [ObservableProperty]
    public partial ViewModelBase? Content { get; private set; }

    /// <summary>Opens the library remembered from the last run, or shows the welcome screen.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var remembered = _settings.Current.LibraryPath;
        if (string.IsNullOrWhiteSpace(remembered))
        {
            Content = _createWelcome();
            return;
        }

        try
        {
            await _session.OpenAsync(remembered, ct);
        }
        catch (Exception ex) when (LibrarySession.IsExpectedFailure(ex))
        {
            _logger.LogWarning(ex, "Could not open the remembered library at {Path}", remembered);
            var welcome = _createWelcome();
            welcome.ErrorMessage = string.Format(Strings.Error_RememberedLibrary, remembered, LibrarySession.DescribeError(ex));
            Content = welcome;
        }
    }

    private async void OnLibraryChanged(object? sender, LibraryInfo library)
    {
        try
        {
            var shell = _createShell();
            Content = shell;
            await shell.InitializeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show library {LibraryId}", library.Id);
        }
    }
}
