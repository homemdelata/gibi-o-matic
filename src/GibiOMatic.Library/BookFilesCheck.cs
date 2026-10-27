using GibiOMatic.Library.Index;

namespace GibiOMatic.Library;

/// <summary>
/// Finds books whose files were deleted or moved outside the app: the main original is missing,
/// or the whole book folder is gone.
/// </summary>
public static class BookFilesCheck
{
    /// <param name="MissingOriginal">Books whose folder is there but whose main original is not.</param>
    /// <param name="MissingFolder">Books whose folder is gone.</param>
    public sealed record Result(IReadOnlySet<string> MissingOriginal, IReadOnlySet<string> MissingFolder);

    /// <summary>
    /// <c>null</c> when the library itself cannot be reached: an unmounted drive does not mean that
    /// every book is gone.
    /// </summary>
    public static Result? Check(string root, IEnumerable<CatalogBook> books, CancellationToken ct = default)
    {
        if (!Directory.Exists(LibraryLayout.BooksPath(root)))
        {
            return null;
        }

        var missingOriginal = new HashSet<string>(StringComparer.Ordinal);
        var missingFolder = new HashSet<string>(StringComparer.Ordinal);
        foreach (var book in books)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(Path.Combine(root, book.Folder)))
            {
                missingFolder.Add(book.Metadata.Id);
            }
            else if (!HasOriginal(root, book))
            {
                missingOriginal.Add(book.Metadata.Id);
            }
        }

        return new Result(missingOriginal, missingFolder);
    }

    public static bool HasOriginal(string root, CatalogBook book) =>
        book.MainFile is { } main && File.Exists(Path.Combine(root, book.Folder, main.Name));
}
