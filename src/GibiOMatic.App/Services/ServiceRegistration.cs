using GibiOMatic.App.ViewModels;
using GibiOMatic.Core;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Core.Secrets;
using GibiOMatic.Core.Settings;
using GibiOMatic.Library;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        services.AddSingleton<ILibraryService, LibraryService>();

        // UI services
        services.AddSingleton<IFolderPicker, AvaloniaFolderPicker>();
        services.AddSingleton<LibrarySession>();

        // View models (a new shell and pages for each opened library)
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<WelcomeViewModel>();
        services.AddTransient<ShellViewModel>();
        services.AddTransient<LibraryPageViewModel>();
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
