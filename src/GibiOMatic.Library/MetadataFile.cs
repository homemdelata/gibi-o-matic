using System.Text.Json;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.IO;
using GibiOMatic.Core.Json;

namespace GibiOMatic.Library;

/// <summary>Reads and writes a book's <c>metadata.json</c>.</summary>
public static class MetadataFile
{
    public static async Task<BookMetadata> ReadAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<BookMetadata>(stream, GibiJson.Options, ct)
               ?? throw new JsonException("metadata.json is empty.");
    }

    public static Task WriteAsync(string path, BookMetadata metadata, CancellationToken ct = default) =>
        AtomicFile.WriteAllTextAsync(path, Serialize(metadata), ct);

    public static string Serialize(BookMetadata metadata) => JsonSerializer.Serialize(metadata, GibiJson.Options);
}
