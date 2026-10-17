using System.Globalization;
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

    /// <summary>
    /// Saves a new version, first copying the current file to
    /// <c>.gibiomatic/history/&lt;id&gt;/&lt;timestamp&gt;.json</c> (the newest versions are kept).
    /// </summary>
    public static async Task WriteWithHistoryAsync(string libraryRoot, string path, BookMetadata metadata, CancellationToken ct = default)
    {
        if (File.Exists(path))
        {
            var historyFolder = Path.Combine(LibraryLayout.HistoryPath(libraryRoot), metadata.Id);
            Directory.CreateDirectory(historyFolder);
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
            await AtomicFile.WriteAllBytesAsync(Path.Combine(historyFolder, stamp + ".json"), await File.ReadAllBytesAsync(path, ct), ct);

            foreach (var old in Directory.GetFiles(historyFolder, "*.json").Order(StringComparer.Ordinal).SkipLast(LibraryLayout.HistoryLimit))
            {
                File.Delete(old);
            }
        }

        await WriteAsync(path, metadata, ct);
    }

    public static string Serialize(BookMetadata metadata) => JsonSerializer.Serialize(metadata, GibiJson.Options);

    public static BookMetadata Deserialize(string json) =>
        JsonSerializer.Deserialize<BookMetadata>(json, GibiJson.Options) ?? throw new JsonException("metadata.json is empty.");
}
