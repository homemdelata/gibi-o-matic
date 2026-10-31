using System.Globalization;
using System.Text.Json;
using GibiOMatic.Conversion;
using GibiOMatic.Conversion.Images;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.IO;
using GibiOMatic.Core.Json;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Library.Index;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Library.Editing;

/// <summary>A saved version of a book's metadata in <c>.gibiomatic/history</c>.</summary>
/// <param name="Batch">The batch that replaced this version (not undone yet), if any.</param>
public sealed record HistoryEntry(string Path, DateTimeOffset SavedAt, BookMetadata Metadata, HistoryBatch? Batch = null);

/// <summary>
/// Every change to a book goes through here (SPEC §7): the previous <c>metadata.json</c> goes to the
/// history, the folder and the files follow the new title, publisher and series, and the index is
/// updated. Originals are only renamed, never rewritten.
/// </summary>
public sealed class BookEditor(LibraryService libraries, IBookFileReader reader, ILogger<BookEditor> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>A book's metadata or cover was saved (destinations regenerate its copies).</summary>
    public event EventHandler<BookSavedEventArgs>? BookSaved;

    /// <summary>A book was taken out of the library (destinations delete their copies).</summary>
    public event EventHandler<BookSavedEventArgs>? BookRemoved;

    /// <summary>Applies <paramref name="change"/> to the book as it is on disk and saves it. Returns the saved book.</summary>
    public Task<CatalogBook> UpdateAsync(LibraryInfo library, string bookId, Func<BookMetadata, BookMetadata> change, CancellationToken ct = default) =>
        UpdateAsync(library, bookId, change, null, ct);

    /// <summary>Saves a change whose history entry joins <paramref name="batch"/>, to be undone with it.</summary>
    public async Task<CatalogBook> UpdateAsync(LibraryInfo library, string bookId, Func<BookMetadata, BookMetadata> change,
        ICollection<HistoryBatchEntry>? batch, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var index = libraries.GetIndex(library);
            var entry = await index.GetBookAsync(bookId, ct) ?? throw new InvalidOperationException($"Book {bookId} is not in the index.");
            var folderPath = Path.Combine(library.RootPath, entry.Folder);
            var metadataPath = Path.Combine(folderPath, LibraryLayout.MetadataFileName);
            var current = File.Exists(metadataPath) ? await MetadataFile.ReadAsync(metadataPath, ct) : entry.Metadata;

            var updated = change(current) with { Id = current.Id, Schema = BookMetadata.CurrentSchema, CreatedAt = current.CreatedAt };
            if (MetadataFile.Serialize(updated with { UpdatedAt = current.UpdatedAt }) == MetadataFile.Serialize(current))
            {
                return entry;
            }

            updated = updated with { UpdatedAt = DateTimeOffset.UtcNow };
            if (updated.Title != current.Title)
            {
                updated = updated with { Files = RenameFiles(folderPath, updated.Files, updated.Title) };
            }

            if (await MetadataFile.WriteWithHistoryAsync(library.RootPath, metadataPath, updated, ct) is { } history)
            {
                batch?.Add(new HistoryBatchEntry(bookId, history));
            }

            var folder = BookFolders.MoveIfNeeded(library.RootPath, entry.Folder, LibraryNaming.BookFolder(updated), logger);
            var saved = new IndexedBook(updated, folder, File.GetLastWriteTimeUtc(Path.Combine(library.RootPath, folder, LibraryLayout.MetadataFileName)));
            await index.UpsertBookAsync(saved, ct);
            logger.LogInformation("Saved book {BookId} ({Title})", updated.Id, updated.Title);
            BookSaved?.Invoke(this, new BookSavedEventArgs(library.Id, updated.Id));
            return new CatalogBook(updated, folder);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Takes a book out of the library, at the user's request: its folder (original, cover,
    /// metadata.json) goes to the internal trash, or is deleted along with its history. A folder
    /// that is already gone only leaves the index.
    /// </summary>
    public async Task RemoveAsync(LibraryInfo library, string bookId, BookRemoval removal, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var index = libraries.GetIndex(library);
            var entry = await index.GetBookAsync(bookId, ct) ?? throw new InvalidOperationException($"Book {bookId} is not in the index.");
            var folderPath = Path.GetFullPath(Path.Combine(library.RootPath, entry.Folder));
            var books = Path.TrimEndingDirectorySeparator(Path.GetFullPath(LibraryLayout.BooksPath(library.RootPath))) + Path.DirectorySeparatorChar;
            if (!folderPath.StartsWith(books, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"The folder of book {bookId} ({entry.Folder}) is not inside {LibraryLayout.BooksFolderName}.");
            }

            if (Directory.Exists(folderPath))
            {
                if (removal == BookRemoval.DeleteFiles)
                {
                    Directory.Delete(folderPath, recursive: true);
                }
                else
                {
                    await BookTrash.MoveFolderAsync(library.RootPath, entry.Metadata, folderPath, "removed from the library", ct);
                }

                BookFolders.RemoveEmptyParents(library.RootPath, folderPath);
            }

            var history = Path.Combine(LibraryLayout.HistoryPath(library.RootPath), bookId);
            if (removal == BookRemoval.DeleteFiles && Directory.Exists(history))
            {
                Directory.Delete(history, recursive: true);
            }

            await index.DeleteBookAsync(bookId, ct);
            logger.LogInformation("Removed book {BookId} ({Title}) from the library: {Removal}", bookId, entry.Metadata.Title, removal);
            BookRemoved?.Invoke(this, new BookSavedEventArgs(library.Id, bookId));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Saved versions, newest first.</summary>
    public async Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(LibraryInfo library, string bookId, CancellationToken ct = default)
    {
        var folder = Path.Combine(LibraryLayout.HistoryPath(library.RootPath), bookId);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var batches = (await GetBatchesAsync(library, ct))
            .Where(b => b.UndoneAt is null)
            .SelectMany(b => b.Entries.Where(e => e.BookId == bookId).Select(e => (e.HistoryFile, Batch: b)))
            .DistinctBy(x => x.HistoryFile)
            .ToDictionary(x => x.HistoryFile, x => x.Batch, StringComparer.Ordinal);
        var entries = new List<HistoryEntry>();
        foreach (var file in Directory.GetFiles(folder, "*.json").OrderDescending(StringComparer.Ordinal))
        {
            try
            {
                var stamp = DateTimeOffset.ParseExact(Path.GetFileNameWithoutExtension(file), "yyyyMMdd'T'HHmmssfff'Z'",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                entries.Add(new HistoryEntry(file, stamp, await MetadataFile.ReadAsync(file, ct), batches.GetValueOrDefault(Path.GetFileName(file))));
            }
            catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
            {
                logger.LogWarning(ex, "Ignoring unreadable history file {File}", file);
            }
        }

        return entries;
    }

    /// <summary>
    /// Undo: brings back the metadata of a saved version (the current one goes to the history too).
    /// The list of files stays as it is now, since originals are never moved back.
    /// </summary>
    public Task<CatalogBook> RestoreAsync(LibraryInfo library, string bookId, HistoryEntry version, CancellationToken ct = default) =>
        UpdateAsync(library, bookId, current => version.Metadata with { Files = current.Files }, ct);

    /// <summary>Records a batch of saves, so it can be undone at once.</summary>
    public async Task SaveBatchAsync(LibraryInfo library, HistoryBatch batch, CancellationToken ct = default)
    {
        var folder = LibraryLayout.BatchesPath(library.RootPath);
        Directory.CreateDirectory(folder);
        await AtomicFile.WriteAllTextAsync(Path.Combine(folder, batch.Id + ".json"), JsonSerializer.Serialize(batch, GibiJson.Options), ct);

        // Only recent batches can still be undone: the history keeps the last versions of each book.
        foreach (var old in Directory.GetFiles(folder, "*.json").Order(StringComparer.Ordinal).SkipLast(BatchLimit))
        {
            File.Delete(old);
        }
    }

    private const int BatchLimit = 50;

    /// <summary>The recorded batches, newest first.</summary>
    public async Task<IReadOnlyList<HistoryBatch>> GetBatchesAsync(LibraryInfo library, CancellationToken ct = default)
    {
        var folder = LibraryLayout.BatchesPath(library.RootPath);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var batches = new List<HistoryBatch>();
        foreach (var file in Directory.GetFiles(folder, "*.json").OrderDescending(StringComparer.Ordinal))
        {
            try
            {
                if (JsonSerializer.Deserialize<HistoryBatch>(await File.ReadAllTextAsync(file, ct), GibiJson.Options) is { } batch)
                {
                    batches.Add(batch);
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Ignoring unreadable batch file {File}", file);
            }
        }

        return batches;
    }

    /// <summary>
    /// Undoes a batch: every book gets back its version from before it (the current one goes to the
    /// history too). Books removed since, or whose version left the history, are skipped. Returns the
    /// books restored.
    /// </summary>
    public async Task<IReadOnlyList<string>> UndoBatchAsync(LibraryInfo library, string batchId, CancellationToken ct = default)
    {
        var batch = (await GetBatchesAsync(library, ct)).FirstOrDefault(b => b.Id == batchId);
        if (batch is null || batch.UndoneAt is not null)
        {
            return [];
        }

        var index = libraries.GetIndex(library);
        var restored = new List<string>();
        foreach (var entry in batch.Entries)
        {
            var file = Path.Combine(LibraryLayout.HistoryPath(library.RootPath), entry.BookId, entry.HistoryFile);
            if (!File.Exists(file) || await index.GetBookAsync(entry.BookId, ct) is null)
            {
                logger.LogWarning("Batch {BatchId}: book {BookId} can no longer be undone", batchId, entry.BookId);
                continue;
            }

            var version = await MetadataFile.ReadAsync(file, ct);
            await UpdateAsync(library, entry.BookId, current => version with { Files = current.Files }, ct);
            restored.Add(entry.BookId);
        }

        await SaveBatchAsync(library, batch with { UndoneAt = DateTimeOffset.UtcNow }, ct);
        logger.LogInformation("Undid batch {BatchId} ({Label}): {Count} books", batchId, batch.Label, restored.Count);
        return restored;
    }

    /// <summary>Replaces <c>cover.jpg</c> with an image file chosen by the user.</summary>
    public async Task<bool> SetCoverAsync(LibraryInfo library, string bookId, string imagePath, CancellationToken ct = default)
    {
        var jpeg = CoverImages.CreateCover(await File.ReadAllBytesAsync(imagePath, ct));
        return jpeg is not null && await WriteCoverAsync(library, bookId, jpeg, ct);
    }

    /// <summary>Extracts the cover again from the main original (first page / declared cover).</summary>
    public async Task<bool> UseFirstPageAsync(LibraryInfo library, string bookId, CancellationToken ct = default)
    {
        var entry = await libraries.GetIndex(library).GetBookAsync(bookId, ct);
        if (entry?.MainFile is not { } main)
        {
            return false;
        }

        var content = await reader.ReadAsync(Path.Combine(library.RootPath, entry.Folder, main.Name), main.Format, ct);
        return content.Cover is { } raw && CoverImages.CreateCover(raw) is { } jpeg && await WriteCoverAsync(library, bookId, jpeg, ct);
    }

    private async Task<bool> WriteCoverAsync(LibraryInfo library, string bookId, byte[] jpeg, CancellationToken ct)
    {
        var entry = await libraries.GetIndex(library).GetBookAsync(bookId, ct);
        if (entry is null)
        {
            return false;
        }

        await AtomicFile.WriteAllBytesAsync(Path.Combine(library.RootPath, entry.Folder, LibraryLayout.CoverFileName), jpeg, ct);
        logger.LogInformation("Changed the cover of book {BookId}", bookId);
        BookSaved?.Invoke(this, new BookSavedEventArgs(library.Id, bookId));
        return true;
    }

    /// <summary>Originals are named "{Title}.{ext}" (SPEC §4.2): renaming is not changing their content.</summary>
    private IReadOnlyList<BookFile> RenameFiles(string folderPath, IReadOnlyList<BookFile> files, string title)
    {
        var renamed = new List<BookFile>();
        foreach (var file in files)
        {
            var wanted = LibraryNaming.FileName(title, file.Format);
            var source = Path.Combine(folderPath, file.Name);
            if (string.Equals(wanted, file.Name, StringComparison.Ordinal) || !File.Exists(source))
            {
                renamed.Add(file);
                continue;
            }

            var target = string.Equals(wanted, file.Name, StringComparison.OrdinalIgnoreCase) ? wanted : LibraryNaming.Unique(folderPath, wanted);
            try
            {
                File.Move(source, Path.Combine(folderPath, target));
                renamed.Add(file with { Name = target });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not rename {File}", source);
                renamed.Add(file);
            }
        }

        return renamed;
    }
}

/// <summary>What happens to the files of a book taken out of the library.</summary>
public enum BookRemoval
{
    /// <summary>The book folder goes to <c>.gibiomatic/trash</c>, where it can be recovered by hand.</summary>
    ToTrash,

    /// <summary>The book folder and its history are deleted for good.</summary>
    DeleteFiles,
}

public sealed class BookSavedEventArgs(string libraryId, string bookId) : EventArgs
{
    public string LibraryId { get; } = libraryId;
    public string BookId { get; } = bookId;
}
