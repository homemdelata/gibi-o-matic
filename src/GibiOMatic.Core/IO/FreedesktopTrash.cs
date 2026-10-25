using System.Globalization;
using System.Text;

namespace GibiOMatic.Core.IO;

/// <summary>
/// The home trash of the freedesktop.org specification (GNOME, KDE…): the file goes to
/// <c>Trash/files</c> and a <c>.trashinfo</c> in <c>Trash/info</c> records where it came from, so the
/// file manager can restore it.
/// </summary>
public sealed class FreedesktopTrash(string trashFolder) : IRecycleBin
{
    /// <summary><c>$XDG_DATA_HOME/Trash</c>, or <c>~/.local/share/Trash</c>.</summary>
    public static string DefaultFolder
    {
        get
        {
            var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrEmpty(dataHome) || !Path.IsPathRooted(dataHome))
            {
                dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            }

            return Path.Combine(dataHome, "Trash");
        }
    }

    public bool TryRecycle(string path)
    {
        var source = Path.GetFullPath(path);
        if (!File.Exists(source))
        {
            return false;
        }

        try
        {
            var files = Directory.CreateDirectory(Path.Combine(trashFolder, "files")).FullName;
            var info = Directory.CreateDirectory(Path.Combine(trashFolder, "info")).FullName;

            // The .trashinfo is created first, exclusively: it reserves the name (as the specification asks).
            var name = Path.GetFileName(source);
            for (var n = 2; ; n++)
            {
                var infoPath = Path.Combine(info, name + ".trashinfo");
                try
                {
                    using (var stream = new FileStream(infoPath, FileMode.CreateNew, FileAccess.Write))
                    {
                        stream.Write(Encoding.UTF8.GetBytes(InfoFile(source)));
                    }

                    try
                    {
                        // A file on another drive is copied and then deleted by File.Move.
                        File.Move(source, Path.Combine(files, name));
                        return true;
                    }
                    catch
                    {
                        File.Delete(infoPath);
                        throw;
                    }
                }
                catch (IOException) when (File.Exists(infoPath) || File.Exists(Path.Combine(files, name)))
                {
                    name = $"{Path.GetFileNameWithoutExtension(source)} ({n}){Path.GetExtension(source)}";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string InfoFile(string source)
    {
        // The path is URL-encoded segment by segment; the date is local time, without a zone.
        var encoded = string.Join('/', source.Split('/').Select(Uri.EscapeDataString));
        var date = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        return $"[Trash Info]\nPath={encoded}\nDeletionDate={date}\n";
    }
}
