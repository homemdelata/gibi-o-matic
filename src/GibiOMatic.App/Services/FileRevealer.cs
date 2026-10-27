using System.Diagnostics;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Library.Index;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.App.Services;

/// <summary>Shows a file or a folder in the system's file manager (Explorer on Windows).</summary>
public interface IFileRevealer
{
    /// <summary>The book's main original, selected in its folder; the folder alone when the file is missing.</summary>
    void RevealBook(LibraryInfo library, CatalogBook book);
}

public sealed class FileRevealer(ILogger<FileRevealer> logger) : IFileRevealer
{
    public void RevealBook(LibraryInfo library, CatalogBook book)
    {
        var folder = Path.Combine(library.RootPath, book.Folder);
        var file = book.MainFile is { } main ? Path.Combine(folder, main.Name) : null;
        if (file is not null && File.Exists(file))
        {
            Reveal(file);
            return;
        }

        // The nearest folder that still exists (the book's, else one above it).
        var existing = folder;
        while (!Directory.Exists(existing) && Path.GetDirectoryName(existing) is { } parent)
        {
            existing = parent;
        }

        Open(existing);
    }

    private void Reveal(string file)
    {
        if (OperatingSystem.IsWindows())
        {
            Start("explorer.exe", $"/select,\"{file}\"");
        }
        else
        {
            // xdg-open cannot select a file: the folder it is in opens.
            Open(Path.GetDirectoryName(file)!);
        }
    }

    private void Open(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            Start("explorer.exe", $"\"{folder}\"");
        }
        else
        {
            Start("xdg-open", folder);
        }
    }

    private void Start(string program, string arguments)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not open {Program} {Arguments}", program, arguments);
        }
    }
}
