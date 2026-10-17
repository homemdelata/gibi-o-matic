using Dapper;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Library.Index;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Destinations.Sync;

/// <summary>A copy as recorded in the index: what was generated, where, from which content.</summary>
public sealed record OutputRecord(string DestinationId, string BookId, string Path, string ContentHash, DateTimeOffset GeneratedAt);

public enum SyncActionKind
{
    /// <summary>The book has no copy yet.</summary>
    Create,

    /// <summary>The copy is outdated (metadata, original or name changed).</summary>
    Update,

    /// <summary>The book left the destination (or the library): its copy is removed.</summary>
    Delete,
}

/// <param name="OldPath">Recorded path (Update/Delete), relative to the destination folder.</param>
/// <param name="NewPath">Path to write (Create/Update).</param>
public sealed record SyncAction(SyncActionKind Kind, string BookId, string Title, string? OldPath, string? NewPath, string? ContentHash);

/// <summary>What a sync would do: shown to the user before a batch run (CLAUDE.md, rule 7).</summary>
public sealed record SyncPlan(string DestinationId, IReadOnlyList<SyncAction> Actions, int UpToDate)
{
    public int Creates => Actions.Count(a => a.Kind == SyncActionKind.Create);
    public int Updates => Actions.Count(a => a.Kind == SyncActionKind.Update);
    public int Deletes => Actions.Count(a => a.Kind == SyncActionKind.Delete);
    public bool IsEmpty => Actions.Count == 0;
}

public sealed record SyncResult(int Written, int Deleted, IReadOnlyList<(string Title, string Error)> Failures);

/// <summary>Copies generated per destination, in the library index (per machine, like destinations).</summary>
internal sealed class OutputStore(LibraryIndex index)
{
    public async Task<Dictionary<string, OutputRecord>> GetAsync(string destinationId, CancellationToken ct)
    {
        await using var connection = await index.OpenAsync(ct);
        var rows = await connection.QueryAsync<(string BookId, string Path, string ContentHash, string GeneratedAt)>(new CommandDefinition(
            "SELECT book_id, path, content_hash, generated_at FROM destination_outputs WHERE destination_id = @destinationId;",
            new { destinationId }, cancellationToken: ct));
        return rows.ToDictionary(r => r.BookId, r => new OutputRecord(destinationId, r.BookId, r.Path, r.ContentHash,
            DateTimeOffset.Parse(r.GeneratedAt, System.Globalization.CultureInfo.InvariantCulture)));
    }

