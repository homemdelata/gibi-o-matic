using System.Security.Cryptography;
using System.Text;
using GibiOMatic.Core.IO;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Metadata.Http;

/// <summary>
/// Downloads the small cover images of candidates (to compare with the book in the Review) and
/// keeps them in <c>%LOCALAPPDATA%\GibiOMatic\cache\covers</c>. Only HTTPS images from the known
/// sources are fetched, one at a time, with the app's User-Agent.
/// </summary>
public sealed class CoverImageCache(HttpClient http, string directory, ILogger<CoverImageCache> logger)
{
    private const long MaxBytes = 5 * 1024 * 1024;

    private static readonly string[] AllowedHosts =
        ["comicvine.gamespot.com", "covers.openlibrary.org", "books.google.com", "books.googleusercontent.com"];

    private readonly RateLimiter _limiter = new(TimeSpan.FromMilliseconds(500), 600);

    /// <summary>Local path of the image, downloading it the first time; <c>null</c> if it cannot be had.</summary>
    public async Task<string?> GetAsync(string? url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !AllowedHosts.Any(h => uri.Host == h || uri.Host.EndsWith("." + h, StringComparison.Ordinal)))
        {
            return null;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
        var file = Path.Combine(directory, hash[..2], hash + ".img");
        if (File.Exists(file))
        {
            return file;
        }

        try
        {
            await _limiter.WaitAsync("covers", ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(SourceClient.UserAgent);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.Ordinal) != true
                || response.Content.Headers.ContentLength > MaxBytes)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0 || bytes.Length > MaxBytes)
            {
                return null;
            }

            await AtomicFile.WriteAllBytesAsync(file, bytes, ct);
            return file;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogInformation(ex, "Could not download cover {Url}", uri);
            return null;
        }
    }
}
