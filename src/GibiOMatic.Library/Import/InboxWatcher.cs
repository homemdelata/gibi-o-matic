using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Library.Import;

public enum InboxWatchStatus
{
    /// <summary>No inbox folder in the settings.</summary>
    Off,
    Watching,

    /// <summary>The folder does not exist (a drive not connected); watching starts when it appears.</summary>
    Missing,
}

/// <summary>
/// Watches the inbox folder (SPEC §6, F5): a book that appears there, or in a subfolder, goes to the
/// inbox queue once it stops changing, so a download in progress is left alone. The folder is also
/// scanned when watching starts and every few minutes, because change events are lost while the
/// app is closed, when the event buffer overflows and on some network or cloud drives.
/// </summary>
public sealed class InboxWatcher : IDisposable
{
    private readonly ImportQueue _queue;
    private readonly ILogger<InboxWatcher> _logger;
    private readonly ConcurrentDictionary<string, Candidate> _candidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _timer;
    private DateTimeOffset _nextScan;
    private int _ticking;
    private bool _disposed;

    public InboxWatcher(ImportQueue queue, InboxWatchOptions options, ILogger<InboxWatcher> logger)
    {
        _queue = queue;
        _logger = logger;
        SettleTime = options.SettleTime;
        PollInterval = options.PollInterval;
        RescanInterval = options.RescanInterval;
    }

    /// <summary>How long a file must stay the same size before it is picked up.</summary>
    public TimeSpan SettleTime { get; }

    /// <summary>How often files in the making and a missing folder are looked at.</summary>
    public TimeSpan PollInterval { get; }

    public TimeSpan RescanInterval { get; }

    public string? Folder { get; private set; }

    public InboxWatchStatus Status { get; private set; }

    /// <summary>Raised on a background thread when <see cref="Status"/> or <see cref="Folder"/> changes.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Starts watching <paramref name="folder"/> (null stops), replacing the previous one.</summary>
    public void Watch(string? folder)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            StopWatcher();
            _candidates.Clear();
            Folder = string.IsNullOrWhiteSpace(folder) ? null : Path.GetFullPath(folder);
            Status = Folder is null ? InboxWatchStatus.Off : InboxWatchStatus.Missing;
            if (Folder is not null)
            {
                TryStartWatcher();
                _timer ??= new Timer(_ => _ = TickAsync(), null, PollInterval, PollInterval);
            }
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Looks for new files and hands over the complete ones now (the timer calls it; tests too).</summary>
    public async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            var statusChanged = false;
            lock (_gate)
            {
                if (_disposed || Folder is null)
                {
                    return;
                }

                // A folder on a drive that was not connected, or that was removed.
                if (Status == InboxWatchStatus.Missing && Directory.Exists(Folder))
                {
                    statusChanged = TryStartWatcher();
                }
                else if (Status == InboxWatchStatus.Watching && !Directory.Exists(Folder))
                {
                    _logger.LogWarning("The inbox folder {Folder} is gone", Folder);
                    StopWatcher();
                    Status = InboxWatchStatus.Missing;
                    statusChanged = true;
                }
            }

            if (statusChanged)
            {
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }

            if (Status == InboxWatchStatus.Watching && DateTimeOffset.UtcNow >= _nextScan)
            {
                Scan();
            }

            await HandOverSettledAsync();
        }
        catch (Exception ex)
        {
            // The queue may be closing with the library; the next tick tries again.
            _logger.LogWarning(ex, "Inbox watcher tick failed");
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    /// <summary>Must be called with the gate held. Returns whether watching started.</summary>
    private bool TryStartWatcher()
    {
        if (Folder is null || !Directory.Exists(Folder))
        {
            return false;
        }

        try
        {
            var watcher = new FileSystemWatcher(Folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Created += OnFileEvent;
            watcher.Changed += OnFileEvent;
            watcher.Renamed += OnFileEvent;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Without events the periodic scan still finds new files.
            _logger.LogWarning(ex, "Could not watch {Folder} for changes; scanning it periodically instead", Folder);
        }

        Status = InboxWatchStatus.Watching;
        _nextScan = DateTimeOffset.MinValue;
        _logger.LogInformation("Watching the inbox folder {Folder}", Folder);
        return true;
    }

    private void StopWatcher()
    {
        if (_watcher is { } watcher)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            _watcher = null;
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (Folder is { } folder && InboxFolder.IsCandidate(folder, e.FullPath) && !IsInLibrary(e.FullPath))
        {
            // Written after the app started: a file that failed before is tried again.
            _candidates.AddOrUpdate(e.FullPath, _ => new Candidate(RetryFailed: true), (_, c) => c with { RetryFailed = true });
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.LogWarning(e.GetException(), "The inbox watcher lost events; scanning the folder again");
        _nextScan = DateTimeOffset.MinValue;
    }

    private void Scan()
    {
        _nextScan = DateTimeOffset.UtcNow + RescanInterval;
        if (Folder is not { } folder)
        {
            return;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", options))
            {
                if (InboxFolder.IsCandidate(folder, file) && !IsInLibrary(file))
                {
                    _candidates.TryAdd(file, new Candidate(RetryFailed: false));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not scan the inbox folder {Folder}", folder);
        }
    }

    /// <summary>Files whose size and date stayed the same for <see cref="SettleTime"/> and that nobody is writing.</summary>
    private async Task HandOverSettledAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var ready = new List<(string Path, bool RetryFailed)>();
        foreach (var (path, candidate) in _candidates)
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                _candidates.TryRemove(path, out _);
                continue;
            }

            var length = info.Length;
            var written = info.LastWriteTimeUtc;
            if (candidate.Since is null || length != candidate.Length || written != candidate.Written || length == 0)
            {
                // Some browsers create the final file empty and fill it at the end.
                _candidates[path] = candidate with { Length = length, Written = written, Since = now };
                continue;
            }

            if (now - candidate.Since >= SettleTime && IsClosed(path))
            {
                _candidates.TryRemove(path, out _);
                ready.Add((path, candidate.RetryFailed));
            }
        }

        if (ready.Count == 0)
        {
            return;
        }

        var added = await _queue.AddAutoAsync(ready.Where(r => r.RetryFailed).Select(r => r.Path), retryFailed: true)
                    + await _queue.AddAutoAsync(ready.Where(r => !r.RetryFailed).Select(r => r.Path), retryFailed: false);
        if (added > 0)
        {
            _logger.LogInformation("Inbox watcher: {Count} new files", added);
        }
    }

    /// <summary>The settings refuse an inbox around the library; this is a second guard (files there are never opened).</summary>
    private bool IsInLibrary(string file) => InboxFolder.IsInside(_queue.Library.RootPath, file);

    /// <summary>Whether no other program has the file open for writing.</summary>
    private static bool IsClosed(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            StopWatcher();
            _timer?.Dispose();
            _timer = null;
        }
    }

    private sealed record Candidate(bool RetryFailed, long Length = -1, DateTime Written = default, DateTimeOffset? Since = null);
}

/// <summary>Timings of the inbox watcher and of the batch summary (shorter in tests).</summary>
public sealed record InboxWatchOptions
{
    /// <summary>How long a file must stay the same size before it is picked up.</summary>
    public TimeSpan SettleTime { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How often files in the making and a missing folder are looked at.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Full scans of the folder, for events that were lost.</summary>
    public TimeSpan RescanInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Files arriving closer than this make one batch (one notification).</summary>
    public TimeSpan QuietTime { get; init; } = TimeSpan.FromSeconds(5);
}
