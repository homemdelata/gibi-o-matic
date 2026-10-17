using Microsoft.Extensions.Logging;

namespace GibiOMatic.Library;

/// <summary>Moves a book folder when its publisher, series or title change (SPEC §4.2).</summary>
public static class BookFolders
{
    /// <summary>Moves the folder and removes parent folders left empty. Returns the folder in use (relative, '/').</summary>
    public static string MoveIfNeeded(string root, string current, string wanted, ILogger logger)
    {
        if (string.Equals(current, wanted, StringComparison.Ordinal))
        {
            return current;
        }

        var from = Path.Combine(root, current);
        var to = Path.Combine(root, wanted);
        var caseOnly = string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(to) && !caseOnly)
        {
            logger.LogWarning("Not moving {From}: {To} already exists", current, wanted);
            return current;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (caseOnly)
            {
                // Windows needs a detour to change only the case of a folder name.
                var detour = from + ".moving";
                Directory.Move(from, detour);
                Directory.Move(detour, to);
            }
            else
            {
                Directory.Move(from, to);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not move {From} to {To}", current, wanted);
            return current;
        }

        RemoveEmptyParents(root, from);
        logger.LogInformation("Moved book folder {From} to {To}", current, wanted);
        return wanted;
    }

    private static void RemoveEmptyParents(string root, string from)
    {
        var books = Path.GetFullPath(LibraryLayout.BooksPath(root));
        for (var parent = Path.GetDirectoryName(from); parent is not null && Path.GetFullPath(parent).Length > books.Length; parent = Path.GetDirectoryName(parent))
        {
            if (!Directory.Exists(parent) || Directory.EnumerateFileSystemEntries(parent).Any())
            {
                break;
            }

            Directory.Delete(parent);
        }
    }
}
