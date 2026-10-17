using Dapper;
using GibiOMatic.Core.Domain;
using Microsoft.Data.Sqlite;

namespace GibiOMatic.Library.Index;

/// <summary>
/// Local SQLite cache of a library's metadata. It lives outside the library folder and can be
/// deleted and rebuilt at any time from the <c>metadata.json</c> files.
/// </summary>
public sealed class LibraryIndex(string databasePath)
{
    // Pooling is off so the file is released as soon as each operation ends
    // (the index may be deleted and recreated while the app runs).
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Pooling = false,
    }.ToString();

    public string DatabasePath { get; } = databasePath;

    /// <summary>
    /// Creates or migrates the schema. Returns <c>true</c> when the index started empty
    /// (new, or discarded because a newer app version wrote it) and must be rebuilt.
    /// </summary>
    public async Task<bool> EnsureSchemaAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(DatabasePath))!);

        var version = await GetVersionAsync(ct);
        if (version > IndexMigrations.LatestVersion)
        {
            // Written by a newer version of the app. It is only a cache: start over.
            File.Delete(DatabasePath);
            version = 0;
        }

        await using var connection = await OpenAsync(ct);
        for (var next = version + 1; next <= IndexMigrations.LatestVersion; next++)
        {
            await using var tx = await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition(IndexMigrations.All[next - 1], transaction: tx, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition($"PRAGMA user_version = {next};", transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }

        return version == 0;
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

    /// <summary>Replaces the whole content of the index in a single transaction.</summary>
    public async Task ReplaceAllAsync(IEnumerable<IndexedBook> books, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM books;", transaction: tx, cancellationToken: ct));

        var indexedAt = DateTimeOffset.UtcNow.ToString("O");
        foreach (var book in books)
        {
            ct.ThrowIfCancellationRequested();
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
        }

        await tx.CommitAsync(ct);
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

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}

/// <param name="Folder">Book folder relative to the library root, with '/' separators.</param>
public sealed record IndexedBook(BookMetadata Metadata, string Folder, DateTime MetadataModifiedUtc);
