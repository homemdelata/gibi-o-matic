using System.Text.Json;
using Dapper;
using GibiOMatic.Core.Json;
using GibiOMatic.Library.Index;

namespace GibiOMatic.Library.Import;

/// <summary>Persistence of the inbox queue in the library index (survives closing the app).</summary>
internal sealed class InboxStore(LibraryIndex index)
{
    private sealed record Row(long Id, string SourcePath, string State, string? AnalysisJson, string? Decision,
        string? BookId, string? Message, string AddedAt);

    public async Task<IReadOnlyList<InboxItem>> GetAllAsync(CancellationToken ct)
    {
        await using var connection = await index.OpenAsync(ct);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT id AS Id, source_path AS SourcePath, state AS State, analysis_json AS AnalysisJson,
                   decision AS Decision, book_id AS BookId, message AS Message, added_at AS AddedAt
            FROM inbox_items ORDER BY id;
            """, cancellationToken: ct));
        return rows.Select(ToItem).ToList();
    }

    public async Task<InboxItem?> GetAsync(long id, CancellationToken ct) =>
        (await GetAllAsync(ct)).FirstOrDefault(i => i.Id == id);

    /// <summary>Source paths that are still in the inbox and not finished.</summary>
    public async Task<HashSet<string>> GetOpenSourcePathsAsync(CancellationToken ct)
    {
        await using var connection = await index.OpenAsync(ct);
        var paths = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT source_path FROM inbox_items WHERE state NOT IN ('imported', 'skipped', 'failed');", cancellationToken: ct));
        return new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
    }

    public async Task AddAsync(IEnumerable<string> sourcePaths, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await index.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        foreach (var path in sourcePaths)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO inbox_items (source_path, state, added_at, updated_at) VALUES (@path, 'pending', @now, @now);",
                new { path, now }, transaction: tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>The oldest item in <paramref name="state"/>, if any.</summary>
    public async Task<InboxItem?> NextAsync(InboxItemState state, CancellationToken ct)
    {
        await using var connection = await index.OpenAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<Row>(new CommandDefinition(
            """
            SELECT id AS Id, source_path AS SourcePath, state AS State, analysis_json AS AnalysisJson,
                   decision AS Decision, book_id AS BookId, message AS Message, added_at AS AddedAt
            FROM inbox_items WHERE state = @state ORDER BY id LIMIT 1;
            """, new { state = Name(state) }, cancellationToken: ct));
        return row is null ? null : ToItem(row);
    }

    public async Task SetStateAsync(long id, InboxItemState state, CancellationToken ct,
        ImportAnalysis? analysis = null, string? bookId = null, InboxMessage message = InboxMessage.None, string? detail = null)
    {
        await using var connection = await index.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE inbox_items SET state = @state,
                analysis_json = COALESCE(@analysis, analysis_json),
                book_id = COALESCE(@bookId, book_id),
                message = @message, updated_at = @now
            WHERE id = @id;
            """,
            new
            {
                id,
                state = Name(state),
                analysis = analysis is null ? null : JsonSerializer.Serialize(analysis, GibiJson.Options),
                bookId,
                message = message == InboxMessage.None ? null : detail is null ? Name(message) : $"{Name(message)}|{detail}",
                now = DateTimeOffset.UtcNow.ToString("O"),
            },
            cancellationToken: ct));
    }

    public async Task SetDecisionAsync(long id, ImportDecision decision, CancellationToken ct)
    {
        await using var connection = await index.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE inbox_items SET decision = @decision, updated_at = @now WHERE id = @id;",
            new { id, decision = Name(decision), now = DateTimeOffset.UtcNow.ToString("O") }, cancellationToken: ct));
    }

    /// <summary>Items interrupted by closing the app go back to where they were.</summary>
    public async Task ResetInterruptedAsync(CancellationToken ct)
    {
        await using var connection = await index.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE inbox_items SET state = 'pending' WHERE state = 'analyzing';
            UPDATE inbox_items SET state = 'queued' WHERE state = 'importing';
            """, cancellationToken: ct));
    }

    public Task<IReadOnlyList<long>> DeleteFinishedAsync(CancellationToken ct) => DeleteInStatesAsync("'imported', 'skipped', 'failed'", ct);

    public Task<IReadOnlyList<long>> DeleteImportedAsync(CancellationToken ct) => DeleteInStatesAsync("'imported'", ct);

    private async Task<IReadOnlyList<long>> DeleteInStatesAsync(string states, CancellationToken ct)
    {
        await using var connection = await index.OpenAsync(ct);
        var ids = (await connection.QueryAsync<long>(new CommandDefinition(
            $"SELECT id FROM inbox_items WHERE state IN ({states});", cancellationToken: ct))).AsList();
        await connection.ExecuteAsync(new CommandDefinition(
            $"DELETE FROM inbox_items WHERE state IN ({states});", cancellationToken: ct));
        return ids;
    }

    private static InboxItem ToItem(Row row)
    {
        var message = InboxMessage.None;
        string? detail = null;
        if (row.Message is { } text)
        {
            var parts = text.Split('|', 2);
            message = Enum.TryParse<InboxMessage>(parts[0], ignoreCase: true, out var m) ? m : InboxMessage.IoError;
            detail = parts.Length > 1 ? parts[1] : null;
        }

        return new InboxItem(
            row.Id,
            row.SourcePath,
            Enum.Parse<InboxItemState>(row.State, ignoreCase: true),
            row.AnalysisJson is null ? null : JsonSerializer.Deserialize<ImportAnalysis>(row.AnalysisJson, GibiJson.Options),
            row.Decision is null ? null : Enum.Parse<ImportDecision>(row.Decision, ignoreCase: true),
            row.BookId,
            message,
            detail,
            DateTimeOffset.Parse(row.AddedAt, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string Name<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}
