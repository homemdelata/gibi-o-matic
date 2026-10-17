using System.Security.Cryptography;
using GibiOMatic.Conversion;
using GibiOMatic.Conversion.Images;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Core.Text;
using GibiOMatic.Library.Index;

namespace GibiOMatic.Library.Import;

/// <summary>Analyses a file without changing anything: format, SHA-256, embedded metadata, cover, duplicates.</summary>
public sealed class ImportAnalyzer(IBookFileReader reader, ThumbnailCache thumbnails)
{
    private const int BufferSize = 1 << 20;

    public sealed record Outcome(ImportAnalysis? Analysis, InboxMessage Failure = InboxMessage.None, string? Detail = null);

    public async Task<Outcome> AnalyzeAsync(LibraryInfo library, LibraryIndex index, long itemId, string path,
        IProgress<double>? progress, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return new Outcome(null, InboxMessage.NotFound);
        }

        var format = FormatDetector.Detect(path);
        if (format is null)
        {
            return new Outcome(null, InboxMessage.UnsupportedFormat);
        }

        var bytes = new FileInfo(path).Length;
        var sha256 = await HashAsync(path, bytes, progress, ct);
        var content = await reader.ReadAsync(path, format.Value, ct);

        var hasThumbnail = false;
        if (content.Cover is { } cover && CoverImages.CreateThumbnail(cover) is { } thumbnail)
        {
            await thumbnails.SaveInboxThumbnailAsync(library, itemId, thumbnail, ct);
            hasThumbnail = true;
        }

        var fileName = Path.GetFileName(path);
        var metadata = content.Metadata with { Isbn = content.Metadata.Isbn is { } isbn ? Isbn.Normalize(isbn) : null };
        var title = BookIdentity.TitleFor(metadata, fileName);
        var analysis = new ImportAnalysis
        {
            FileName = fileName,
            Format = format.Value,
            Bytes = bytes,
            Sha256 = sha256,
            Title = title,
            BookKey = BookIdentity.KeyOf(title),
            Metadata = metadata,
            Warnings = content.Warnings,
            HasThumbnail = hasThumbnail,
        };

        return new Outcome(analysis with { Match = await FindMatchAsync(index, analysis, ct) });
    }

    private static async Task<LibraryMatch?> FindMatchAsync(LibraryIndex index, ImportAnalysis analysis, CancellationToken ct)
    {
        if (await index.FindBookBySha256Async(analysis.Sha256, ct) is { } sameFileId && await index.GetBookAsync(sameFileId, ct) is { } sameFile)
        {
            return ToMatch(sameFile, sameFile: true);
        }

        var sameBook = BookIdentity.FindSameBook(await index.GetBooksAsync(ct), analysis);
        return sameBook is null ? null : ToMatch(sameBook, sameFile: false);
    }

    private static LibraryMatch ToMatch(CatalogBook book, bool sameFile) =>
        new(sameFile, book.Metadata.Id, book.Metadata.Title, book.MainFile?.Format, book.MainFile?.Quality ?? Core.Domain.FileQuality.Unknown);

    internal static async Task<string> HashAsync(string path, long length, IProgress<double>? progress, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[BufferSize];
        long done = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            done += read;
            progress?.Report(length == 0 ? 1 : (double)done / length);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
