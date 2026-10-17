using System.Globalization;
using Dapper;
using GibiOMatic.Library.Index;

namespace GibiOMatic.Destinations.Sync;

public enum DestinationLogKind
{
    Created,
    Updated,
    Deleted,

    /// <summary>A copy could not be written or removed; the message has the error.</summary>
    Failed,

    /// <summary>The server was asked to scan (Komga).</summary>
    ScanRequested,
    ScanFailed,

    /// <summary>End of a run; the message has "written/deleted/failed" counts.</summary>
    Finished,
}

public sealed record DestinationLogEntry(long Id, DateTimeOffset At, DestinationLogKind Kind, string? Title, string? Path, string? Message);

/// <summary>
/// History of each destination, in the library index: what was written, removed and what failed.
/// Technical details (stack traces) go to the app log file; this is what the user reads.
/// </summary>
internal sealed class DestinationLogStore(LibraryIndex index)
{
    /// <summary>Entries kept per destination; older ones are dropped.</summary>
    private const int Keep = 2000;

    public async Task AddAsync(string destinationId, DestinationLogKind kind, string? title, string? path, string? message, CancellationToken ct)
    {
        await using var connection = await index.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO destination_log (destination_id, at, kind, title, path, message)
            VALUES (@destinationId, @at, @kind, @title, @path, @message);
            """,
            new { destinationId, at = DateTimeOffset.UtcNow.ToString("O"), kind = kind.ToString(), title, path, message },
            cancellationToken: ct));
    }

    public async Task TrimAsync(string destinationId, CancellationToken ct)
    {
        await using var connection = await index.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM destination_log WHERE destination_id = @destinationId AND id <=
                (SELECT id FROM destination_log WHERE destination_id = @destinationId ORDER BY id DESC LIMIT 1 OFFSET @keep);
            """,
            new { destinationId, keep = Keep }, cancellationToken: ct));
    }

    /// <summary>Newest first.</summary>
    public async Task<IReadOnlyList<DestinationLogEntry>> GetAsync(string destinationId, int limit, CancellationToken ct)
    {
        await using var connection = await index.OpenAsync(ct);
        var rows = await connection.QueryAsync<(long Id, string At, string Kind, string? Title, string? Path, string? Message)>(new CommandDefinition(
            "SELECT id, at, kind, title, path, message FROM destination_log WHERE destination_id = @destinationId ORDER BY id DESC LIMIT @limit;",
            new { destinationId, limit }, cancellationToken: ct));
        return rows.Select(r => new DestinationLogEntry(r.Id, DateTimeOffset.Parse(r.At, CultureInfo.InvariantCulture),
            Enum.TryParse<DestinationLogKind>(r.Kind, out var kind) ? kind : DestinationLogKind.Failed, r.Title, r.Path, r.Message)).ToList();
    }

    public async Task DeleteAsync(string destinationId, CancellationToken ct)
    {
        await using var connection = await index.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM destination_log WHERE destination_id = @destinationId;", new { destinationId }, cancellationToken: ct));
    }
}
