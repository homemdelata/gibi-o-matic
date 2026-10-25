using Avalonia.Media.Imaging;

namespace GibiOMatic.App.Services;

public interface ICoverLoader
{
    /// <summary>Decodes an image file at <paramref name="decodeWidth"/> pixels wide; <c>null</c> if it cannot be read.</summary>
    Task<Bitmap?> LoadAsync(string path, int decodeWidth, CancellationToken ct = default);
}

/// <summary>
/// Decodes cover images off the UI thread and keeps the most recent ones, so scrolling back
/// does not decode again. Older entries are dropped and left to the garbage collector.
/// </summary>
public sealed class CoverLoader : ICoverLoader
{
    private const int Capacity = 400;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, Bitmap Bitmap)>> _map = [];
    private readonly LinkedList<(string Key, Bitmap Bitmap)> _recent = new();

    public async Task<Bitmap?> LoadAsync(string path, int decodeWidth, CancellationToken ct = default)
    {
        DateTime modified;
        try
        {
            // Off the UI thread: the library may be on a slow drive (Google Drive wakes up on first access).
            modified = await Task.Run(() => File.GetLastWriteTimeUtc(path), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var key = $"{path}|{decodeWidth}|{modified.Ticks}";
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _recent.Remove(node);
                _recent.AddFirst(node);
                return node.Value.Bitmap;
            }
        }

        var bitmap = await Task.Run(() =>
        {
            try
            {
                using var stream = File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.HighQuality);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                return null;
            }
        }, ct);

        if (bitmap is null)
        {
            return null;
        }

        lock (_gate)
        {
            if (!_map.ContainsKey(key))
            {
                _map[key] = _recent.AddFirst((key, bitmap));
                while (_recent.Count > Capacity)
                {
                    _map.Remove(_recent.Last!.Value.Key);
                    _recent.RemoveLast();
                }
            }
        }

        return bitmap;
    }
}
