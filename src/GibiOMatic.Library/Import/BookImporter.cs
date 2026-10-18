using System.Security.Cryptography;
using GibiOMatic.Conversion;
using GibiOMatic.Conversion.Images;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.IO;
using GibiOMatic.Core.Ids;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Library.Index;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Library.Import;

/// <summary>Raised when an import cannot go on; the message becomes the item's failure reason.</summary>
public sealed class ImportException(InboxMessage reason, string? detail = null) : Exception(detail ?? reason.ToString())
{
    public InboxMessage Reason { get; } = reason;
    public string? Detail { get; } = detail;
}

/// <summary>
/// Copies an analysed file into the library and writes its <c>metadata.json</c>. The source is
/// only read; the copy is checked against the SHA-256 computed during analysis.
/// </summary>
public sealed class BookImporter(IBookFileReader reader, ILogger<BookImporter> logger)
{
    private const int BufferSize = 1 << 20;

    public sealed record Outcome(InboxItemState State, string? BookId = null, InboxMessage Message = InboxMessage.None, string? Detail = null);

    public async Task<Outcome> ImportAsync(LibraryInfo library, LibraryIndex index, string sourcePath, ImportAnalysis analysis,
        ImportDecision decision, IReadOnlyList<BookFormat> preference, IProgress<double>? progress, CancellationToken ct)
    {
        if (decision == ImportDecision.Skip)
        {
            return new Outcome(InboxItemState.Skipped, Message: InboxMessage.SkippedByUser);
        }

        // Re-checked now: an earlier item of the same batch may have added this file or this book.
        if (await index.FindBookBySha256Async(analysis.Sha256, ct) is { } sameFile)
        {
            return new Outcome(InboxItemState.Skipped, sameFile, InboxMessage.AlreadyInLibrary);
        }

        if (!File.Exists(sourcePath))
        {
            throw new ImportException(InboxMessage.NotFound);
        }

        var existing = BookIdentity.FindSameBook(await index.GetBooksAsync(ct), analysis);
        if (existing is null)
        {
            return new Outcome(InboxItemState.Imported, await CreateBookAsync(library, index, sourcePath, analysis, progress, ct));
        }

        await AddToBookAsync(library, index, existing, sourcePath, analysis, decision == ImportDecision.Replace, preference, progress, ct);
        return new Outcome(InboxItemState.Imported, existing.Metadata.Id);
    }

