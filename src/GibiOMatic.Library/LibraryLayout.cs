namespace GibiOMatic.Library;

/// <summary>Names of the files and folders that make up a library on disk (SPEC §4.2).</summary>
public static class LibraryLayout
{
    public const string ManifestFileName = "gibiomatic-library.json";
    public const string BooksFolderName = "Books";
    public const string InternalFolderName = ".gibiomatic";
    public const string HistoryFolderName = "history";
    public const string TrashFolderName = "trash";
    public const string MetadataFileName = "metadata.json";
    public const string CoverFileName = "cover.jpg";

    /// <summary>Folder of approved books without a publisher.</summary>
    public const string NoPublisherFolderName = "_No publisher";

    /// <summary>Folder of books not approved yet: they move to their place once the metadata is reviewed.</summary>
    public const string ToReviewFolderName = "_To review";

    /// <summary>Versions of a book's <c>metadata.json</c> kept in the history.</summary>
    public const int HistoryLimit = 20;

    public static string ManifestPath(string root) => Path.Combine(root, ManifestFileName);
    public static string BooksPath(string root) => Path.Combine(root, BooksFolderName);
    public static string InternalPath(string root) => Path.Combine(root, InternalFolderName);
    public static string HistoryPath(string root) => Path.Combine(root, InternalFolderName, HistoryFolderName);
    public static string TrashPath(string root) => Path.Combine(root, InternalFolderName, TrashFolderName);
}
