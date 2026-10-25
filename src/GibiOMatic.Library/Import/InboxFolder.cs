namespace GibiOMatic.Library.Import;

/// <summary>Paths inside the watched inbox folder (SPEC §6).</summary>
public static class InboxFolder
{
    /// <summary>Where processed files go, inside the inbox (SPEC §6, item 6).</summary>
    public const string ImportedFolderName = "_imported";

    /// <summary>Files that may be books; the format is still confirmed by content.</summary>
    public static readonly IReadOnlySet<string> BookExtensions =
        new HashSet<string>([".pdf", ".cbz", ".cbr", ".epub", ".zip", ".rar"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A file of the inbox that should be picked up: a likely book, not hidden, not in <c>_imported</c>.
    /// Downloads in progress have their own extensions (.crdownload, .part…) and are left out by this too.
    /// </summary>
    public static bool IsCandidate(string inbox, string file)
    {
        var name = Path.GetFileName(file);
        if (name.StartsWith('.') || !BookExtensions.Contains(Path.GetExtension(file)))
        {
            return false;
        }

        var relative = RelativePath(inbox, file);
        return relative is not null && !IsInImportedFolder(relative);
    }

    /// <summary>
    /// Where a file of the inbox goes once processed, keeping its subfolders; null when the file is
    /// not inside the inbox or is already in <c>_imported</c>.
    /// </summary>
    public static string? ImportedPathFor(string inbox, string file)
    {
        var relative = RelativePath(inbox, file);
        return relative is null || IsInImportedFolder(relative)
            ? null
            : Path.Combine(Path.GetFullPath(inbox), ImportedFolderName, relative);
    }

    /// <summary>"Saga.pdf" → "Saga (2).pdf" when the name is taken.</summary>
    public static string Unique(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var folder = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(folder, $"{name} ({i}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// After a file left <paramref name="folder"/>: removes it, and then its parents, while they are
    /// completely empty, up to the inbox itself (never removed, nor anything outside it). A folder
    /// that still holds anything, even a download in progress, stays.
    /// </summary>
    public static void RemoveEmptyFolders(string inbox, string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(inbox));
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        while (IsInside(root, current) && Directory.Exists(current) && !Directory.EnumerateFileSystemEntries(current).Any())
        {
            try
            {
                // Not recursive: if something arrived meanwhile, it fails and the folder stays.
                Directory.Delete(current, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            current = Path.GetDirectoryName(current)!;
        }
    }

    /// <summary>Whether <paramref name="file"/> is somewhere inside <paramref name="folder"/>.</summary>
    public static bool IsInside(string folder, string file) => RelativePath(folder, file) is not null;

    private static string? RelativePath(string inbox, string file)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(inbox), Path.GetFullPath(file));
        // Another drive gives back the full path; a file outside the folder starts with "..".
        var outside = Path.IsPathRooted(relative) || relative is "." or ".."
                      || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        return outside ? null : relative;
    }

    private static bool IsInImportedFolder(string relative)
    {
        var separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return separator > 0 && string.Equals(relative[..separator], ImportedFolderName, StringComparison.OrdinalIgnoreCase);
    }
}
