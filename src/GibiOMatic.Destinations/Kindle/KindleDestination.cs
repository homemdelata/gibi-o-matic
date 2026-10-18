using System.Security.Cryptography;
using System.Text;
using GibiOMatic.Conversion.Cbz;
using GibiOMatic.Conversion.Kcc;
using GibiOMatic.Core;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Core.Settings;
using GibiOMatic.Destinations.Sync;
using GibiOMatic.Library.Index;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Destinations.Kindle;

/// <summary>
/// Kindle (SPEC §9.3, F4): the books chosen are converted by the KCC into a local folder while the
/// device is away ("preparar"); they are copied to it when it is connected and the user confirms
/// ("enviar"). The app never deletes anything from the device on its own.
/// </summary>
public sealed class KindleDestinationType(IKccRunner kcc, AppPaths paths, ILoggerFactory loggers) : IDestinationType
{
    public const string TypeId = "kindle";

    /// <summary>The Kindle Colorsoft, the user's device.</summary>
    public const string DefaultProfile = "KCS";

    public string Id => TypeId;

    // The KCC converts images only: text books would need another converter (SPEC §9.1, backlog).
    public IReadOnlySet<BookKind> AcceptedKinds { get; } =
        new HashSet<BookKind> { BookKind.Issue, BookKind.Tpb, BookKind.Omnibus, BookKind.Artbook };

    /// <summary>Only the books chosen by hand, prepared as soon as they are chosen (decision of 2026-10-18).</summary>
    public DestinationConfig CreateDefault(string id, string name) => new()
    {
        Id = id,
        Type = TypeId,
        Name = name,
        Which = DestinationScope.ChosenBooks,
        When = DestinationTiming.Automatic,
        IncludeUnreviewed = true,
        Folder = Path.Combine(paths.KindleDirectory, id),
        OneShotsFolder = null,
        KindleProfile = DefaultProfile,
    };

    public IDestination Create(DestinationConfig config) =>
        new KindleDestination(config, kcc, paths, loggers.CreateLogger<KindleDestination>());
}

public sealed class KindleDestination(DestinationConfig config, IKccRunner kcc, AppPaths paths, ILogger<KindleDestination> logger) : IDestination
{
    /// <summary>Bump when the generated files change for the same book (forces a new conversion).</summary>
    private const string GeneratorVersion = "kindle-1";

    /// <summary>A big omnibus takes minutes; a stuck KCC is stopped after this.</summary>
    public static readonly TimeSpan ConversionTimeout = TimeSpan.FromMinutes(45);

    public DestinationConfig Config { get; } = config;

    /// <summary>The KCC tool that will be used, or null when there is none.</summary>
    public string? KccPath => KccTool.Locate(Config.KccPath, paths.ToolsDirectory);

    public KccOptions Options => new()
    {
        Profile = string.IsNullOrWhiteSpace(Config.KindleProfile) ? KindleDestinationType.DefaultProfile : Config.KindleProfile,
        Color = Config.KindleColor,
        Manga = Config.KindleManga,
        Splitter = Config.KindleSplitter,
        Cropping = Config.KindleCropping,
        ExtraArguments = Config.KccArguments,
    };

    /// <summary>Preparing needs the KCC; the device itself is only needed to send (<see cref="KindleSender"/>).</summary>
    public DestinationStatus GetStatus()
    {
        if (string.IsNullOrWhiteSpace(Config.Folder))
        {
            return new DestinationStatus(DestinationHealth.NotConfigured);
        }

        return KccPath is null
            ? new DestinationStatus(DestinationHealth.NotConfigured, "kcc")
            : new DestinationStatus(DestinationHealth.Ready);
    }

    public bool Includes(BookMetadata book) =>
        Config.ChosenBookIds.Contains(book.Id)
        && book.Kind != BookKind.Book
        && book.Files.Any(f => f.Role == FileRole.Original);

    public string OutputPath(CatalogBook book) => OutputNames.Kindle(book.Metadata);

    public string ContentSignature(CatalogBook book)
    {
        var o = Options;
        var text = string.Join('\n', GeneratorVersion, book.MainFile?.Sha256, book.Metadata.Title, AuthorOf(book.Metadata),
            o.Profile, o.Color, o.Manga, o.Splitter, o.Cropping, o.ExtraArguments);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Prepared, or sent with this same content (its prepared file is deleted once sent).</summary>
    public bool HasCopy(OutputRecord record, string absolutePath) =>
        File.Exists(absolutePath) || KindleSentStore.Read(Config.Folder!, record.BookId)?.ContentHash == record.ContentHash;

    /// <summary>A CBZ of the original (with its metadata), then the KCC turns it into a MOBI for the device.</summary>
    public async Task GenerateAsync(LibraryInfo library, CatalogBook book, string target, IProgress<double>? progress, CancellationToken ct)
    {
        var main = book.MainFile ?? throw new InvalidOperationException($"Book {book.Metadata.Id} has no original.");
        var tool = KccPath ?? throw new KccException("The KCC (Kindle Comic Converter) was not found.");
        var source = Path.Combine(library.RootPath, book.Folder, main.Name);
        var work = Path.Combine(Config.Folder!, ".work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var cbz = Path.Combine(work, "book.cbz");
            await CbzBuilder.BuildAsync(source, main.Format, book.Metadata, cbz, Scaled(progress, 0, 0.3), ct);
            var job = new KccJob(tool, cbz, Path.Combine(work, "out"), Options, book.Metadata.Title, AuthorOf(book.Metadata), ConversionTimeout);
            var mobi = await kcc.ConvertAsync(job, Scaled(progress, 0.3, 1), ct);
            File.Move(mobi, target, overwrite: true);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not remove the work folder {Folder}", work);
            }
        }
    }

    public Task<AfterSyncResult?> AfterSyncAsync(CancellationToken ct) => Task.FromResult<AfterSyncResult?>(null);

    /// <summary>The writers (or else the artists), as the Kindle shows under the title.</summary>
    internal static string? AuthorOf(BookMetadata book)
    {
        var names = book.Credits.Where(c => c.Role == CreditRole.Writer).Select(c => c.Name).Distinct().ToList();
        if (names.Count == 0)
        {
            names = book.Credits.Where(c => c.Role == CreditRole.Artist).Select(c => c.Name).Distinct().ToList();
        }

        return names.Count == 0 ? null : string.Join(", ", names);
    }

    private static Progress? Scaled(IProgress<double>? progress, double from, double to) =>
        progress is null ? null : new Progress(f => progress.Report(from + (Math.Clamp(f, 0, 1) * (to - from))));

    private sealed class Progress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
