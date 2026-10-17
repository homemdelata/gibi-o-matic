using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GibiOMatic.Core.IO;

namespace GibiOMatic.Metadata.Http;

/// <summary>
/// Local copy of API responses (<c>%LOCALAPPDATA%\GibiOMatic\cache\&lt;source&gt;</c>), so a book is
/// never looked up twice in a short time and re-identifying the collection is cheap. Keys are
/// URLs without the API key.
/// </summary>
public sealed class ResponseCache(string directory)
{
    private sealed record Entry(string Url, DateTimeOffset FetchedAt, string Body);

    public async Task<string?> GetAsync(string source, string url, TimeSpan maxAge, CancellationToken ct)
    {
        var file = FileFor(source, url);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            var entry = JsonSerializer.Deserialize<Entry>(await File.ReadAllTextAsync(file, ct));
            return entry is not null && entry.Url == url && DateTimeOffset.UtcNow - entry.FetchedAt <= maxAge ? entry.Body : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task SetAsync(string source, string url, string body, CancellationToken ct) =>
        AtomicFile.WriteAllTextAsync(FileFor(source, url), JsonSerializer.Serialize(new Entry(url, DateTimeOffset.UtcNow, body)), ct);

    private string FileFor(string source, string url)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        return Path.Combine(directory, source, hash[..2], hash + ".json");
    }
}
