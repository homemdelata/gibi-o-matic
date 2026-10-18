using System.Text.Json;
using GibiOMatic.Core.Json;

namespace GibiOMatic.Destinations.Kindle;

/// <summary>A book copied to the Kindle: which file, with which content, when.</summary>
/// <param name="DeviceFile">Path inside the device's <c>documents</c> folder.</param>
public sealed record KindleSent(string BookId, string Title, string DeviceFile, string ContentHash, DateTimeOffset SentAt);

/// <summary>
/// What the app sent to the Kindle, one small file per book in the destination's local folder
/// (<c>.sent/&lt;bookId&gt;.json</c>). It tells a sent book from one still to send, which device files
/// are the app's own (shown in the Kindle manager), and an outdated copy on the device.
/// </summary>
public static class KindleSentStore
{
    private const string FolderName = ".sent";

    public static KindleSent? Read(string folder, string bookId)
    {
        var path = PathOf(folder, bookId);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<KindleSent>(File.ReadAllText(path), GibiJson.Options) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static IReadOnlyList<KindleSent> All(string folder)
    {
        var sent = Path.Combine(folder, FolderName);
        if (!Directory.Exists(sent))
        {
            return [];
        }

        return Directory.EnumerateFiles(sent, "*.json")
            .Select(f => Read(folder, Path.GetFileNameWithoutExtension(f)))
            .OfType<KindleSent>()
            .ToList();
    }

    public static void Write(string folder, KindleSent sent)
    {
        var path = PathOf(folder, sent.BookId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(sent, GibiJson.Options));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The user deleted the book's file from the device.</summary>
    public static void Forget(string folder, string bookId)
    {
        var path = PathOf(folder, bookId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    // Book ids are ULIDs: safe as file names.
    private static string PathOf(string folder, string bookId) => Path.Combine(folder, FolderName, bookId + ".json");
}
