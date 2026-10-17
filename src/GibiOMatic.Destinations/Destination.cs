using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Core.Settings;
using GibiOMatic.Library.Index;

namespace GibiOMatic.Destinations;

/// <summary>A kind of destination (SPEC §9.1): Komga now, Kindle in F4.</summary>
public interface IDestinationType
{
    string Id { get; }

    /// <summary>Kinds of book the type can receive; "book" (text) only when it reads EPUB reflow.</summary>
    IReadOnlySet<BookKind> AcceptedKinds { get; }

    bool AcceptsTextBooks => AcceptedKinds.Contains(BookKind.Book);

    DestinationConfig CreateDefault(string id, string name);

    IDestination Create(DestinationConfig config);
}

public enum DestinationHealth
{
    Ready,

    /// <summary>The folder is not reachable (network drive off, Kindle unplugged…).</summary>
    Unavailable,
    NotConfigured,
}

public sealed record DestinationStatus(DestinationHealth Health, string? Detail = null);

/// <summary>One configured destination (SPEC §9.1).</summary>
public interface IDestination
{
    DestinationConfig Config { get; }

    DestinationStatus GetStatus();

    /// <summary>The "quais livros" rule plus what the type accepts.</summary>
    bool Includes(BookMetadata book);

    /// <summary>Path of the book's copy relative to the destination folder, with extension.</summary>
    string OutputPath(CatalogBook book);

    /// <summary>
    /// Text that changes whenever the copy would change (metadata written into it, original,
    /// generator version). Copies whose hash differs are "desatualizadas".
    /// </summary>
    string ContentSignature(CatalogBook book);

    /// <summary>Writes the copy of <paramref name="book"/> to <paramref name="target"/> (a temporary file).</summary>
    Task GenerateAsync(LibraryInfo library, CatalogBook book, string target, IProgress<double>? progress, CancellationToken ct);

    /// <summary>
    /// Called after a sync wrote or removed files (Komga: ask the server to scan).
    /// Returns what happened, for the destination's history, or null when there was nothing to do.
    /// </summary>
    Task<AfterSyncResult?> AfterSyncAsync(CancellationToken ct);
}

/// <param name="Message">Technical detail (the server's error), shown as is in the history.</param>
public sealed record AfterSyncResult(bool Success, string? Message = null);
