namespace GibiOMatic.Library.Index;

/// <summary>
/// Versioned schema of the SQLite index. Migration N (1-based) brings the database from
/// <c>user_version</c> N-1 to N. Never edit a published migration; append a new one.
/// </summary>
internal static class IndexMigrations
{
    public static IReadOnlyList<string> All { get; } =
    [
        // 1: books
        """
        CREATE TABLE books (
            id              TEXT PRIMARY KEY,
            folder          TEXT NOT NULL,
            kind            TEXT NOT NULL,
            title           TEXT NOT NULL,
            series_name     TEXT,
            series_number   TEXT,
            publisher       TEXT,
            year            INTEGER,
            review_status   TEXT NOT NULL,
            metadata_json   TEXT NOT NULL,
            metadata_mtime  INTEGER NOT NULL,
            indexed_at      TEXT NOT NULL
        );
        CREATE INDEX ix_books_series ON books (series_name);
        CREATE INDEX ix_books_publisher ON books (publisher);
        """,
    ];

    public static int LatestVersion => All.Count;
}
