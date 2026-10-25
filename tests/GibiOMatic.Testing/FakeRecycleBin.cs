using GibiOMatic.Core.IO;

namespace GibiOMatic.Testing;

/// <summary>A recycle bin that moves files into a folder of the test, never the real one.</summary>
public sealed class FakeRecycleBin(string folder) : IRecycleBin
{
    /// <summary>False simulates a drive without a recycle bin.</summary>
    public bool Available { get; set; } = true;

    /// <summary>Names of the files received, in order.</summary>
    public List<string> Recycled { get; } = [];

    public bool TryRecycle(string path)
    {
        if (!Available || !File.Exists(path))
        {
            return false;
        }

        Directory.CreateDirectory(folder);
        var name = Path.GetFileName(path);
        File.Move(path, Path.Combine(folder, $"{Recycled.Count}-{name}"));
        Recycled.Add(name);
        return true;
    }
}
