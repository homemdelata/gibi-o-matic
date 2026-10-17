namespace GibiOMatic.Core.Libraries;

/// <summary>Creates and opens libraries (the "Arquivo") and maintains their local index.</summary>
public interface ILibraryService
{
    /// <summary>Turns <paramref name="rootPath"/> into a new, empty library and opens it.</summary>
    /// <exception cref="LibraryException">The folder already holds a library.</exception>
    Task<LibraryInfo> CreateAsync(string rootPath, CancellationToken ct = default);

    /// <summary>Opens an existing library, creating or migrating its index if needed.</summary>
    /// <exception cref="LibraryException">The folder is not a valid library.</exception>
    Task<LibraryInfo> OpenAsync(string rootPath, CancellationToken ct = default);

    /// <summary>Discards the index and rebuilds it by scanning every <c>metadata.json</c>.</summary>
    Task<IndexRebuildResult> RebuildIndexAsync(LibraryInfo library, CancellationToken ct = default);

    Task<int> CountBooksAsync(LibraryInfo library, CancellationToken ct = default);
}

public sealed record LibraryInfo(string Id, int FormatVersion, string RootPath);

public sealed record IndexRebuildResult(int IndexedBooks, IReadOnlyList<IndexIssue> Issues);

/// <summary>A <c>metadata.json</c> that could not be indexed.</summary>
/// <param name="RelativePath">Path relative to the library root.</param>
public sealed record IndexIssue(string RelativePath, string Message);

public enum LibraryErrorKind
{
    AlreadyALibrary,
    NotALibrary,
    InvalidManifest,
    UnsupportedFormatVersion,
}

public sealed class LibraryException(LibraryErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public LibraryErrorKind Kind { get; } = kind;
}
