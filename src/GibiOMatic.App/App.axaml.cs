using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using GibiOMatic.App.Services;
using GibiOMatic.App.Services.Tray;
using GibiOMatic.App.ViewModels;
using GibiOMatic.App.Views;
using GibiOMatic.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace GibiOMatic.App;

/// <summary>How the app was started (command line and single instance).</summary>
/// <param name="InTray">Started with the system in "only the tray" mode: no window until asked.</param>
public sealed record AppLaunch(bool InTray, SingleInstance? Instance);

public partial class App : Application, IAppHost
{
    private readonly IServiceProvider? _services;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainWindowViewModel? _viewModel;
    private MainWindow? _window;
    private TrayController? _tray;
    private bool _exiting;

    // Parameterless constructor for the XAML designer.
    public App()
        : this(services: null)
    {
    }

    public App(IServiceProvider? services)
    {
        _services = services;
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && _services is not null)
        {
            _desktop = desktop;
            var settings = _services.GetRequiredService<ISettingsService>();
            ApplyTheme(settings.Current.Theme);
            // A new theme applies right away (F6); "Seguir o sistema" follows the system while running.
            settings.Saved += (_, saved) => Dispatcher.UIThread.Post(() => ApplyTheme(saved.Theme));
            // The app lives on in the tray when the window closes; "Sair" ends it.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var launch = _services.GetService<AppLaunch>() ?? new AppLaunch(InTray: false, Instance: null);
            _viewModel = _services.GetRequiredService<MainWindowViewModel>();
            _viewModel.CloseChosen += (_, keepInTray) =>
            {
                if (keepInTray)
                {
                    _window?.Hide();
                }
                else
                {
                    Exit();
                }
            };

            _tray = _services.GetRequiredService<TrayController>();
            _tray.Start(this);
            if (!launch.InTray || !_tray.IsSupported)
            {
                ShowMainWindow();
            }

            // The library opens even with no window: the inbox folder is watched from the tray.
            Dispatcher.UIThread.Post(async () => await _viewModel.InitializeAsync());
            ApplyStartupRegistration();

            // A new version is installed when the user asks: the app exits and starts again updated.
            var updates = _services.GetRequiredService<UpdateChecker>();
            updates.RestartRequested += (_, _) => Exit();
            updates.Start();

            if (launch.Instance is { } instance)
            {
                instance.Activated += (_, _) => Dispatcher.UIThread.Post(() => ShowMainWindow());
                instance.Listen();
            }

            // Stop the background import cleanly: a copy in progress is discarded and resumes next time.
            // It runs off the UI thread, which is blocked here waiting for it.
            desktop.Exit += (_, _) =>
            {
                _tray.Dispose();
                Task.Run(() => _services.GetRequiredService<LibrarySession>().DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(10));
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void ShowMainWindow(NavPage? page = null)
    {
        if (_desktop is null || _viewModel is null)
        {
            return;
        }

        if (_window is null)
        {
            _window = new MainWindow { DataContext = _viewModel };
            _window.Closing += OnWindowClosing;
            _desktop.MainWindow = _window;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        if (page is { } target)
        {
            _viewModel.ShowPage(target);
        }
    }

    public void Exit()
    {
        _exiting = true;
        _desktop?.Shutdown();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exiting || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown || _viewModel is null)
        {
            return;
        }

        e.Cancel = true;
        switch (_viewModel.OnCloseRequested(_tray?.IsSupported == true))
        {
            case CloseAction.Hide:
                _window?.Hide();
                break;
            case CloseAction.Exit:
                Exit();
                break;
            case CloseAction.Ask:
                // The window shows the question; the answer comes through CloseChosen.
                break;
        }
    }

    private void ApplyTheme(AppTheme theme) => RequestedThemeVariant = theme switch
    {
        AppTheme.Light => ThemeVariant.Light,
        AppTheme.System => ThemeVariant.Default,
        _ => ThemeVariant.Dark,
    };

    /// <summary>The start entry points at this executable, which may have moved since it was saved.</summary>
    private void ApplyStartupRegistration()
    {
        var services = _services!;
        var mode = services.GetRequiredService<ISettingsService>().Current.Background.StartWithSystem;
        var startup = services.GetRequiredService<IStartupRegistration>();
        if (mode != StartupMode.Off && startup.IsSupported)
        {
            startup.Apply(mode);
        }
    }
}
