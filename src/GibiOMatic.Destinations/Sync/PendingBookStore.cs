using Dapper;
using GibiOMatic.Library.Index;

namespace GibiOMatic.Destinations.Sync;

/// <summary>
/// Books changed since their copies were last updated. Kept in the index, so a change is not lost
/// when the app closes (or crashes) before its turn in the queue.
/// </summary>
internal sealed class PendingBookStore(LibraryIndex index)
{
    public async Task AddAsync(string bookId, CancellationToken ct)
    {
        await using var connection = await index.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO destination_pending (book_id, since) VALUES (@bookId, @since)
            ON CONFLICT (book_id) DO UPDATE SET since = excluded.since;
            """,
            new { bookId, since = DateTimeOffset.UtcNow.ToString("O") }, cancellationToken: ct));
    }

    /// <summary>Done, unless the book changed again after <paramref name="handledAt"/>.</summary>
    public async Task RemoveAsync(string bookId, DateTimeOffset handledAt, CancellationToken ct)
    {
        await using var connection = await index.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM destination_pending WHERE book_id = @bookId AND since <= @handledAt;",
            new { bookId, handledAt = handledAt.ToUniversalTime().ToString("O") }, cancellationToken: ct));
    }

    /// <summary>Oldest first.</summary>
    public async Task<IReadOnlyList<string>> GetAsync(CancellationToken ct)
    {
        await using var connection = await index.OpenAsync(ct);
        return (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT book_id FROM destination_pending ORDER BY since;", cancellationToken: ct))).ToList();
    }
}
