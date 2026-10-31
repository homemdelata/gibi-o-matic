namespace GibiOMatic.Library.Editing;

/// <summary>
/// Saves made together, such as a group approved in the Review by series (SPEC §7.1): each book has
/// its own history entry, and the batch lists them so they can be undone at once. Kept in
/// <c>.gibiomatic/batches/&lt;id&gt;.json</c>.
/// </summary>
public sealed record HistoryBatch
{
    public required string Id { get; init; }

    /// <summary>What was done, for the history ("Deadly Class").</summary>
    public required string Label { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the batch was undone; an undone batch is not offered again.</summary>
    public DateTimeOffset? UndoneAt { get; init; }

    public IReadOnlyList<HistoryBatchEntry> Entries { get; init; } = [];
}

/// <param name="HistoryFile">The history file holding the book's version from before the batch.</param>
public sealed record HistoryBatchEntry(string BookId, string HistoryFile);
