using GibiOMatic.Conversion;
using GibiOMatic.Core;
using GibiOMatic.Core.Settings;
using GibiOMatic.Library.Editing;
using GibiOMatic.Library.Identification;
using GibiOMatic.Library.Import;
using GibiOMatic.Metadata;
using GibiOMatic.Metadata.Assisted;
using GibiOMatic.Metadata.Http;
using GibiOMatic.Metadata.Identification;
using GibiOMatic.Metadata.Sources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace GibiOMatic.Library.Tests;

/// <summary>
/// The whole assisted identification with the real Claude Code, the real ComicVine and the built
/// app as MCP server and hook (SPEC §8.4). It uses the developer's Claude plan, so it runs only when
/// GIBIOMATIC_CLAUDE_E2E_BOOK names a comic on disk (never committed), Claude Code is logged in and
/// there is a ComicVine key. Build the app first (dotnet build src/GibiOMatic.App).
/// </summary>
public sealed class ClaudeEndToEndTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Key(string key) : IComicVineKeyProvider
    {
        public Task<string?> GetKeyAsync(CancellationToken ct) => Task.FromResult<string?>(key);
    }

    [Fact]
    public async Task A_real_book_is_identified_by_the_real_claude_code()
    {
        var bookPath = Environment.GetEnvironmentVariable("GIBIOMATIC_CLAUDE_E2E_BOOK");
        if (string.IsNullOrWhiteSpace(bookPath) || !File.Exists(bookPath))
        {
            Assert.Skip("Set GIBIOMATIC_CLAUDE_E2E_BOOK to a comic file to run it (it uses your Claude plan).");
        }

        var app = Environment.GetEnvironmentVariable("GIBIOMATIC_CLAUDE_E2E_APP")
                  ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GibiOMatic.App", "bin", "Debug", "net10.0",
                      OperatingSystem.IsWindows() ? "GibiOMatic.exe" : "GibiOMatic"));
        if (!File.Exists(app))
        {
            Assert.Skip($"Build the app first: {app} is missing.");
        }

        var config = new ConfigurationBuilder().AddUserSecrets(typeof(ClaudeEndToEndTests).Assembly, optional: true).AddEnvironmentVariables().Build();
        var key = config["ComicVine:ApiKey"] ?? config["GIBIOMATIC_COMICVINE_KEY"] ?? string.Empty;
        if (key.Length == 0)
        {
            Assert.Skip("No ComicVine key (user secrets or GIBIOMATIC_COMICVINE_KEY).");
        }

        var claude = new ClaudeCodeCli(NullLogger<ClaudeCodeCli>.Instance);
        if (!(await claude.GetStatusAsync(Ct)).IsReady)
        {
            Assert.Skip("Claude Code is not installed or not logged in.");
        }

        // A library of one book, in a temporary folder.
        var paths = new AppPaths(_temp.Combine("config"), _temp.Combine("data"));
        var libraries = new LibraryService(paths, NullLogger<LibraryService>.Instance);
        var settings = new JsonSettingsService(paths, NullLogger<JsonSettingsService>.Instance);
        await settings.SaveAsync(new AppSettings { Claude = new ClaudeSettings { Enabled = true } }, Ct);
        var reader = new BookFileReader(NullLogger<BookFileReader>.Instance);
        var thumbnails = new ThumbnailCache(paths, NullLogger<ThumbnailCache>.Instance);
        var library = await libraries.CreateAsync(_temp.Combine("Library"), Ct);
        await using (var imports = new ImportQueueFactory(libraries, new ImportAnalyzer(reader, thumbnails), new BookImporter(reader, NullLogger<BookImporter>.Instance),
                         thumbnails, settings, new FakeRecycleBin(_temp.Combine("bin")), NullLogger<ImportQueue>.Instance).Create(library))
        {
            await imports.StartAsync(Ct);
            var copy = _temp.Combine("Downloads", Path.GetFileName(bookPath));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(bookPath, copy);
            await imports.AddAsync([copy], Ct);
            await imports.WaitUntilIdleAsync(Ct);
            await imports.ConfirmAsync(Ct);
            await imports.WaitUntilIdleAsync(Ct);
        }

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var cache = new ResponseCache(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GibiOMatic", "test-cache"));
        var comicVine = new ComicVineClient(new SourceClient(SourceIds.ComicVine, http, cache, new RateLimiter(TimeSpan.FromSeconds(1.5), 50),
            RetryPolicy.Default, TimeSpan.FromDays(30), NullLogger.Instance), new Key(key));
        var identifier = new BookIdentifier([], comicVine, NullLogger<BookIdentifier>.Instance);
        var covers = new CoverImageCache(http, _temp.Combine("covers"), NullLogger<CoverImageCache>.Instance);
        var editor = new BookEditor(libraries, reader, NullLogger<BookEditor>.Instance);
        await using var queue = new IdentificationQueueFactory(libraries, identifier, editor, covers, settings, NullLogger<IdentificationQueue>.Instance)
            .Create(library);
        await queue.StartAsync(Ct);
        await queue.WaitUntilIdleAsync(Ct);
        var assisted = new AssistedIdentifier(claude, identifier, comicVine, covers, settings, paths, new ClaudeBridge(app, []),
            NullLogger<AssistedIdentifier>.Instance);
        await using var claudeQueue = new AssistedQueue(library, libraries.GetIndex(library), queue, assisted, settings, NullLogger<AssistedQueue>.Instance);

        var book = (await libraries.GetIndex(library).GetBooksAsync(Ct)).Single();
        var result = await claudeQueue.RunBookAsync(book.Metadata.Id, AssistedLevel.Quick, Ct);

        var run = Directory.GetDirectories(assisted.RunsDirectory).Single();
        TestContext.Current.TestOutputHelper?.WriteLine($"{result?.Outcome} {result?.Candidate?.Record.SourceName} cover={result?.Candidate?.Assisted?.CoverMatches} " +
                                                  $"{result?.Duration.TotalSeconds:0}s, {result?.Turns} turns, run folder {run}: " +
                                                  string.Join(", ", Directory.GetFiles(run).Select(Path.GetFileName)));
        Assert.NotNull(result);
        Assert.True(result.Outcome is AssistedOutcome.Found or AssistedOutcome.EditionMissing, $"{result.Outcome}: {result.Message}");
        Assert.NotNull(result.Candidate);

        // Optionally, the record the book is known to be ("4000-457596").
        if (Environment.GetEnvironmentVariable("GIBIOMATIC_CLAUDE_E2E_EXPECT") is { Length: > 0 } expected)
        {
            Assert.Equal(expected, result.Candidate.Record.ComicVineId);
        }

        // The app's own server and guard were used: ComicVine calls and tool calls were logged in the run folder.
        Assert.True(File.Exists(Path.Combine(run, ComicVineMcpServer.LogFile)), "the MCP server was not used");
        Assert.True(File.Exists(Path.Combine(run, SiteGuard.LogFile)), "the hook was not used");
    }
}
