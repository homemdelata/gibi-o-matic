using System.Collections.Concurrent;
using GibiOMatic.Conversion.Images;
using GibiOMatic.Core;
using GibiOMatic.Core.IO;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Library.Index;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Library;

/// <summary>
/// Small cover images for the grid and the inbox, kept in the local data folder
/// (<c>%LOCALAPPDATA%\GibiOMatic\thumbs\&lt;library id&gt;</c>), never in the library.
/// </summary>
public sealed class ThumbnailCache(AppPaths paths, ILogger<ThumbnailCache> logger)
{
    private readonly ConcurrentDictionary<string, Task<string?>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    private string FolderOf(LibraryInfo library) => Path.Combine(paths.ThumbnailsDirectory, library.Id);

    public string InboxThumbnailPath(LibraryInfo library, long itemId) => Path.Combine(FolderOf(library), $"inbox-{itemId}.jpg");

    public Task SaveInboxThumbnailAsync(LibraryInfo library, long itemId, byte[] jpeg, CancellationToken ct) =>
        AtomicFile.WriteAllBytesAsync(InboxThumbnailPath(library, itemId), jpeg, ct);

    public void DeleteInboxThumbnails(LibraryInfo library, IEnumerable<long> itemIds)
    {
        foreach (var id in itemIds)
        {
            try
            {
                File.Delete(InboxThumbnailPath(library, id));
            }
            catch (IOException ex)
            {
                logger.LogDebug(ex, "Could not delete inbox thumbnail {Id}", id);
            }
        }
    }

    /// <summary>
    /// Path of the book's thumbnail, created from its <c>cover.jpg</c> when missing or outdated.
    /// Returns <c>null</c> when the book has no cover.
    /// </summary>
    public Task<string?> GetBookThumbnailAsync(LibraryInfo library, CatalogBook book, CancellationToken ct = default)
    {
        var thumbnail = Path.Combine(FolderOf(library), book.Metadata.Id + ".jpg");
        var cover = Path.Combine(library.RootPath, book.Folder, LibraryLayout.CoverFileName);
        return _inFlight.GetOrAdd(thumbnail, _ => Task.Run(() => Ensure(cover, thumbnail), ct))
            .ContinueWith(t =>
            {
                _inFlight.TryRemove(thumbnail, out _);
                return t.IsCompletedSuccessfully ? t.Result : null;
            }, TaskScheduler.Default);
    }

    private string? Ensure(string cover, string thumbnail)
    {
        try
        {
            if (!File.Exists(cover))
            {
                return null;
            }

            if (File.Exists(thumbnail) && File.GetLastWriteTimeUtc(thumbnail) >= File.GetLastWriteTimeUtc(cover))
            {
                return thumbnail;
            }

            var jpeg = CoverImages.CreateThumbnail(File.ReadAllBytes(cover));
            if (jpeg is null)
            {
                return null;
            }

            AtomicFile.WriteAllBytesAsync(thumbnail, jpeg).GetAwaiter().GetResult();
            return thumbnail;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not create the thumbnail of {Cover}", cover);
            return null;
        }
    }
}