    private async Task<string> CreateBookAsync(LibraryInfo library, LibraryIndex index, string sourcePath, ImportAnalysis analysis,
        IProgress<double>? progress, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var m = analysis.Metadata;
        var draft = new BookMetadata
        {
            Id = Ulid.NewUlid(now),
            Kind = BookIdentity.KindFor(m, analysis.Title),
            Title = analysis.Title,
            Series = m.Series is null ? null : new SeriesInfo(m.Series, m.Number, m.Volume),
            Date = m.Date,
            Publisher = m.Publisher,
            Imprint = m.Imprint,
            Credits = m.Credits,
            Summary = m.Summary,
            Language = m.Language,
            Identifiers = new BookIdentifiers { Isbn = m.Isbn },
            Tags = m.Tags,
            Universes = m.Universes,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var folder = LibraryNaming.BookFolder(draft);
        var folderPath = Path.Combine(library.RootPath, folder);
        if (Directory.Exists(folderPath))
        {
            throw new ImportException(InboxMessage.IoError, $"'{folder}' already exists.");
        }

        Directory.CreateDirectory(folderPath);
        try
        {
            var fileName = LibraryNaming.FileName(draft.Title, analysis.Format);
            var filePath = Path.Combine(folderPath, fileName);
            await CopyVerifiedAsync(sourcePath, filePath, analysis.Sha256, progress, ct);

            var book = draft with { Files = [NewFile(analysis, fileName, FileRole.Original)] };
            await WriteCoverAsync(filePath, analysis.Format, Path.Combine(folderPath, LibraryLayout.CoverFileName), ct);
            var metadataPath = Path.Combine(folderPath, LibraryLayout.MetadataFileName);
            await MetadataFile.WriteAsync(metadataPath, book, ct);
            await index.UpsertBookAsync(new IndexedBook(book, folder, File.GetLastWriteTimeUtc(metadataPath)), ct);

            logger.LogInformation("Imported {Source} as book {BookId} in {Folder}", sourcePath, book.Id, folder);
            return book.Id;
        }
        catch
        {
            // Leave no half-created book behind (the source was only read).
            TryDeleteFolder(folderPath);
            throw;
        }
    }

    /// <summary>
    /// Adds the file to a book that already exists. With <paramref name="replace"/> it becomes the
    /// main original and the current one goes to the trash; otherwise the preferred format is the main one.
    /// </summary>
    private async Task AddToBookAsync(LibraryInfo library, LibraryIndex index, CatalogBook existing, string sourcePath,
        ImportAnalysis analysis, bool replace, IReadOnlyList<BookFormat> preference, IProgress<double>? progress, CancellationToken ct)
    {
        var book = existing.Metadata;
        var folderPath = Path.Combine(library.RootPath, existing.Folder);
        Directory.CreateDirectory(folderPath);

        var fileName = LibraryNaming.Unique(folderPath, LibraryNaming.FileName(book.Title, analysis.Format));
        var filePath = Path.Combine(folderPath, fileName);
        await CopyVerifiedAsync(sourcePath, filePath, analysis.Sha256, progress, ct);

        var newFile = NewFile(analysis, fileName, FileRole.Extra);
        var files = book.Files.ToList();
        var oldMain = existing.MainFile;

        if (replace && oldMain is not null)
        {
            var oldPath = Path.Combine(folderPath, oldMain.Name);
            if (File.Exists(oldPath))
            {
                await BookTrash.MoveAsync(library.RootPath, book, oldPath, oldMain, $"replaced by {analysis.FileName}", ct);
            }

            files.Remove(oldMain);
            files = files.Select(f => f with { Role = FileRole.Extra }).Append(newFile with { Role = FileRole.Original }).ToList();
        }
        else
        {
            files.Add(newFile);
            var main = files
                .OrderBy(f => BookIdentity.Rank(f.Format, preference))
                .ThenBy(f => f == oldMain ? 0 : 1)
                .First();
            files = files.Select(f => f with { Role = f == main ? FileRole.Original : FileRole.Extra }).ToList();
        }

        // The cover follows the main original; a missing cover is always recreated.
        var coverPath = Path.Combine(folderPath, LibraryLayout.CoverFileName);
        var mainNow = files.First(f => f.Role == FileRole.Original);
        if (!File.Exists(coverPath) || mainNow.Name == fileName)
        {
            await WriteCoverAsync(Path.Combine(folderPath, mainNow.Name), mainNow.Format, coverPath, ct);
        }

        var updated = FillMissing(book, analysis.Metadata) with
        {
            Files = files.OrderBy(f => f.Role).ToList(),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var metadataPath = Path.Combine(folderPath, LibraryLayout.MetadataFileName);
        await MetadataFile.WriteWithHistoryAsync(library.RootPath, metadataPath, updated, ct);

        // A publisher or series filled in by the new file moves the book to its proper folder (SPEC §4.2).
        var folder = BookFolders.MoveIfNeeded(library.RootPath, existing.Folder, LibraryNaming.BookFolder(updated), logger);
        metadataPath = Path.Combine(library.RootPath, folder, LibraryLayout.MetadataFileName);
        await index.UpsertBookAsync(new IndexedBook(updated, folder, File.GetLastWriteTimeUtc(metadataPath)), ct);

        logger.LogInformation("Added {Source} to book {BookId} ({Mode})", sourcePath, book.Id, replace ? "replace" : "keep both");
    }

    /// <summary>Existing metadata wins; the new file only fills what is empty.</summary>
    private static BookMetadata FillMissing(BookMetadata book, EmbeddedMetadata m) => book with
    {
        Series = book.Series ?? (m.Series is null ? null : new SeriesInfo(m.Series, m.Number, m.Volume)),
        Date = book.Date ?? m.Date,
        Publisher = book.Publisher ?? m.Publisher,
        Imprint = book.Imprint ?? m.Imprint,
        Credits = book.Credits.Count > 0 ? book.Credits : m.Credits,
        Summary = book.Summary ?? m.Summary,
        Language = book.Language ?? m.Language,
        Identifiers = book.Identifiers.Isbn is null ? book.Identifiers with { Isbn = m.Isbn } : book.Identifiers,
        Tags = book.Tags.Count > 0 ? book.Tags : m.Tags,
        Universes = book.Universes.Count > 0 ? book.Universes : m.Universes,
    };

    private static BookFile NewFile(ImportAnalysis analysis, string name, FileRole role) => new()
    {
        Role = role,
        Name = name,
        DownloadedAs = analysis.FileName,
        Format = analysis.Format,
        Sha256 = analysis.Sha256,
        Bytes = analysis.Bytes,
        Pages = analysis.Metadata.PageCount,
        Quality = analysis.Metadata.LooksConverted ? FileQuality.Converted : FileQuality.Unknown,
    };

    private async Task WriteCoverAsync(string bookFile, BookFormat format, string coverPath, CancellationToken ct)
    {
        var content = await reader.ReadAsync(bookFile, format, ct);
        if (content.Cover is { } raw && CoverImages.CreateCover(raw) is { } jpeg)
        {
            await AtomicFile.WriteAllBytesAsync(coverPath, jpeg, ct);
        }
        else
        {
            logger.LogInformation("No cover for {File}", bookFile);
        }
    }

    /// <summary>Copies through a temporary file, hashing on the way, and only then gives it its name.</summary>
    internal static async Task CopyVerifiedAsync(string source, string target, string expectedSha256, IProgress<double>? progress, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.part");
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                var length = input.Length;
                var buffer = new byte[BufferSize];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    progress?.Report(length == 0 ? 1 : (double)done / length);
                }

                await output.FlushAsync(ct);
            }

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ImportException(InboxMessage.SourceChanged);
            }

            File.Move(temp, target, overwrite: false);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }

            throw;
        }
    }

    private void TryDeleteFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not clean up {Folder} after a failed import", folder);
        }
    }
}
