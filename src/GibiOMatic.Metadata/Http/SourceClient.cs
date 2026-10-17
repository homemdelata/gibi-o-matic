using System.Net;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Metadata.Http;

/// <summary>The source refused the request (bad key, quota) or kept failing after the retries.</summary>
public sealed class MetadataSourceException(string source, MetadataSourceError error, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string SourceId { get; } = source;
    public MetadataSourceError Error { get; } = error;
}

public enum MetadataSourceError
{
    Unavailable,
    InvalidKey,
    RateLimited,
    BadResponse,
}

/// <summary>Retry policy of a source; the defaults are for production, tests shrink them.</summary>
public sealed record RetryPolicy(int Attempts, TimeSpan BaseDelay, TimeSpan RateLimitDelay)
{
    public static RetryPolicy Default { get; } = new(4, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(2));
}

/// <summary>
/// GET with the rules every external API must follow (CLAUDE.md, rule 5): own User-Agent, local
/// cache, request spacing and retries with backoff. Secrets in the query string are never logged
/// nor used as cache keys.
/// </summary>
public sealed class SourceClient(
    string source,
    HttpClient http,
    ResponseCache cache,
    RateLimiter limiter,
    RetryPolicy retry,
    TimeSpan cacheMaxAge,
    ILogger logger)
{
    public const string UserAgent = "GibiOMatic/1.0 (+https://github.com/homemdelata/gibi-o-matic; personal comics library)";

    public RateLimiter Limiter => limiter;

    /// <summary>
    /// Returns the response body. <paramref name="publicUrl"/> is the URL without secrets (cache
    /// key, logs); <paramref name="secretQuery"/> is appended only to the request actually sent.
    /// <paramref name="isRateLimited"/> lets a source flag a 200 response that is a quota error.
    /// </summary>
    public async Task<string> GetAsync(string resource, string publicUrl, string? secretQuery, CancellationToken ct,
        Func<string, bool>? isRateLimited = null, Func<string, bool>? isCacheable = null)
    {
        if (await cache.GetAsync(source, publicUrl, cacheMaxAge, ct) is { } cached)
        {
            return cached;
        }

        var requestUrl = secretQuery is null ? publicUrl : publicUrl + (publicUrl.Contains('?') ? "&" : "?") + secretQuery;
        Exception? last = null;
        for (var attempt = 1; attempt <= retry.Attempts; attempt++)
        {
            await limiter.WaitAsync(resource, ct);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                request.Headers.UserAgent.ParseAdd(UserAgent);
                request.Headers.Accept.ParseAdd("application/json");
                using var response = await http.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);

                if (response.StatusCode is HttpStatusCode.TooManyRequests or (HttpStatusCode)420 || isRateLimited?.Invoke(body) == true)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? retry.RateLimitDelay * attempt;
                    limiter.BlockUntil(DateTimeOffset.UtcNow + wait);
                    logger.LogWarning("{Source} rate limit on {Url}; waiting {Wait}", source, publicUrl, wait);
                    last = new MetadataSourceException(source, MetadataSourceError.RateLimited, "Rate limited.");
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new MetadataSourceException(source, MetadataSourceError.InvalidKey, $"HTTP {(int)response.StatusCode}");
                }

                if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
                {
                    last = new MetadataSourceException(source, MetadataSourceError.Unavailable, $"HTTP {(int)response.StatusCode}");
                    logger.LogInformation("{Source} answered {Status} for {Url} (attempt {Attempt})", source, (int)response.StatusCode, publicUrl, attempt);
                }
                else if (!response.IsSuccessStatusCode)
                {
                    throw new MetadataSourceException(source, MetadataSourceError.BadResponse, $"HTTP {(int)response.StatusCode}");
                }
                else
                {
                    if (isCacheable?.Invoke(body) ?? true)
                    {
                        await cache.SetAsync(source, publicUrl, body, ct);
                    }

                    return body;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                last = ex;
                logger.LogInformation(ex, "{Source} request failed for {Url} (attempt {Attempt})", source, publicUrl, attempt);
            }

            if (attempt < retry.Attempts)
            {
                await Task.Delay(retry.BaseDelay * Math.Pow(2, attempt - 1), ct);
            }
        }

        throw last as MetadataSourceException
              ?? new MetadataSourceException(source, MetadataSourceError.Unavailable, $"{source} is not responding.", last);
    }
}
