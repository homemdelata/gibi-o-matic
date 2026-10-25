using System.Text;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Ids;

namespace GibiOMatic.Library;

/// <summary>
/// Folder and file names inside the library (SPEC §4.2):
/// <c>Livros/{Editora}/{Série}/{Título} [{id6}]/{Título}.{ext}</c>. Books without a series
/// skip the series level. Books not approved yet wait in <c>Livros/_a revisar/{Título} [{id6}]</c>,
/// since their publisher and series are still guesses.
/// </summary>
public static class LibraryNaming
{
    // Keeps paths well under Windows limits even with deep Drive folders.
    private const int MaxSegmentLength = 100;

    private static readonly HashSet<string> ReservedNames = new(
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Book folder relative to the library root, with '/' separators.</summary>
    public static string BookFolder(BookMetadata book)
    {
        var parts = new List<string> { LibraryLayout.BooksFolderName };
        if (book.Review.Status != ReviewStatus.Approved)
        {
            parts.Add(LibraryLayout.ToReviewFolderName);
        }
        else
        {
            parts.Add(Segment(book.Publisher) ?? LibraryLayout.NoPublisherFolderName);
            if (Segment(book.Series?.Name) is { } series)
            {
                parts.Add(series);
            }
        }

        var title = Segment(book.Title) ?? "Livro";
        parts.Add($"{title} [{Ulid.ShortId(book.Id)}]");
        return string.Join('/', parts);
    }

    /// <summary>"{Título}.{ext}" for a file of the book.</summary>
    public static string FileName(string title, BookFormat format) =>
        $"{Segment(title) ?? "Livro"}.{Extension(format)}";

    public static string Extension(BookFormat format) => format.ToString().ToLowerInvariant();

    /// <summary>A name that is valid on Windows and Linux: ':' becomes ' -', other reserved characters are dropped.</summary>
    public static string? Segment(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case ':':
                    sb.Append(" -");
                    break;
                case '/' or '\\':
                    sb.Append('-');
                    break;
                case '<' or '>' or '"' or '|' or '?' or '*':
                    break;
                default:
                    sb.Append(char.IsControl(c) ? ' ' : c);
                    break;
            }
        }

        var name = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length > MaxSegmentLength)
        {
            name = name[..MaxSegmentLength];
        }

        // Windows ignores trailing dots and spaces, and hidden folders start with a dot.
        name = name.TrimEnd('.', ' ').TrimStart('.', ' ');
        if (name.Length == 0)
        {
            return null;
        }

        return ReservedNames.Contains(name.Split('.')[0]) ? name + "_" : name;
    }

    /// <summary>Returns <paramref name="fileName"/>, or "Name (2).ext", "Name (3).ext"… if taken in <paramref name="folder"/>.</summary>
    public static string Unique(string folder, string fileName)
    {
        var candidate = fileName;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var n = 2; File.Exists(Path.Combine(folder, candidate)) || Directory.Exists(Path.Combine(folder, candidate)); n++)
        {
            candidate = $"{stem} ({n}){extension}";
        }

        return candidate;
    }
}
