using GibiOMatic.Conversion.Archives;
using GibiOMatic.Conversion.Epub;
using GibiOMatic.Conversion.Pdf;
using GibiOMatic.Core.Domain;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Conversion;

/// <summary>Reads embedded metadata and the cover of PDF, CBZ, CBR and EPUB files.</summary>
public interface IBookFileReader
{
    /// <summary>
    /// Never throws for a damaged file: what could not be read becomes a warning, so the
    /// original can still be imported.
    /// </summary>
    Task<BookFileContent> ReadAsync(string path, BookFormat format, CancellationToken ct = default);
}

public sealed class BookFileReader(ILogger<BookFileReader> logger) : IBookFileReader
{
    public Task<BookFileContent> ReadAsync(string path, BookFormat format, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return format switch
                {
                    BookFormat.Cbz or BookFormat.Cbr => ComicArchiveReader.Read(path),
                    BookFormat.Epub => EpubReader.Read(path),
                    BookFormat.Pdf => PdfReader.Read(path),
                    _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
                };
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or ArgumentOutOfRangeException))
            {
                logger.LogWarning(ex, "Could not read {Format} content of {Path}", format, path);
                return new BookFileContent(EmbeddedMetadata.Empty, null, [$"The file could not be read: {ex.Message}"]);
            }
        }, ct);
}
