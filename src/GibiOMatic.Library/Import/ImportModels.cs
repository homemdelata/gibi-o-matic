using GibiOMatic.Conversion;
using GibiOMatic.Core.Domain;

namespace GibiOMatic.Library.Import;

/// <summary>
/// Life of a file in the inbox: it is analysed on its own (read-only), then waits for the user
/// to confirm, then is imported in the background (SPEC §6, §10).
/// </summary>
public enum InboxItemState
{
    /// <summary>Waiting to be analysed.</summary>
    Pending,
    Analyzing,

    /// <summary>Analysed; waiting for the user to confirm the import.</summary>
    Ready,

    /// <summary>Confirmed; waiting for its turn.</summary>
    Queued,
    Importing,
    Imported,
    Skipped,
    Failed,
}

public enum ImportDecision
{
    /// <summary>New book.</summary>
    Import,

    /// <summary>The new file becomes the main original; the current one goes to the internal trash.</summary>
    Replace,

    /// <summary>Both files are kept: the preferred format is the main original, the other an extra format.</summary>
    KeepBoth,
    Skip,
}

/// <summary>Why an item was skipped or failed; the UI turns it into text.</summary>
public enum InboxMessage
{
    None,
    NotFound,
    UnsupportedFormat,
    SourceChanged,
    AlreadyInLibrary,
    DuplicateInList,
    SkippedByUser,
    IoError,
}

public enum DuplicateKind
{
    /// <summary>The library already has this exact file (same SHA-256).</summary>
    SameFileInLibrary,

    /// <summary>An earlier item of the inbox is this exact file.</summary>
    SameFileInList,

    /// <summary>The library has the same book in another file.</summary>
    SameBookInLibrary,

    /// <summary>Another item of the inbox is the same book.</summary>
    SameBookInList,
}

/// <summary>The book of the library that a file matches, found during analysis.</summary>
public sealed record LibraryMatch(bool SameFile, string BookId, string BookTitle, BookFormat? MainFormat, FileQuality MainQuality);

/// <summary>Result of analysing a file: everything needed to show it and to import it later.</summary>
public sealed record ImportAnalysis
{
    public required string FileName { get; init; }
    public required BookFormat Format { get; init; }
    public long Bytes { get; init; }
    public required string Sha256 { get; init; }

    /// <summary>Title the book will get: embedded title, or series and number, or the cleaned file name.</summary>
    public required string Title { get; init; }

    /// <summary>Key that recognises the same book in another format (F1: ISBN, or title and number).</summary>
    public required string BookKey { get; init; }

    public EmbeddedMetadata Metadata { get; init; } = EmbeddedMetadata.Empty;
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public LibraryMatch? Match { get; init; }
    public bool HasThumbnail { get; init; }
}

/// <summary>A row of the inbox as stored.</summary>
/// <param name="Auto">
/// Picked up from the watched inbox folder (F5): confirmed with the suggested decision as soon as it is
/// analysed, and moved to <c>_imported</c> once it is in the library.
/// </param>
public sealed record InboxItem(
    long Id,
    string SourcePath,
    InboxItemState State,
    ImportAnalysis? Analysis,
    ImportDecision? Decision,
    string? BookId,
    InboxMessage Message,
    string? MessageDetail,
    DateTimeOffset AddedAt,
    bool Auto = false);

/// <summary>An inbox row with what the queue concluded about it.</summary>
/// <param name="Decision">The user's choice if still allowed, otherwise the suggestion.</param>
public sealed record InboxEntry(
    InboxItem Item,
    DuplicateKind? Duplicate,
    string? DuplicateTitle,
    ImportDecision Decision,
    IReadOnlyList<ImportDecision> AllowedDecisions);

public sealed record ImportSummary(int Waiting, int Ready, int Queued, int Imported, int Skipped, int Failed)
{
    public int Total => Waiting + Ready + Queued + Imported + Skipped + Failed;
    public bool HasWork => Waiting + Queued > 0;
}

/// <summary>An item reached <see cref="InboxItemState.Imported"/>, <see cref="InboxItemState.Skipped"/> or <see cref="InboxItemState.Failed"/>.</summary>
public sealed record InboxItemFinished(long ItemId, bool Auto, InboxItemState State, string? BookId, InboxMessage Message);

/// <summary>What the background worker is doing right now.</summary>
public sealed record ImportProgress(long ItemId, InboxItemState Phase, string FileName, double Fraction);
