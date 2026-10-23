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

        // 2: files of each book (exact-duplicate lookup by hash), the inbox queue and app state
        """
        CREATE TABLE book_files (
            book_id   TEXT NOT NULL,
            name      TEXT NOT NULL,
            role      TEXT NOT NULL,
            format    TEXT NOT NULL,
            sha256    TEXT NOT NULL,
            bytes     INTEGER NOT NULL,
            PRIMARY KEY (book_id, name)
        );
        CREATE INDEX ix_book_files_sha256 ON book_files (sha256);

        CREATE TABLE inbox_items (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            source_path     TEXT NOT NULL,
            state           TEXT NOT NULL,
            analysis_json   TEXT,
            decision        TEXT,
            book_id         TEXT,
            message         TEXT,
            added_at        TEXT NOT NULL,
            updated_at      TEXT NOT NULL
        );
        CREATE INDEX ix_inbox_state ON inbox_items (state);

        CREATE TABLE app_state (
            key    TEXT PRIMARY KEY,
            value  TEXT NOT NULL
        );
        """,

        // 3: online identification of each book (candidates for the Review)
        """
        CREATE TABLE identifications (
            book_id      TEXT PRIMARY KEY,
            state        TEXT NOT NULL,
            result_json  TEXT,
            confidence   REAL,
            message      TEXT,
            skipped      INTEGER NOT NULL DEFAULT 0,
            updated_at   TEXT NOT NULL
        );
        CREATE INDEX ix_identifications_state ON identifications (state);
        """,

        // 4: copies generated for each destination (to know what is up to date and what to remove)
        """
        CREATE TABLE destination_outputs (
            destination_id  TEXT NOT NULL,
            book_id         TEXT NOT NULL,
            path            TEXT NOT NULL,
            content_hash    TEXT NOT NULL,
            generated_at    TEXT NOT NULL,
            PRIMARY KEY (destination_id, book_id)
        );
        """,

        // 5: what each destination did (copies written and removed, errors), shown in its history
        """
        CREATE TABLE destination_log (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            destination_id  TEXT NOT NULL,
            at              TEXT NOT NULL,
            kind            TEXT NOT NULL,
            title           TEXT,
            path            TEXT,
            message         TEXT
        );
        CREATE INDEX ix_destination_log ON destination_log (destination_id, id);
        """,

        // 6: books whose copies still have to be updated (survives closing the app)
        """
        CREATE TABLE destination_pending (
            book_id  TEXT PRIMARY KEY,
            since    TEXT NOT NULL
        );
        """,

        // 7: where each book was left in the reader (never written to the original)
        """
        CREATE TABLE reading_state (
            book_id        TEXT PRIMARY KEY,
            file           TEXT NOT NULL,
            position       INTEGER NOT NULL,
            right_to_left  INTEGER NOT NULL DEFAULT 0,
            updated_at     TEXT NOT NULL
        );
        """,

        // 8: reading status: progress shown in the library, "started" and "read" marks
        """
        ALTER TABLE reading_state ADD COLUMN total INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE reading_state ADD COLUMN started INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE reading_state ADD COLUMN read_at TEXT;
        """,

        // 9: pending books per destination (each destination has its own queue; v6 rows are moved on start)
        """
        CREATE TABLE destination_pending_books (
            destination_id  TEXT NOT NULL,
            book_id         TEXT NOT NULL,
            since           TEXT NOT NULL,
            PRIMARY KEY (destination_id, book_id)
        );
        """,

        // 10: files picked up from the watched inbox folder are confirmed and moved without the user (F5)
        """
        ALTER TABLE inbox_items ADD COLUMN auto INTEGER NOT NULL DEFAULT 0;
        """,
    ];

    public static int LatestVersion => All.Count;
}
