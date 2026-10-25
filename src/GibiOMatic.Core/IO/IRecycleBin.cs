namespace GibiOMatic.Core.IO;

/// <summary>The system's recycle bin (Windows) or trash (Linux desktops).</summary>
public interface IRecycleBin
{
    /// <summary>
    /// Sends the file to the recycle bin. Returns false, leaving the file where it is, when that is
    /// not possible: a drive without a recycle bin is never a reason to delete for good.
    /// </summary>
    bool TryRecycle(string path);
}
