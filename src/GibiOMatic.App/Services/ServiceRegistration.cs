using GibiOMatic.App.ViewModels;
using GibiOMatic.Conversion;
using GibiOMatic.Core;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Core.Secrets;
using GibiOMatic.Core.Settings;
using GibiOMatic.Destinations;
using GibiOMatic.Conversion.Kcc;
using GibiOMatic.Destinations.Kindle;
using GibiOMatic.Destinations.Komga;
using GibiOMatic.Destinations.Sync;
using GibiOMatic.Library;
using GibiOMatic.Library.Editing;
using GibiOMatic.Library.Identification;
using GibiOMatic.Library.Import;
using GibiOMatic.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace GibiOMatic.App.Services;

/// <summary>Composition root.</summary>
public static class ServiceRegistration
{
    public static ServiceProvider Build(AppPaths paths) =>
        new ServiceCollection()
            .AddGibiOMatic(paths, BuildConfiguration())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

    public static IServiceCollection AddGibiOMatic(this IServiceCollection services, AppPaths paths, IConfiguration configuration)
    {
        services.AddLogging(builder => builder.AddSerilog(dispose: false));
        services.AddSingleton(configuration);
        services.AddSingleton(paths);

        // Core and library
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<ISecretStore, DpapiSecretStore>();
        }
        else
        {
            services.AddSingleton<ISecretStore, UnsupportedSecretStore>();
        }

        services.AddSingleton<LibraryService>();
        services.AddSingleton<ILibraryService>(sp => sp.GetRequiredService<LibraryService>());
        services.AddSingleton<IBookFileReader, BookFileReader>();
        services.AddSingleton<ThumbnailCache>();
        services.AddSingleton<ImportAnalyzer>();
        services.AddSingleton<BookImporter>();
        services.AddSingleton<ImportQueueFactory>();
        services.AddMetadataSources(paths);
        services.AddSingleton<BookEditor>();
        services.AddSingleton<IdentificationQueueFactory>();
        services.AddSingleton(sp => new KomgaClient(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }, sp.GetRequiredService<ILogger<KomgaClient>>()));
        services.AddSingleton<IDestinationType, KomgaDestinationType>();
        services.AddSingleton<IKccRunner, KccRunner>();
        services.AddSingleton<IDestinationType, KindleDestinationType>();
        services.AddSingleton<IKindleDevices, MtpKindleDevices>();
        // The KCC download is big: no overall timeout, the user can cancel.
        services.AddSingleton(sp => new KccInstaller(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, paths, sp.GetRequiredService<ILogger<KccInstaller>>()));
        services.AddSingleton<DestinationServiceFactory>();

        // UI services
        services.AddSingleton<IUiThread, AvaloniaUiThread>();
        services.AddSingleton<IFolderPicker, AvaloniaFolderPicker>();
        services.AddSingleton<IFilePicker, AvaloniaFilePicker>();
        services.AddSingleton<ICoverLoader, CoverLoader>();
        services.AddSingleton<IUrlOpener, AvaloniaUrlOpener>();
        services.AddSingleton<IReaderImages, AvaloniaReaderImages>();
        services.AddSingleton<IReaderLauncher, ReaderWindowLauncher>();
        services.AddSingleton<ReadingTracker>();
        services.AddSingleton<KindleWatcher>();
        services.AddSingleton<LibrarySession>();

        // View models (a new shell and pages for each opened library)
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<WelcomeViewModel>();
        services.AddTransient<ShellViewModel>();
        services.AddTransient<LibraryPageViewModel>();
        services.AddTransient<InboxPageViewModel>();
        services.AddTransient<ReviewPageViewModel>();
        services.AddTransient<EditorPageViewModel>();
        services.AddTransient<DestinationsPageViewModel>();
        services.AddTransient<SettingsPageViewModel>();
        services.AddSingleton<Func<WelcomeViewModel>>(sp => sp.GetRequiredService<WelcomeViewModel>);
        services.AddSingleton<Func<ShellViewModel>>(sp => sp.GetRequiredService<ShellViewModel>);

        return services;
    }

    /// <summary>
    /// The ComicVine key comes from the environment (CI, other developers) or, in Debug builds,
    /// from .NET user secrets. In production it is typed in Settings and kept in the secret store.
    /// </summary>
    private static IConfiguration BuildConfiguration()
    {
        var builder = new ConfigurationBuilder();
#if DEBUG
        builder.AddUserSecrets(typeof(ServiceRegistration).Assembly, optional: true);
#endif
        var envKey = Environment.GetEnvironmentVariable("GIBIOMATIC_COMICVINE_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            builder.AddInMemoryCollection([new KeyValuePair<string, string?>("ComicVine:ApiKey", envKey)]);
        }

        return builder.Build();
    }
}
