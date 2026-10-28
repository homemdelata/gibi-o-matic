using System.Globalization;
using Dapper;
using GibiOMatic.Library.Index;

namespace GibiOMatic.Library.Reading;

/// <summary>Where a book was left in the reader.</summary>
/// <param name="File">Name of the file that was open (another file starts from the beginning).</param>
/// <param name="Position">Page (0-based) for comics and PDFs, chapter for text EPUBs.</param>
/// <param name="RightToLeft">Pages read from right to left (manga).</param>
public sealed record ReadingState(string File, int Position, bool RightToLeft);

/// <summary>What one session in the reader adds to a book's status.</summary>
/// <param name="Total">Pages (or chapters of a text EPUB) of the open file.</param>
/// <param name="Started">The book was really read, not just opened to look at it.</param>
/// <param name="ReachedEnd">The last page (or chapter) was shown.</param>
public sealed record ReadingProgress(int Total, bool Started, bool ReachedEnd);

/// <summary>A book's reading status, as the library shows it.</summary>
public sealed record BookReading(string BookId, string File, int Position, int Total, bool Started, DateTimeOffset? ReadAt,
    DateTimeOffset UpdatedAt)
{
    public bool IsRead => ReadAt is not null;

    /// <summary>Started and not finished: shown in "Continue reading".</summary>
    public bool IsReading => Started && !IsRead;

    /// <summary>From 0 to 1; 0 when the size of the book is not known.</summary>
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((Position + 1) / (double)Total, 0, 1);
}

/// <summary>
/// Reading positions and statuses, kept in the index; the original files are never touched.
/// A book is "started" once more than <see cref="StartedAfterPages"/> pages were shown (opening it to
/// look at the cover or the credits does not count) and "read" when its last page is reached after
/// that, or when marked by hand.
/// </summary>
public sealed class ReadingStateStore(LibraryIndex index)
{
    /// <summary>Pages shown before a book counts as started.</summary>
    public const int StartedAfterPages = 5;

    /// <summary>Chapters of a text EPUB shown before it counts as started (they are much longer than pages).</summary>
    public const int StartedAfterChapters = 1;

    private const string Columns = """
        book_id AS BookId, file AS File, position AS Position, right_to_left AS RightToLeft, total AS Total,
        started AS Started, read_at AS ReadAt, updated_at AS UpdatedAt
        """;

    public async Task<ReadingState?> GetAsync(string bookId, CancellationToken ct = default)
    {
        await using var connection = await index.OpenAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM reading_state WHERE book_id = @bookId;", new { bookId }, cancellationToken: ct));
        return row is null ? null : new ReadingState(row.File, (int)row.Position, row.RightToLeft != 0);
    }

    /// <summary>Every book that was opened in the reader or marked by hand.</summary>
    public async Task<IReadOnlyDictionary<string, BookReading>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = await index.OpenAsync(ct);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition($"SELECT {Columns} FROM reading_state;", cancellationToken: ct));
        return rows.ToDictionary(r => r.BookId, r => r.ToReading());
    }

    /// <summary>Saves where the book was left; marks it started or read as <paramref name="progress"/> says.</summary>
    public async Task SaveAsync(string bookId, ReadingState state, ReadingProgress? progress = null, CancellationToken ct = default)
    {
        await using var connection = await index.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO reading_state (book_id, file, position, right_to_left, total, started, read_at, updated_at)
            VALUES (@bookId, @file, @position, @rtl, @total, @started, CASE WHEN @started AND @end THEN @now END, @now)
            ON CONFLICT (book_id) DO UPDATE SET file = excluded.file, position = excluded.position,
                right_to_left = excluded.right_to_left, total = excluded.total,
                read_at = CASE WHEN reading_state.read_at IS NULL AND @end AND (reading_state.started OR @started) THEN @now
                               ELSE reading_state.read_at END,
                started = reading_state.started OR @started,
                updated_at = excluded.updated_at;
            """,
            new
            {
                bookId,
                file = state.File,
                position = state.Position,
                rtl = state.RightToLeft ? 1 : 0,
                total = progress?.Total ?? 0,
                started = progress?.Started == true,
                end = progress?.ReachedEnd == true,
                now = Now(),
            },
            cancellationToken: ct));
    }

    /// <summary>
    /// Marks a book read or not read by hand. "Not read" also forgets the progress, so the book does
    /// not come back as being read.
    /// </summary>
    public async Task SetReadAsync(string bookId, bool read, CancellationToken ct = default)
    {
        await using var connection = await index.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            read
                ? """
                  INSERT INTO reading_state (book_id, file, position, right_to_left, read_at, updated_at)
                  VALUES (@bookId, '', 0, 0, @now, @now)
                  ON CONFLICT (book_id) DO UPDATE SET read_at = @now, updated_at = @now;
                  """
                : "UPDATE reading_state SET read_at = NULL, started = 0, position = 0, updated_at = @now WHERE book_id = @bookId;",
            new { bookId, now = Now() }, cancellationToken: ct));
    }

    /// <summary>Forgets that a book was started: it leaves "Continue reading" and opens at the first page.</summary>
    public async Task ClearProgressAsync(string bookId, CancellationToken ct = default)
    {
        await using var connection = await index.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE reading_state SET started = 0, position = 0, updated_at = @now WHERE book_id = @bookId;",
            new { bookId, now = Now() }, cancellationToken: ct));
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private sealed class Row
    {
        public string BookId { get; init; } = string.Empty;
        public string File { get; init; } = string.Empty;
        public long Position { get; init; }
        public long RightToLeft { get; init; }
        public long Total { get; init; }
        public long Started { get; init; }
        public string? ReadAt { get; init; }
        public string UpdatedAt { get; init; } = string.Empty;

        public BookReading ToReading() => new(BookId, File, (int)Position, (int)Total, Started != 0,
            ReadAt is null ? null : DateTimeOffset.Parse(ReadAt, CultureInfo.InvariantCulture),
            DateTimeOffset.TryParse(UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : DateTimeOffset.MinValue);
    }
}
