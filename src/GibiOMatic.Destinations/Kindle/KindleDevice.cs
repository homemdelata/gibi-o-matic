namespace GibiOMatic.Destinations.Kindle;

/// <summary>A Kindle seen on USB.</summary>
public sealed record KindleDeviceInfo(string Id, string Name);

/// <summary>A book in the device's <c>documents</c> folder.</summary>
/// <param name="Path">Relative to <c>documents</c>, with <c>/</c> (e.g. <c>Saga v01 (2012).mobi</c>).</param>
public sealed record KindleFile(string Path, long Bytes, DateTime? Modified)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

public sealed record KindleSpace(long Free, long Total);

public sealed class KindleDeviceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Finds Kindles connected by USB (MTP on Windows; Linux is still to do).</summary>
public interface IKindleDevices
{
    bool IsSupported { get; }

    IReadOnlyList<KindleDeviceInfo> Find();

    IKindleConnection Connect(string deviceId);
}

/// <summary>An open Kindle: its books, and copying to and deleting from <c>documents</c>.</summary>
public interface IKindleConnection : IDisposable
{
    string Name { get; }

    KindleSpace? Space();

    /// <summary>Book files under <c>documents</c> (the Kindle's own <c>.sdr</c> folders are left out).</summary>
    IReadOnlyList<KindleFile> ListBooks();

    bool Exists(string path);

    /// <summary>Copies a file; an existing file with that name is replaced (its <c>.sdr</c> notes stay).</summary>
    void Upload(string localFile, string path, IProgress<double>? progress, CancellationToken ct);

    /// <summary>Deletes a book and the folder of notes and reading position the Kindle keeps for it.</summary>
    void Delete(string path);
}

/// <summary>Files a Kindle reads as books (what the manager lists).</summary>
public static class KindleBooks
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mobi", ".azw", ".azw3", ".azw4", ".kfx", ".prc", ".pdf", ".epub", ".txt", ".docx",
    };

    public static bool IsBook(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary><c>Saga v01.mobi</c> → <c>Saga v01.sdr</c>, next to it.</summary>
    public static string NotesFolderOf(string path) => Path.ChangeExtension(path, ".sdr").Replace('\\', '/');

    /// <summary>Paths inside the Kindle's notes folders, which are not books.</summary>
    public static bool IsInNotesFolder(string path) =>
        path.Replace('\\', '/').Split('/').SkipLast(1).Any(segment => segment.EndsWith(".sdr", StringComparison.OrdinalIgnoreCase));

    /// <summary>Refuses anything that would leave <c>documents</c> ("..", rooted paths).</summary>
    public static string Checked(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 || normalized.Split('/').Any(s => s is "" or "." or "..") || Path.IsPathRooted(path))
        {
            throw new KindleDeviceException($"'{path}' is not a file inside the Kindle's documents folder.");
        }

        return normalized;
    }
}
