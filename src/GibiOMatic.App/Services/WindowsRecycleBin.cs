using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GibiOMatic.Core.IO;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.App.Services;

/// <summary>
/// The Windows recycle bin, through <c>SHFileOperation</c> without any UI. Windows silently deletes
/// for good on drives without a recycle bin (network shares, USB sticks, Google Drive), so only
/// local NTFS and ReFS drives are accepted; anything else returns false and the caller keeps the file.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class WindowsRecycleBin(ILogger<WindowsRecycleBin> logger) : IRecycleBin
{
    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    public bool TryRecycle(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full) || !HasRecycleBin(full))
        {
            return false;
        }

        // A list of paths, each ending in a null, with one more null at the end.
        fixed (char* from = full + "\0\0")
        {
            var operation = new FileOperation
            {
                Function = FoDelete,
                From = from,
                Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi,
            };
            var result = SHFileOperationW(&operation);
            if (result != 0 || operation.AnyOperationsAborted != 0 || File.Exists(full))
            {
                logger.LogWarning("Could not send {Path} to the recycle bin (code {Result})", full, result);
                return false;
            }
        }

        return true;
    }

    private bool HasRecycleBin(string path)
    {
        try
        {
            // UNC paths have no drive letter, and shares have no recycle bin.
            var root = Path.GetPathRoot(path);
            if (root is null || root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return false;
            }

            var drive = new DriveInfo(root);
            return drive.DriveType == DriveType.Fixed && drive.DriveFormat is "NTFS" or "ReFS";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not inspect the drive of {Path}", path);
            return false;
        }
    }

    // SHFILEOPSTRUCTW with the 64-bit layout (the app ships only for x64 and arm64).
    [StructLayout(LayoutKind.Sequential)]
    private struct FileOperation
    {
        public nint Window;
        public uint Function;
        public char* From;
        public char* To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public nint NameMappings;
        public char* ProgressTitle;
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHFileOperationW(FileOperation* operation);
}
