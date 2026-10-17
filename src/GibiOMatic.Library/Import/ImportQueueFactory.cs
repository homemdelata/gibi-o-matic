using GibiOMatic.Core.Libraries;
using GibiOMatic.Core.Settings;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Library.Import;

/// <summary>Creates the inbox queue of an open library.</summary>
public sealed class ImportQueueFactory(
    LibraryService libraries,
    ImportAnalyzer analyzer,
    BookImporter importer,
    ThumbnailCache thumbnails,
    ISettingsService settings,
    ILogger<ImportQueue> logger)
{
    public ImportQueue Create(LibraryInfo library) =>
        new(library, libraries.GetIndex(library), analyzer, importer, thumbnails, settings, logger);
}
