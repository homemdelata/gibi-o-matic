using System.Security.Cryptography;
using System.Text;
using GibiOMatic.Conversion.Cbz;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Core.Secrets;
using GibiOMatic.Core.Settings;
using GibiOMatic.Library.Index;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Destinations.Komga;

/// <summary>Komga (SPEC §9.2): CBZ + ComicInfo.xml in a folder the server scans; text EPUBs as they are.</summary>
public sealed class KomgaDestinationType(KomgaClient client, ISecretStore secrets, ILoggerFactory loggers) : IDestinationType
{
    public const string TypeId = "komga";

    public string Id => TypeId;

    // Komga reads EPUB reflow since 1.8, so text books can go too (off by default).
    public IReadOnlySet<BookKind> AcceptedKinds { get; } =
        new HashSet<BookKind> { BookKind.Issue, BookKind.Tpb, BookKind.Omnibus, BookKind.Artbook, BookKind.Book };

    public DestinationConfig CreateDefault(string id, string name) => new()
    {
        Id = id,
        Type = TypeId,
        Name = name,
        Which = DestinationScope.AllBooks,
        When = DestinationTiming.Automatic,
        OneShotsFolder = OutputNames.DefaultOneShotsFolder,
    };

    public IDestination Create(DestinationConfig config) =>
        new KomgaDestination(config, client, secrets, loggers.CreateLogger<KomgaDestination>());
}

public sealed class KomgaDestination(DestinationConfig config, KomgaClient client, ISecretStore secrets, ILogger<KomgaDestination> logger) : IDestination
{
    /// <summary>Bump when the generated files change for the same book (forces regeneration).</summary>
    private const string GeneratorVersion = "komga-1";

    public DestinationConfig Config { get; } = config;

    public DestinationStatus GetStatus()
    {
        if (string.IsNullOrWhiteSpace(Config.Folder))
        {
            return new DestinationStatus(DestinationHealth.NotConfigured);
        }

        return Directory.Exists(Config.Folder)
            ? new DestinationStatus(DestinationHealth.Ready)
            : new DestinationStatus(DestinationHealth.Unavailable, Config.Folder);
    }

    public bool Includes(BookMetadata book)
    {
        if (book.Review.Status != ReviewStatus.Approved && !Config.IncludeUnreviewed)
        {
            return false;
        }

        if (Config.Which == DestinationScope.ChosenBooks && !Config.ChosenBookIds.Contains(book.Id))
        {
            return false;
        }

        var main = book.Files.FirstOrDefault(f => f.Role == FileRole.Original);
        if (main is null)
        {
            return false;
        }

        // Text books only as EPUB, and only when the option is on.
        return book.Kind != BookKind.Book || (Config.IncludeTextBooks && main.Format == BookFormat.Epub);
    }

    private static bool AsEpub(BookMetadata book) => book.Kind == BookKind.Book;

    public string OutputPath(CatalogBook book) =>
        OutputNames.KomgaPath(book.Metadata, Config.OneShotsFolder, Config.SeriesTemplate, Config.OneShotTemplate)
        + (AsEpub(book.Metadata) ? ".epub" : ".cbz");

    public string ContentSignature(CatalogBook book)
    {
        var main = book.MainFile;
        var text = $"{GeneratorVersion}\n{main?.Sha256}\n{(AsEpub(book.Metadata) ? "epub" : ComicInfoWriter.Write(book.Metadata, main?.Pages))}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public async Task GenerateAsync(LibraryInfo library, CatalogBook book, string target, IProgress<double>? progress, CancellationToken ct)
    {
        var main = book.MainFile ?? throw new InvalidOperationException($"Book {book.Metadata.Id} has no original.");
        var source = Path.Combine(library.RootPath, book.Folder, main.Name);
        if (AsEpub(book.Metadata))
        {
            // Text EPUBs go as they are: Komga has its own reader for them.
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous);
            await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
            await input.CopyToAsync(output, ct);
            progress?.Report(1);
            return;
        }

        var result = await CbzBuilder.BuildAsync(source, main.Format, book.Metadata, target, progress, ct);
        logger.LogInformation("Built {Target}: {Pages} pages ({Original} copied as they were)", target, result.Pages, result.OriginalPages);
    }

    public async Task<AfterSyncResult?> AfterSyncAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Config.ServerUrl) || string.IsNullOrWhiteSpace(Config.KomgaLibraryId) || !secrets.IsSupported
            || await secrets.GetAsync(Config.SecretName, ct) is not { Length: > 0 } key)
        {
            return null;
        }

        try
        {
            await client.ScanAsync(Config.ServerUrl, key, Config.KomgaLibraryId, ct);
            return new AfterSyncResult(true);
        }
        catch (KomgaException ex)
        {
            // The files are there; Komga will find them on its own scheduled scan.
            logger.LogWarning(ex, "Komga scan request failed: {Message}", ex.Message);
            return new AfterSyncResult(false, ex.Message);
        }
    }
}
