using Dapper;
using GibiOMatic.Core.Domain;
using Microsoft.Data.Sqlite;

namespace GibiOMatic.Library.Index;

/// <summary>
/// Local SQLite cache of a library's metadata. It lives outside the library folder and can be
/// deleted and rebuilt at any time from the <c>metadata.json</c> files. It also keeps the inbox
/// queue, which is per machine.
/// </summary>
public sealed class LibraryIndex(string databasePath)
{
    /// <summary>Indexes older than this lack data that only a rebuild can fill (the files table).</summary>
    private const int RebuildBelowVersion = 2;

    // Pooling is off so the file is released as soon as each operation ends
    // (the index may be deleted and recreated while the app runs).
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Pooling = false,
        DefaultTimeout = 30,
    }.ToString();

    public string DatabasePath { get; } = databasePath;

    /// <summary>
    /// Creates or migrates the schema. Returns <c>true</c> when the index must be rebuilt:
    /// it is new, was discarded because a newer app version wrote it, or came from a version
    /// whose data is incomplete.
    /// </summary>
    public async Task<bool> EnsureSchemaAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(DatabasePath))!);

        var version = await GetVersionAsync(ct);
        if (version > IndexMigrations.LatestVersion)
        {
            // Written by a newer version of the app: used as it is when that version says this one can
            // still read it (it only added tables); otherwise it is only a cache, so start over.
            if (await GetOldestReaderAsync(ct) is { } oldest && oldest <= IndexMigrations.LatestVersion)
            {
                return false;
            }

            DeleteDatabaseFiles();
            version = 0;
        }

        // A copy of the index as it was before migrating, so going back to the older app can restore it by hand.
        if (version > 0 && version < IndexMigrations.LatestVersion)
        {
            BackUp(version);
        }

        await using var connection = await OpenAsync(ct);
        // WAL lets the background import write while the UI reads.
        await connection.ExecuteAsync(new CommandDefinition("PRAGMA journal_mode = WAL;", cancellationToken: ct));
        for (var next = version + 1; next <= IndexMigrations.LatestVersion; next++)
        {
            await using var tx = await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition(IndexMigrations.All[next - 1], transaction: tx, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition($"PRAGMA user_version = {next};", transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO app_state (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = @value;",
            new { key = IndexMigrations.OldestReaderKey, value = IndexMigrations.OldestReader.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            cancellationToken: ct));
        return version < RebuildBelowVersion;
    }

    public async Task<int> GetVersionAsync(CancellationToken ct = default)
    {
        if (!File.Exists(DatabasePath))
        {
            return 0;
        }

        await using var connection = await OpenAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition("PRAGMA user_version;", cancellationToken: ct));
    }

    /// <summary>Replaces every book in the index in a single transaction (the inbox is kept).</summary>
    public async Task ReplaceAllAsync(IEnumerable<IndexedBook> books, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM books; DELETE FROM book_files;", transaction: tx, cancellationToken: ct));

        var indexedAt = DateTimeOffset.UtcNow.ToString("O");
        foreach (var book in books)
        {
            ct.ThrowIfCancellationRequested();
            await InsertAsync(connection, tx, book, indexedAt, ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>Adds or replaces one book.</summary>
    public async Task UpsertBookAsync(IndexedBook book, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM books WHERE id = @Id; DELETE FROM book_files WHERE book_id = @Id;",
            new { book.Metadata.Id }, transaction: tx, cancellationToken: ct));
        await InsertAsync(connection, tx, book, DateTimeOffset.UtcNow.ToString("O"), ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Forgets a book removed from the library: its row, files, identification and reading state.</summary>
    public async Task DeleteBookAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM books WHERE id = @id;
            DELETE FROM book_files WHERE book_id = @id;
            DELETE FROM identifications WHERE book_id = @id;
            DELETE FROM reading_state WHERE book_id = @id;
            DELETE FROM review_group_books WHERE book_id = @id;
            """,
            new { id }, cancellationToken: ct));
    }

    private static async Task InsertAsync(SqliteConnection connection, System.Data.Common.DbTransaction tx, IndexedBook book, string indexedAt, CancellationToken ct)
    {
        var m = book.Metadata;
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO books (id, folder, kind, title, series_name, series_number, publisher, year,
                               review_status, metadata_json, metadata_mtime, indexed_at)
            VALUES (@Id, @Folder, @Kind, @Title, @SeriesName, @SeriesNumber, @Publisher, @Year,
                    @ReviewStatus, @MetadataJson, @MetadataMtime, @IndexedAt);
            """,
            new
            {
                m.Id,
                book.Folder,
                Kind = m.Kind.ToString().ToLowerInvariant(),
                m.Title,
                SeriesName = m.Series?.Name,
                SeriesNumber = m.Series?.Number,
                m.Publisher,
                m.Date?.Year,
                ReviewStatus = m.Review.Status.ToString().ToLowerInvariant(),
                MetadataJson = MetadataFile.Serialize(m),
                MetadataMtime = book.MetadataModifiedUtc.Ticks,
                IndexedAt = indexedAt,
            },
            transaction: tx,
            cancellationToken: ct));

        foreach (var file in m.Files)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT OR REPLACE INTO book_files (book_id, name, role, format, sha256, bytes) VALUES (@BookId, @Name, @Role, @Format, @Sha256, @Bytes);",
                new
                {
                    BookId = m.Id,
                    file.Name,
                    Role = file.Role.ToString().ToLowerInvariant(),
                    Format = file.Format.ToString().ToLowerInvariant(),
                    Sha256 = file.Sha256.ToLowerInvariant(),
                    file.Bytes,
                },
                transaction: tx,
                cancellationToken: ct));
        }
    }

    public async Task<int> CountBooksAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM books;", cancellationToken: ct));
    }

    public async Task<IReadOnlyList<string>> GetBookIdsAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var ids = await connection.QueryAsync<string>(new CommandDefinition("SELECT id FROM books ORDER BY id;", cancellationToken: ct));
        return ids.AsList();
    }

    /// <summary>Every book with its folder, read from the cached <c>metadata.json</c> contents.</summary>
    public async Task<IReadOnlyList<CatalogBook>> GetBooksAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var rows = await connection.QueryAsync<(string Folder, string MetadataJson)>(
            new CommandDefinition("SELECT folder, metadata_json FROM books;", cancellationToken: ct));
        return rows.Select(r => new CatalogBook(MetadataFile.Deserialize(r.MetadataJson), r.Folder)).ToList();
    }

    public async Task<CatalogBook?> GetBookAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<(string Folder, string MetadataJson)?>(
            new CommandDefinition("SELECT folder, metadata_json FROM books WHERE id = @id;", new { id }, cancellationToken: ct));
        return row is { } r ? new CatalogBook(MetadataFile.Deserialize(r.MetadataJson), r.Folder) : null;
    }

    /// <summary>The book that already has a file with this SHA-256, if any.</summary>
    public async Task<string?> FindBookBySha256Async(string sha256, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        return await connection.QueryFirstOrDefaultAsync<string>(new CommandDefinition(
            "SELECT book_id FROM book_files WHERE sha256 = @sha LIMIT 1;", new { sha = sha256.ToLowerInvariant() }, cancellationToken: ct));
    }

    public async Task<string?> GetStateAsync(string key, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        return await connection.QueryFirstOrDefaultAsync<string>(new CommandDefinition(
            "SELECT value FROM app_state WHERE key = @key;", new { key }, cancellationToken: ct));
    }

    public async Task SetStateAsync(string key, string value, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO app_state (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
            new { key, value }, cancellationToken: ct));
    }

    internal async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    /// <summary>The oldest app schema a newer index says can read it; null when it doesn't say (written before 12).</summary>
    private async Task<int?> GetOldestReaderAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = await OpenAsync(ct);
            var value = await connection.QueryFirstOrDefaultAsync<string>(new CommandDefinition(
                "SELECT value FROM app_state WHERE key = @key;", new { key = IndexMigrations.OldestReaderKey }, cancellationToken: ct));
            return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var oldest) ? oldest : null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <summary>Copies the database (and its write-ahead log) to <c>&lt;name&gt;.v&lt;version&gt;.bak</c>, replacing an older copy.</summary>
    private void BackUp(int version)
    {
        try
        {
            foreach (var suffix in new[] { "", "-wal" })
            {
                var source = DatabasePath + suffix;
                if (File.Exists(source))
                {
                    File.Copy(source, $"{DatabasePath}.v{version}.bak{suffix}", overwrite: true);
                }
            }
        }
        catch (IOException)
        {
            // Only a convenience: migrating goes on without it.
        }
    }

    private void DeleteDatabaseFiles()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(DatabasePath + suffix);
        }
    }
}

/// <param name="Folder">Book folder relative to the library root, with '/' separators.</param>
public sealed record IndexedBook(BookMetadata Metadata, string Folder, DateTime MetadataModifiedUtc);

/// <summary>A book as listed by the index.</summary>
/// <param name="Folder">Book folder relative to the library root, with '/' separators.</param>
public sealed record CatalogBook(BookMetadata Metadata, string Folder)
{
    public BookFile? MainFile => Metadata.Files.FirstOrDefault(f => f.Role == FileRole.Original) ?? Metadata.Files.FirstOrDefault();
}
