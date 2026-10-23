using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Media;
using GibiOMatic.App.Services;
using GibiOMatic.Core;
using GibiOMatic.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Velopack;

namespace GibiOMatic.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Installer hooks first (they run the app with special arguments and exit right away).
        var velopack = VelopackApp.Build();
        if (OperatingSystem.IsWindows())
        {
            velopack = velopack.OnBeforeUninstallFastCallback(_ => RemoveStartWithSystem());
        }

        velopack.Run();

        // One Gibi-o-matic per user: a second start brings the running one to the front and ends.
        using var instance = SingleInstance.Acquire();
        if (!instance.IsFirst)
        {
            if (OperatingSystem.IsWindows())
            {
                Services.Tray.Win32TrayIcon.AllowOtherProcessToTakeFocus();
            }

            return instance.SignalFirst(args, TimeSpan.FromSeconds(5)) ? 0 : 1;
        }

        var paths = AppPaths.ForCurrentUser();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(paths.LogsDirectory, "gibiomatic-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Log.Error(e.Exception, "Unobserved task exception");

        try
        {
            Log.Information("Starting Gibi-o-matic {Version}", AppVersion());
            var launch = new AppLaunch(args.Contains(StartupCommand.TrayArgument, StringComparer.OrdinalIgnoreCase), instance);
            using var services = ServiceRegistration.Build(paths, s => s.AddSingleton(launch));

            // Settings are read before the UI exists: the language must be set first.
            var settings = services.GetRequiredService<ISettingsService>().LoadAsync().GetAwaiter().GetResult();
            ApplyLanguage(settings.Language);

            return BuildAvaloniaApp(services).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Gibi-o-matic terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    // Used by the Avalonia designer/previewer.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(services: null);

    private static AppBuilder BuildAvaloniaApp(IServiceProvider? services) =>
        AppBuilder.Configure(() => new App(services))
            .UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = "avares://GibiOMatic/Assets/Fonts#IBM Plex Sans" })
            .LogToTrace();

    /// <summary>Uninstalling (Windows): the start-with-Windows entry would point at a deleted program.</summary>
    private static void RemoveStartWithSystem()
    {
        if (OperatingSystem.IsWindows())
        {
            new WindowsStartupRegistration(Microsoft.Extensions.Logging.Abstractions.NullLogger<WindowsStartupRegistration>.Instance)
                .Apply(StartupMode.Off);
        }
    }

    private static void ApplyLanguage(string language)
    {
        var culture = CultureInfo.GetCultureInfo(language);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    // The full version from the git tag (MinVer), e.g. "0.5.0" or "0.5.1-alpha.0.3+<commit>";
    // AssemblyVersion only keeps the major number.
    private static string AppVersion() =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";
}
