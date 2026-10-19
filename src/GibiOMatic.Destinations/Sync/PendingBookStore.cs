using Dapper;
using GibiOMatic.Library.Index;

namespace GibiOMatic.Destinations.Sync;

/// <summary>
/// Books changed since their copy in each destination was last updated. Kept in the index, so a
/// change is not lost when the app closes (or crashes) before its turn; one list per destination,
/// so a destination that is away (an unplugged drive) does not hold the others.
/// </summary>
internal sealed class PendingBookStore(LibraryIndex index)
{
    public async Task AddAsync(string destinationId, string bookId, CancellationToken ct)
    {
        await using var connection = await index.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO destination_pending_books (destination_id, book_id, since) VALUES (@destinationId, @bookId, @since)
            ON CONFLICT (destination_id, book_id) DO UPDATE SET since = excluded.since;
            """,
            new { destinationId, bookId, since = DateTimeOffset.UtcNow.ToString("O") }, cancellationToken: ct));
    }

    /// <summary>Done, unless the book changed again after <paramref name="handledAt"/>.</summary>
    public async Task RemoveAsync(string destinationId, string bookId, DateTimeOffset handledAt, CancellationToken ct)
    {
        await using var connection = await index.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM destination_pending_books WHERE destination_id = @destinationId AND book_id = @bookId AND since <= @handledAt;",
            new { destinationId, bookId, handledAt = handledAt.ToUniversalTime().ToString("O") }, cancellationToken: ct));
    }

    /// <summary>Oldest first.</summary>
    public async Task<IReadOnlyList<string>> GetAsync(string destinationId, CancellationToken ct)
    {
        await using var connection = await index.OpenAsync(ct);
        return (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT book_id FROM destination_pending_books WHERE destination_id = @destinationId ORDER BY since;",
            new { destinationId }, cancellationToken: ct))).ToList();
    }

    /// <summary>Books left pending by a version with a single queue for every destination (index v6–v8).</summary>
    public async Task<IReadOnlyList<string>> TakeSharedAsync(CancellationToken ct)
    {
        await using var connection = await index.OpenAsync(ct);
        var books = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT book_id FROM destination_pending ORDER BY since;", cancellationToken: ct))).ToList();
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM destination_pending;", cancellationToken: ct));
        return books;
    }
}
