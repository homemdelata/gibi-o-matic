using System.Globalization;
using System.Text.Json;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.IO;
using GibiOMatic.Core.Ids;
using GibiOMatic.Core.Json;

namespace GibiOMatic.Library;

/// <summary>
/// The library's internal trash (<c>.gibiomatic/trash</c>). Originals that leave a book, and whole
/// books removed from the library, are moved here; each one gets a small JSON note saying where it came from.
/// </summary>
public static class BookTrash
{
    public sealed record TrashNote(string BookId, string BookTitle, string OriginalPath, BookFile File, string Reason, DateTimeOffset MovedAt);

    /// <param name="OriginalFolder">The book folder, relative to the library root.</param>
    public sealed record FolderTrashNote(string BookId, string BookTitle, string OriginalFolder, string Reason, DateTimeOffset MovedAt);

    /// <summary>Moves <paramref name="filePath"/> to a new folder of the trash and returns its new path.</summary>
    public static async Task<string> MoveAsync(string libraryRoot, BookMetadata book, string filePath, BookFile file, string reason, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var folder = NewFolder(libraryRoot, book, now);

        var target = Path.Combine(folder, LibraryNaming.Unique(folder, Path.GetFileName(filePath)));
        File.Move(filePath, target);

        var note = new TrashNote(book.Id, book.Title, Path.GetRelativePath(libraryRoot, filePath).Replace('\\', '/'), file, reason, now);
        await AtomicFile.WriteAllTextAsync(target + ".trash.json", JsonSerializer.Serialize(note, GibiJson.Options), ct);
        return target;
    }

    /// <summary>Moves a whole book folder to a new folder of the trash and returns its new path.</summary>
    public static async Task<string> MoveFolderAsync(string libraryRoot, BookMetadata book, string folderPath, string reason, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var folder = NewFolder(libraryRoot, book, now);
        var target = Path.Combine(folder, Path.GetFileName(Path.TrimEndingDirectorySeparator(folderPath)));
        Directory.Move(folderPath, target);

        var note = new FolderTrashNote(book.Id, book.Title, Path.GetRelativePath(libraryRoot, folderPath).Replace('\\', '/'), reason, now);
        await AtomicFile.WriteAllTextAsync(target + ".trash.json", JsonSerializer.Serialize(note, GibiJson.Options), ct);
        return target;
    }

    private static string NewFolder(string libraryRoot, BookMetadata book, DateTimeOffset now)
    {
        var folder = Path.Combine(LibraryLayout.TrashPath(libraryRoot),
            $"{now.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)}-{Ulid.ShortId(book.Id)}");
        Directory.CreateDirectory(folder);
        return folder;
    }
}
