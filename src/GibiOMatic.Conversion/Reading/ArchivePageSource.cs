using GibiOMatic.Conversion.Archives;
using GibiOMatic.Conversion.Cbz;
using SharpCompress.Archives;

namespace GibiOMatic.Conversion.Reading;

/// <summary>
/// CBZ and CBR pages, in the same order as the reading copies (natural sort of the names). A solid
/// RAR can only be read from the start, so its pages are loaded into memory once, in the
/// background, as they come; a page that has not arrived yet waits for it.
/// </summary>
internal sealed class ArchivePageSource : EncodedPageSource
{
    private readonly IArchive _archive;
    private readonly List<IArchiveEntry> _pages;
    private readonly object _lock = new();

    // Solid archives only.
    private readonly byte[]?[]? _loaded;
    private readonly CancellationTokenSource _loading = new();
    private Exception? _loadError;
    private bool _loadFinished;

    public ArchivePageSource(string path)
    {
        _archive = ArchiveFactory.Open(path);
        _pages = _archive.Entries
            .Where(e => !e.IsDirectory && ImageFiles.IsImage(e.Key) && !CbzBuilder.IsHidden(e.Key))
            .OrderBy(e => e.Key, NaturalStringComparer.Instance)
            .ToList();
        if (_archive.IsSolid)
        {
            _loaded = new byte[_pages.Count][];
            var ct = _loading.Token;
            _ = Task.Run(() => LoadSolid(path, ct), ct);
        }
    }

    public override int PageCount => _pages.Count;

    protected override byte[] Read(int index, int limit, CancellationToken ct)
    {
        if (_loaded is null)
        {
            lock (_lock)
            {
                using var stream = _pages[index].OpenEntryStream();
                return ReadAll(stream, limit);
            }
        }

        lock (_loaded)
        {
            while (_loaded[index] is null)
            {
                if (_loadError is not null)
                {
                    throw new InvalidDataException("The archive could not be read.", _loadError);
                }

                if (_loadFinished)
                {
                    throw new InvalidDataException($"Page {index + 1} was not found in the archive.");
                }

                ct.ThrowIfCancellationRequested();
                Monitor.Wait(_loaded, 200);
            }

            return _loaded[index]!;
        }
    }

    private void LoadSolid(string path, CancellationToken ct)
    {
        var positions = _pages.Select((e, i) => (e.Key!, i)).ToDictionary(p => p.Item1, p => p.i);
        try
        {
            // A second handle, so the entry list of the first stays usable.
            using var archive = ArchiveFactory.Open(path);
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.Entry.IsDirectory || reader.Entry.Key is not { } key || !positions.TryGetValue(key, out var index))
                {
                    continue;
                }

                using var stream = reader.OpenEntryStream();
                var data = ReadAll(stream, int.MaxValue);
                lock (_loaded!)
                {
                    _loaded[index] = data;
                    Monitor.PulseAll(_loaded);
                }
            }

            lock (_loaded!)
            {
                _loadFinished = true;
                Monitor.PulseAll(_loaded);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            lock (_loaded!)
            {
                _loadError = ex;
                Monitor.PulseAll(_loaded);
            }
        }
    }

    public override void Dispose()
    {
        _loading.Cancel();
        lock (_lock)
        {
            _archive.Dispose();
        }

        _loading.Dispose();
    }
}
