using System.Globalization;
using GibiOMatic.Conversion;
using GibiOMatic.Conversion.Images;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.IO;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Library.Index;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Library.Editing;

/// <summary>A saved version of a book's metadata in <c>.gibiomatic/history</c>.</summary>
public sealed record HistoryEntry(string Path, DateTimeOffset SavedAt, BookMetadata Metadata);

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
    public async Task<CatalogBook> UpdateAsync(LibraryInfo library, string bookId, Func<BookMetadata, BookMetadata> change, CancellationToken ct = default)
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

            await MetadataFile.WriteWithHistoryAsync(library.RootPath, metadataPath, updated, ct);
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

        var entries = new List<HistoryEntry>();
        foreach (var file in Directory.GetFiles(folder, "*.json").OrderDescending(StringComparer.Ordinal))
        {
            try
            {
                var stamp = DateTimeOffset.ParseExact(Path.GetFileNameWithoutExtension(file), "yyyyMMdd'T'HHmmssfff'Z'",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                entries.Add(new HistoryEntry(file, stamp, await MetadataFile.ReadAsync(file, ct)));
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

    /// <summary>Originals are named "{Título}.{ext}" (SPEC §4.2): renaming is not changing their content.</summary>
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
