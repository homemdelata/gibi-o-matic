using Dapper;
using GibiOMatic.Library.Index;

namespace GibiOMatic.Library.Reading;

/// <summary>Where a book was left in the reader.</summary>
/// <param name="File">Name of the file that was open (another file starts from the beginning).</param>
/// <param name="Position">Page (0-based) for comics and PDFs, chapter for text EPUBs.</param>
/// <param name="RightToLeft">Pages read from right to left (manga).</param>
public sealed record ReadingState(string File, int Position, bool RightToLeft);

/// <summary>Reading positions, kept in the index; the original files are never touched.</summary>
public sealed class ReadingStateStore(LibraryIndex index)
{
    public async Task<ReadingState?> GetAsync(string bookId, CancellationToken ct = default)
    {
        await using var connection = await index.OpenAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<Row>(new CommandDefinition(
            "SELECT file AS File, position AS Position, right_to_left AS RightToLeft FROM reading_state WHERE book_id = @bookId;",
            new { bookId }, cancellationToken: ct));
        return row is null ? null : new ReadingState(row.File, (int)row.Position, row.RightToLeft != 0);
    }

    public async Task SaveAsync(string bookId, ReadingState state, CancellationToken ct = default)
    {
        await using var connection = await index.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO reading_state (book_id, file, position, right_to_left, updated_at)
            VALUES (@bookId, @file, @position, @rtl, @now)
            ON CONFLICT (book_id) DO UPDATE SET file = excluded.file, position = excluded.position,
                right_to_left = excluded.right_to_left, updated_at = excluded.updated_at;
            """,
            new { bookId, file = state.File, position = state.Position, rtl = state.RightToLeft ? 1 : 0, now = DateTimeOffset.UtcNow.ToString("O") },
            cancellationToken: ct));
    }

    private sealed class Row
    {
        public string File { get; init; } = string.Empty;
        public long Position { get; init; }
        public long RightToLeft { get; init; }
    }
}
