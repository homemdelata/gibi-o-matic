using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Core.Settings;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.App.ViewModels;

/// <summary>What closing the main window does.</summary>
public enum CloseAction
{
    /// <summary>The window hides; the app goes on in the tray.</summary>
    Hide,

    /// <summary>First close: the window stays and asks (decision of 2026-10-23).</summary>
    Ask,
    Exit,
}

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

    /// <summary>"O app continua na bandeja": shown the first time the window is closed.</summary>
    [ObservableProperty]
    public partial bool ShowClosePrompt { get; private set; }

    /// <summary>The user answered the close prompt: true keeps the app in the tray, false closes it.</summary>
    public event EventHandler<bool>? CloseChosen;

    /// <summary>Decides what closing the window does; <see cref="CloseAction.Ask"/> shows the prompt.</summary>
    public CloseAction OnCloseRequested(bool trayAvailable)
    {
        if (!trayAvailable)
        {
            return CloseAction.Exit;
        }

        switch (_settings.Current.Background.CloseToTray)
        {
            case true:
                return CloseAction.Hide;
            case false:
                return CloseAction.Exit;
            default:
                ShowClosePrompt = true;
                return CloseAction.Ask;
        }
    }

    [RelayCommand]
    private Task KeepInTrayAsync() => ChooseCloseAsync(keepInTray: true);

    [RelayCommand]
    private Task CloseAppAsync() => ChooseCloseAsync(keepInTray: false);

    /// <summary>The prompt was dismissed: the window stays open and the question comes back next time.</summary>
    [RelayCommand]
    private void CancelClose() => ShowClosePrompt = false;

    private async Task ChooseCloseAsync(bool keepInTray)
    {
        ShowClosePrompt = false;
        try
        {
            var current = _settings.Current;
            await _settings.SaveAsync(current with { Background = current.Background with { CloseToTray = keepInTray } });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save the close choice");
        }

        CloseChosen?.Invoke(this, keepInTray);
    }

    /// <summary>Shows a page of the shell (a click on a notification).</summary>
    public void ShowPage(NavPage page)
    {
        if (Content is ShellViewModel shell)
        {
            shell.NavigateTo(page);
        }
    }

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
            (Content as IDisposable)?.Dispose();
            Content = shell;
            await shell.InitializeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show library {LibraryId}", library.Id);
        }
    }
}
