using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaDevices;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Destinations.Kindle;

/// <summary>
/// Kindles from 2024 on use MTP, not USB storage (docs/RESEARCH.md). On Windows the
/// <c>MediaDevices</c> package talks to them through Windows Portable Devices. Paths on the device
/// look like <c>\Internal Storage\documents\Saga v01.mobi</c>.
/// </summary>
public sealed class MtpKindleDevices(ILogger<MtpKindleDevices> logger) : IKindleDevices
{
    public bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(7);

    public IReadOnlyList<KindleDeviceInfo> Find() => OperatingSystem.IsWindowsVersionAtLeast(7) ? FindOnWindows() : [];

    public IKindleConnection Connect(string deviceId) =>
        OperatingSystem.IsWindowsVersionAtLeast(7)
            ? ConnectOnWindows(deviceId)
            : throw new KindleDeviceException("Sending to a Kindle is only available on Windows for now.");

    [SupportedOSPlatform("windows7.0")]
    private IReadOnlyList<KindleDeviceInfo> FindOnWindows()
    {
        try
        {
            var found = new List<KindleDeviceInfo>();
            foreach (var device in MediaDevice.GetDevices())
            {
                using (device)
                {
                    var name = Safe(() => device.FriendlyName) is { Length: > 0 } friendly ? friendly : Safe(() => device.Description) ?? "Kindle";
                    var text = string.Join(' ', name, Safe(() => device.Description), Safe(() => device.Manufacturer), Safe(() => device.Model));
                    if (text.Contains("Kindle", StringComparison.OrdinalIgnoreCase) || text.Contains("Amazon", StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(new KindleDeviceInfo(device.DeviceId, name));
                    }
                }
            }

            return found;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Looking for portable devices failed");
            return [];
        }
    }

    [SupportedOSPlatform("windows7.0")]
    private IKindleConnection ConnectOnWindows(string deviceId)
    {
        var device = MediaDevice.GetDevices().FirstOrDefault(d => d.DeviceId == deviceId)
                     ?? throw new KindleDeviceException("The Kindle is no longer connected.");
        try
        {
            device.Connect(MediaDeviceAccess.Default, MediaDeviceShare.Default, false);
            var documents = FindDocuments(device)
                            ?? throw new KindleDeviceException("The Kindle's documents folder was not found. Is it unlocked and set to transfer files?");
            return new Connection(device, documents, logger);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException)
        {
            device.Dispose();
            throw new KindleDeviceException($"The Kindle could not be opened: {ex.Message}", ex);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    /// <summary>The <c>documents</c> folder of the first storage that has one.</summary>
    [SupportedOSPlatform("windows7.0")]
    private static (string Path, MediaDriveInfo? Drive)? FindDocuments(MediaDevice device)
    {
        foreach (var drive in device.GetDrives() ?? [])
        {
            var path = drive.RootDirectory.FullName.TrimEnd('\\') + "\\documents";
            if (device.DirectoryExists(path))
            {
                return (path, drive);
            }
        }

        foreach (var storage in device.GetDirectories("\\"))
        {
            var path = storage.TrimEnd('\\') + "\\documents";
            if (device.DirectoryExists(path))
            {
                return (path, null);
            }
        }

        return null;
    }

    private static string? Safe(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows7.0")]
    private sealed class Connection(MediaDevice device, (string Path, MediaDriveInfo? Drive) documents, ILogger logger) : IKindleConnection
    {
        private readonly Lock _gate = new();

        public string Name { get; } = device.FriendlyName is { Length: > 0 } name ? name : "Kindle";

        public KindleSpace? Space()
        {
            lock (_gate)
            {
                try
                {
                    return documents.Drive is { } drive ? new KindleSpace(drive.AvailableFreeSpace, drive.TotalSize) : null;
                }
                catch (COMException)
                {
                    return null;
                }
            }
        }

        public IReadOnlyList<KindleFile> ListBooks()
        {
            lock (_gate)
            {
                return Run(() => device.GetDirectoryInfo(documents.Path)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Select(f => (File: f, Relative: Relative(f.FullName)))
                    .Where(f => KindleBooks.IsBook(f.Relative) && !KindleBooks.IsInNotesFolder(f.Relative))
                    .Select(f => new KindleFile(f.Relative, (long)f.File.Length, f.File.LastWriteTime))
                    .OrderBy(f => f.Path, StringComparer.CurrentCultureIgnoreCase)
                    .ToList());
            }
        }

        public bool Exists(string path)
        {
            lock (_gate)
            {
                return Run(() => device.FileExists(Full(path)));
            }
        }

        /// <summary>Written under a temporary name and renamed, so the Kindle never indexes half a book.</summary>
        public void Upload(string localFile, string path, IProgress<double>? progress, CancellationToken ct)
        {
            lock (_gate)
            {
                var full = Full(path);
                var partial = full + ".part";
                Run(() =>
                {
                    var folder = full[..full.LastIndexOf('\\')];
                    if (!device.DirectoryExists(folder))
                    {
                        device.CreateDirectory(folder);
                    }

                    if (device.FileExists(partial))
                    {
                        device.DeleteFile(partial);
                    }

                    try
                    {
                        using (var input = new ProgressStream(File.OpenRead(localFile), progress, ct))
                        {
                            device.UploadFile(input, partial);
                        }

                        if (device.FileExists(full))
                        {
                            device.DeleteFile(full);
                        }

                        device.Rename(partial, full[(full.LastIndexOf('\\') + 1)..]);
                    }
                    catch
                    {
                        TryDelete(partial);
                        throw;
                    }

                    return true;
                });
            }
        }

        public void Delete(string path)
        {
            lock (_gate)
            {
                var full = Full(path);
                var notes = Full(KindleBooks.NotesFolderOf(path));
                Run(() =>
                {
                    if (device.FileExists(full))
                    {
                        device.DeleteFile(full);
                    }

                    if (device.DirectoryExists(notes))
                    {
                        device.DeleteDirectory(notes, true);
                    }

                    return true;
                });
            }
        }

        private void TryDelete(string full)
        {
            try
            {
                if (device.FileExists(full))
                {
                    device.DeleteFile(full);
                }
            }
            catch (COMException ex)
            {
                logger.LogWarning(ex, "Could not remove the partial file {File} from the Kindle", full);
            }
        }

        private string Full(string relative) => documents.Path + "\\" + KindleBooks.Checked(relative).Replace('/', '\\');

        private string Relative(string full) => full[(documents.Path.Length + 1)..].Replace('\\', '/');

        private static T Run<T>(Func<T> action)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException or System.IO.FileNotFoundException)
            {
                throw new KindleDeviceException($"The Kindle did not answer: {ex.Message}", ex);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                try
                {
                    if (device.IsConnected)
                    {
                        device.Disconnect();
                    }
                }
                catch (COMException)
                {
                    // Unplugged already.
                }

                device.Dispose();
            }
        }
    }

    /// <summary>Reports how much of the file was read (sent) and stops when cancelled.</summary>
    private sealed class ProgressStream(Stream inner, IProgress<double>? progress, CancellationToken ct) : Stream
    {
        private readonly long _length = inner.Length;
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ct.ThrowIfCancellationRequested();
            var n = inner.Read(buffer, offset, count);
            _read += n;
            progress?.Report(_length == 0 ? 1 : (double)_read / _length);
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
