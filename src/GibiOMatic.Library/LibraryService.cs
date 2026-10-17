using System.Text.Json;
using GibiOMatic.Core;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Ids;
using GibiOMatic.Core.IO;
using GibiOMatic.Core.Json;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Library.Index;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Library;

public sealed class LibraryService(AppPaths paths, ILogger<LibraryService> logger) : ILibraryService
{
    public async Task<LibraryInfo> CreateAsync(string rootPath, CancellationToken ct = default)
    {
        var root = NormalizeRoot(rootPath);
        if (File.Exists(LibraryLayout.ManifestPath(root)))
        {
            throw new LibraryException(LibraryErrorKind.AlreadyALibrary, $"'{root}' already contains a library.");
        }

        Directory.CreateDirectory(LibraryLayout.BooksPath(root));
        Directory.CreateDirectory(LibraryLayout.HistoryPath(root));

        var manifest = new LibraryManifest { Id = Ulid.NewUlid(), CreatedAt = DateTimeOffset.UtcNow };
        await AtomicFile.WriteAllTextAsync(LibraryLayout.ManifestPath(root),
            JsonSerializer.Serialize(manifest, GibiJson.Options), ct);
        logger.LogInformation("Created library {LibraryId} at {Root}", manifest.Id, root);

        return await OpenAsync(root, ct);
    }

    public async Task<LibraryInfo> OpenAsync(string rootPath, CancellationToken ct = default)
    {
        var root = NormalizeRoot(rootPath);
        var manifest = await ReadManifestAsync(root, ct);
        var library = new LibraryInfo(manifest.Id, manifest.FormatVersion, root);

        // Recreate internal folders if they were removed; never touches books.
        Directory.CreateDirectory(LibraryLayout.BooksPath(root));
        Directory.CreateDirectory(LibraryLayout.HistoryPath(root));

        var index = IndexFor(library);
        if (await index.EnsureSchemaAsync(ct))
        {
            logger.LogInformation("Index for library {LibraryId} is new or was migrated; rebuilding", library.Id);
            await RebuildAsync(library, index, ct);
        }

        logger.LogInformation("Opened library {LibraryId} at {Root}", library.Id, root);
        return library;
    }

    public async Task<IndexRebuildResult> RebuildIndexAsync(LibraryInfo library, CancellationToken ct = default)
    {
        var index = IndexFor(library);
        await index.EnsureSchemaAsync(ct);
        return await RebuildAsync(library, index, ct);
    }

    public Task<int> CountBooksAsync(LibraryInfo library, CancellationToken ct = default) =>
        IndexFor(library).CountBooksAsync(ct);

    /// <summary>The local index of an open library.</summary>
    public LibraryIndex GetIndex(LibraryInfo library) => IndexFor(library);

    private LibraryIndex IndexFor(LibraryInfo library) =>
        new(Path.Combine(paths.IndexDirectory, library.Id + ".db"));

    private async Task<IndexRebuildResult> RebuildAsync(LibraryInfo library, LibraryIndex index, CancellationToken ct)
    {
        var books = new List<IndexedBook>();
        var issues = new List<IndexIssue>();
        var seenIds = new Dictionary<string, string>(StringComparer.Ordinal);

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(LibraryLayout.BooksPath(library.RootPath), LibraryLayout.MetadataFileName, options))
        {
            ct.ThrowIfCancellationRequested();
            var relativeFolder = Path.GetRelativePath(library.RootPath, Path.GetDirectoryName(file)!).Replace('\\', '/');
            var relativeFile = relativeFolder + "/" + LibraryLayout.MetadataFileName;

            BookMetadata metadata;
            try
            {
                metadata = await MetadataFile.ReadAsync(file, ct);
            }
            catch (JsonException ex)
            {
                issues.Add(new IndexIssue(relativeFile, $"Invalid JSON: {ex.Message}"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(metadata.Id) || string.IsNullOrWhiteSpace(metadata.Title))
            {
                issues.Add(new IndexIssue(relativeFile, "Missing id or title."));
                continue;
            }

            if (seenIds.TryGetValue(metadata.Id, out var otherFile))
            {
                issues.Add(new IndexIssue(relativeFile, $"Duplicate id {metadata.Id} (also in {otherFile})."));
                continue;
            }

            seenIds.Add(metadata.Id, relativeFile);
            books.Add(new IndexedBook(metadata, relativeFolder, File.GetLastWriteTimeUtc(file)));
        }

        await index.ReplaceAllAsync(books, ct);
        foreach (var issue in issues)
        {
            logger.LogWarning("Not indexed: {File}: {Message}", issue.RelativePath, issue.Message);
        }

        logger.LogInformation("Rebuilt index for library {LibraryId}: {Count} books, {Issues} issues",
            library.Id, books.Count, issues.Count);
        return new IndexRebuildResult(books.Count, issues);
    }

    /// <summary>
    /// True when the folder already has files or folders the user can see (hidden and system
    /// entries such as desktop.ini do not count). Used to warn before creating a library there.
    /// </summary>
    public static bool HasVisibleContent(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return false;
        }

        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.Hidden | FileAttributes.System, IgnoreInaccessible = true };
        return Directory.EnumerateFileSystemEntries(folder, "*", options).Any(e => !Path.GetFileName(e).StartsWith('.'));
    }

    private static string NormalizeRoot(string rootPath) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));

    private static async Task<LibraryManifest> ReadManifestAsync(string root, CancellationToken ct)
    {
        var path = LibraryLayout.ManifestPath(root);
        if (!File.Exists(path))
        {
            throw new LibraryException(LibraryErrorKind.NotALibrary, $"'{root}' does not contain {LibraryLayout.ManifestFileName}.");
        }

        LibraryManifest? manifest;
        try
        {
            await using var stream = File.OpenRead(path);
            manifest = await JsonSerializer.DeserializeAsync<LibraryManifest>(stream, GibiJson.Options, ct);
        }
        catch (JsonException ex)
        {
            throw new LibraryException(LibraryErrorKind.InvalidManifest, $"'{path}' is not valid JSON.", ex);
        }

        if (manifest is null || !Ulid.IsValid(manifest.Id))
        {
            throw new LibraryException(LibraryErrorKind.InvalidManifest, $"'{path}' has no valid library id.");
        }

        if (manifest.FormatVersion > LibraryManifest.CurrentFormatVersion)
        {
            throw new LibraryException(LibraryErrorKind.UnsupportedFormatVersion,
                $"Library format {manifest.FormatVersion} is newer than supported ({LibraryManifest.CurrentFormatVersion}).");
        }

        return manifest;
    }
}
