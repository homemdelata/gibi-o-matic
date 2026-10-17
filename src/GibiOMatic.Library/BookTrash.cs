using System.Globalization;
using System.Text.Json;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.IO;
using GibiOMatic.Core.Ids;
using GibiOMatic.Core.Json;

namespace GibiOMatic.Library;

/// <summary>
/// The library's internal trash (<c>.gibiomatic/trash</c>). Originals that leave a book are moved
/// here, never deleted; each one gets a small JSON note saying where it came from.
/// </summary>
public static class BookTrash
{
    public sealed record TrashNote(string BookId, string BookTitle, string OriginalPath, BookFile File, string Reason, DateTimeOffset MovedAt);

    /// <summary>Moves <paramref name="filePath"/> to a new folder of the trash and returns its new path.</summary>
    public static async Task<string> MoveAsync(string libraryRoot, BookMetadata book, string filePath, BookFile file, string reason, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var folder = Path.Combine(LibraryLayout.TrashPath(libraryRoot),
            $"{now.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)}-{Ulid.ShortId(book.Id)}");
        Directory.CreateDirectory(folder);

        var target = Path.Combine(folder, LibraryNaming.Unique(folder, Path.GetFileName(filePath)));
        File.Move(filePath, target);

        var note = new TrashNote(book.Id, book.Title, Path.GetRelativePath(libraryRoot, filePath).Replace('\\', '/'), file, reason, now);
        await AtomicFile.WriteAllTextAsync(target + ".trash.json", JsonSerializer.Serialize(note, GibiJson.Options), ct);
        return target;
    }
}
