using System.Globalization;
using Avalonia;
using Avalonia.Media;
using GibiOMatic.App.Services;
using GibiOMatic.Core;
using GibiOMatic.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace GibiOMatic.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
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
            Log.Information("Starting Gibi-o-matic {Version}", typeof(Program).Assembly.GetName().Version);
            using var services = ServiceRegistration.Build(paths);

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

    private static void ApplyLanguage(string language)
    {
        var culture = CultureInfo.GetCultureInfo(language);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
