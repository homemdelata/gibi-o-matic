using GibiOMatic.Core;
using GibiOMatic.Core.Secrets;
using GibiOMatic.Metadata.Http;
using GibiOMatic.Metadata.Identification;
using GibiOMatic.Metadata.Sources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Metadata;

/// <summary>
/// The ComicVine key typed in Settings (kept with DPAPI) wins; otherwise <c>ComicVine:ApiKey</c>
/// from configuration (environment variable, or user secrets in Debug builds). Never logged.
/// </summary>
public sealed class ComicVineKeyProvider(ISecretStore secrets, IConfiguration configuration) : IComicVineKeyProvider
{
    public async Task<string?> GetKeyAsync(CancellationToken ct)
    {
        if (secrets.IsSupported && await secrets.GetAsync(SecretNames.ComicVineApiKey, ct) is { Length: > 0 } stored)
        {
            return stored;
        }

        return configuration["ComicVine:ApiKey"] is { Length: > 0 } configured ? configured : null;
    }

    /// <summary>True when the key comes from configuration (development), not from Settings.</summary>
    public async Task<bool> IsFromConfigurationAsync(CancellationToken ct) =>
        !(secrets.IsSupported && await secrets.GetAsync(SecretNames.ComicVineApiKey, ct) is { Length: > 0 })
        && configuration["ComicVine:ApiKey"] is { Length: > 0 };
}

public static class MetadataServices
{
    /// <summary>
    /// Registers the sources with their limits: ComicVine at most one request every 1.2 s and 190
    /// per resource per hour (its documented limit is 200), Open Library one per second.
    /// </summary>
    public static IServiceCollection AddMetadataSources(this IServiceCollection services, AppPaths paths, HttpMessageHandler? handler = null)
    {
        var cache = new ResponseCache(Path.Combine(paths.DataDirectory, "cache"));
        var http = handler is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(30) } : new HttpClient(handler, disposeHandler: false);

        services.AddSingleton<ComicVineKeyProvider>();
        services.AddSingleton<IComicVineKeyProvider>(sp => sp.GetRequiredService<ComicVineKeyProvider>());

        services.AddSingleton(sp => new OpenLibraryClient(new SourceClient(SourceIds.OpenLibrary, http, cache,
            new RateLimiter(TimeSpan.FromSeconds(1), 1000), RetryPolicy.Default, TimeSpan.FromDays(90),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<OpenLibraryClient>())));
        services.AddSingleton(sp => new GoogleBooksClient(new SourceClient(SourceIds.GoogleBooks, http, cache,
            new RateLimiter(TimeSpan.FromSeconds(1), 100), RetryPolicy.Default, TimeSpan.FromDays(30),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<GoogleBooksClient>())));
        services.AddSingleton(sp => new ComicVineClient(new SourceClient(SourceIds.ComicVine, http, cache,
            new RateLimiter(TimeSpan.FromSeconds(1.2), 190), RetryPolicy.Default, TimeSpan.FromDays(30),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<ComicVineClient>()), sp.GetRequiredService<IComicVineKeyProvider>()));

        services.AddSingleton<IIsbnSource>(sp => sp.GetRequiredService<OpenLibraryClient>());
        services.AddSingleton<IIsbnSource>(sp => sp.GetRequiredService<GoogleBooksClient>());
        services.AddSingleton<IComicVineApi>(sp => sp.GetRequiredService<ComicVineClient>());
        services.AddSingleton<IBookIdentifier, BookIdentifier>();
        services.AddSingleton<VolumeMatcher>();
        services.AddSingleton(sp => new CoverImageCache(http, Path.Combine(paths.DataDirectory, "cache", "covers"),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<CoverImageCache>()));
        return services;
    }
}
