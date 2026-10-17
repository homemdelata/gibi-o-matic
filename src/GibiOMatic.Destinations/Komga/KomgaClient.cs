using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Destinations.Komga;

/// <summary>A Komga library as the server describes it.</summary>
/// <param name="OneShotsDirectory">The library's "One-Shots directory" option.</param>
public sealed record KomgaLibrary(string Id, string Name, string? Root, string? OneShotsDirectory);

public sealed class KomgaException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// The two Komga REST calls the app needs (checked in Komga's LibraryController, 2026-10-17):
/// <c>GET /api/v1/libraries</c> and <c>POST /api/v1/libraries/{id}/scan</c> (admin). Auth with the
/// <c>X-API-Key</c> header.
/// </summary>
public sealed class KomgaClient(HttpClient http, ILogger<KomgaClient> logger)
{
    private const string UserAgent = "GibiOMatic/1.0 (+https://github.com/homemdelata/gibi-o-matic)";

    public async Task<IReadOnlyList<KomgaLibrary>> GetLibrariesAsync(string serverUrl, string apiKey, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, serverUrl, "api/v1/libraries", apiKey, ct);
        var libraries = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (libraries.ValueKind != JsonValueKind.Array)
        {
            throw new KomgaException("Unexpected answer from Komga.");
        }

        return libraries.EnumerateArray().Select(l => new KomgaLibrary(
            l.GetProperty("id").GetString() ?? string.Empty,
            l.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
            l.TryGetProperty("root", out var root) ? root.GetString() : null,
            l.TryGetProperty("oneshotsDirectory", out var oneShots) && oneShots.ValueKind == JsonValueKind.String ? oneShots.GetString() : null)).ToList();
    }

    public async Task ScanAsync(string serverUrl, string apiKey, string libraryId, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Post, serverUrl, $"api/v1/libraries/{Uri.EscapeDataString(libraryId)}/scan", apiKey, ct);
        logger.LogInformation("Asked Komga to scan library {LibraryId}", libraryId);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string serverUrl, string path, string apiKey, CancellationToken ct)
    {
        if (!Uri.TryCreate(serverUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
        {
            throw new KomgaException("Invalid server address.");
        }

        using var request = new HttpRequestMessage(method, new Uri(baseUri, path));
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Add("X-API-Key", apiKey);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new KomgaException($"Komga did not answer: {ex.Message}");
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new KomgaException($"Komga answered HTTP {(int)status}.", status);
        }

        return response;
    }
}