    public async Task SaveAsync(string destinationId, string bookId, string path, string hash, CancellationToken ct)
    {
        await using var connection = await index.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO destination_outputs (destination_id, book_id, path, content_hash, generated_at)
            VALUES (@destinationId, @bookId, @path, @hash, @now)
            ON CONFLICT(destination_id, book_id) DO UPDATE SET path = @path, content_hash = @hash, generated_at = @now;
            """,
            new { destinationId, bookId, path, hash, now = DateTimeOffset.UtcNow.ToString("O") }, cancellationToken: ct));
    }

    public async Task DeleteAsync(string destinationId, string? bookId, CancellationToken ct)
    {
        await using var connection = await index.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            bookId is null
                ? "DELETE FROM destination_outputs WHERE destination_id = @destinationId;"
                : "DELETE FROM destination_outputs WHERE destination_id = @destinationId AND book_id = @bookId;",
            new { destinationId, bookId }, cancellationToken: ct));
    }
}

/// <summary>
/// Compares the library with what a destination holds and applies the difference. Copies are
/// written to a temporary file and then renamed; only files the app recorded as its own are ever
/// deleted (decision of 2026-10-17), and never outside the destination folder.
/// </summary>
public sealed class DestinationSync(LibraryInfo library, LibraryIndex index, ILogger logger)
{
    private readonly OutputStore _outputs = new(index);

    /// <param name="onlyBookId">Plan a single book (automatic mode after an edit).</param>
    public async Task<SyncPlan> PlanAsync(IDestination destination, CancellationToken ct, string? onlyBookId = null)
    {
        var books = (await index.GetBooksAsync(ct)).ToDictionary(b => b.Metadata.Id);
        var recorded = await _outputs.GetAsync(destination.Config.Id, ct);
        var actions = new List<SyncAction>();
        var upToDate = 0;

        foreach (var book in books.Values)
        {
            if (onlyBookId is not null && book.Metadata.Id != onlyBookId)
            {
                continue;
            }

            recorded.TryGetValue(book.Metadata.Id, out var current);
            if (!destination.Includes(book.Metadata))
            {
                if (current is not null)
                {
                    actions.Add(new SyncAction(SyncActionKind.Delete, book.Metadata.Id, book.Metadata.Title, current.Path, null, null));
                }

                continue;
            }

            var path = destination.OutputPath(book);
            var hash = destination.ContentSignature(book);
            if (current is null)
            {
                actions.Add(new SyncAction(SyncActionKind.Create, book.Metadata.Id, book.Metadata.Title, null, path, hash));
            }
            else if (current.ContentHash != hash || !string.Equals(current.Path, path, StringComparison.Ordinal)
                     || !File.Exists(Absolute(destination, current.Path)))
            {
                actions.Add(new SyncAction(SyncActionKind.Update, book.Metadata.Id, book.Metadata.Title, current.Path, path, hash));
            }
            else
            {
                upToDate++;
            }
        }

        // Copies of books that are no longer in the library.
        if (onlyBookId is null || !books.ContainsKey(onlyBookId))
        {
            foreach (var orphan in recorded.Values.Where(r => !books.ContainsKey(r.BookId) && (onlyBookId is null || r.BookId == onlyBookId)))
            {
                actions.Add(new SyncAction(SyncActionKind.Delete, orphan.BookId, Path.GetFileNameWithoutExtension(orphan.Path), orphan.Path, null, null));
            }
        }

        return new SyncPlan(destination.Config.Id, actions, upToDate);
    }

    /// <summary>The state of each book's copy, for the book panel ("em dia", "desatualizado", "não gerado").</summary>
    public async Task<Dictionary<string, OutputRecord>> RecordedAsync(string destinationId, CancellationToken ct) =>
        await _outputs.GetAsync(destinationId, ct);

    public async Task<SyncResult> ExecuteAsync(IDestination destination, SyncPlan plan, IProgress<(int Done, int Total, string Title)>? progress, CancellationToken ct)
    {
        var folder = destination.Config.Folder ?? throw new InvalidOperationException("The destination has no folder.");
        var books = (await index.GetBooksAsync(ct)).ToDictionary(b => b.Metadata.Id);
        var failures = new List<(string, string)>();
        int written = 0, deleted = 0, done = 0;

        foreach (var action in plan.Actions)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((done, plan.Actions.Count, action.Title));
            try
            {
                if (action.Kind == SyncActionKind.Delete)
                {
                    DeleteCopy(destination, action.OldPath!);
                    await _outputs.DeleteAsync(destination.Config.Id, action.BookId, ct);
                    deleted++;
                }
                else if (books.TryGetValue(action.BookId, out var book))
                {
                    var target = Absolute(destination, action.NewPath!);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var temp = target + ".gibiomatic.tmp";
                    try
                    {
                        await destination.GenerateAsync(library, book, temp, null, ct);
                        File.Move(temp, target, overwrite: true);
                    }
                    finally
                    {
                        if (File.Exists(temp))
                        {
                            File.Delete(temp);
                        }
                    }

                    if (action.OldPath is { } old && !string.Equals(old, action.NewPath, StringComparison.Ordinal))
                    {
                        DeleteCopy(destination, old);
                    }

                    await _outputs.SaveAsync(destination.Config.Id, action.BookId, action.NewPath!, action.ContentHash!, ct);
                    written++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Destination {Destination}: {Kind} of {Title} failed", destination.Config.Name, action.Kind, action.Title);
                failures.Add((action.Title, ex.Message));
            }

            done++;
        }

        progress?.Report((done, plan.Actions.Count, string.Empty));
        logger.LogInformation("Destination {Destination}: {Written} written, {Deleted} removed, {Failed} failed",
            destination.Config.Name, written, deleted, failures.Count);
        if (written + deleted > 0)
        {
            await destination.AfterSyncAsync(ct);
        }

        return new SyncResult(written, deleted, failures);
    }

    /// <summary>Forgets every copy of a destination (when it is removed; its files stay where they are).</summary>
    public Task ForgetAsync(string destinationId, CancellationToken ct) => _outputs.DeleteAsync(destinationId, null, ct);

    private static string Absolute(IDestination destination, string relative)
    {
        var root = Path.GetFullPath(destination.Config.Folder!);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"'{relative}' is outside the destination folder.");
        }

        return full;
    }

    /// <summary>Deletes a copy recorded by the app and the folders it leaves empty (never the destination folder).</summary>
    private void DeleteCopy(IDestination destination, string relative)
    {
        var file = Absolute(destination, relative);
        if (File.Exists(file))
        {
            File.Delete(file);
            logger.LogInformation("Removed copy {File}", file);
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination.Config.Folder!));
        for (var dir = Path.GetDirectoryName(file); dir is not null && dir.Length > root.Length; dir = Path.GetDirectoryName(dir))
        {
            if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any())
            {
                break;
            }

            Directory.Delete(dir);
        }
    }
}
