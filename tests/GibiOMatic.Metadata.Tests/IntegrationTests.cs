using GibiOMatic.Core.Settings;
using GibiOMatic.Metadata.Http;
using GibiOMatic.Metadata.Identification;
using GibiOMatic.Metadata.Sources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace GibiOMatic.Metadata.Tests;

/// <summary>
/// Calls the real APIs. Skipped without a ComicVine key (user secrets in development, or the
/// GIBIOMATIC_COMICVINE_KEY variable). Responses are cached across runs, so repeated test runs
/// do not hit the services again.
/// </summary>
public sealed class IntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string? Key = new ConfigurationBuilder()
        .AddUserSecrets(typeof(IntegrationTests).Assembly, optional: true)
        .AddEnvironmentVariables()
        .Build() is var config
            ? config["ComicVine:ApiKey"] ?? config["GIBIOMATIC_COMICVINE_KEY"]
            : null;

    private static readonly ResponseCache Cache = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GibiOMatic", "test-cache"));

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static SourceClient Client(string source, TimeSpan interval) =>
        new(source, Http, Cache, new RateLimiter(interval, 50), RetryPolicy.Default, TimeSpan.FromDays(30), NullLogger.Instance);

    private static void RequireKey()
    {
        if (string.IsNullOrWhiteSpace(Key))
        {
            Assert.Skip("No ComicVine key (user secrets or GIBIOMATIC_COMICVINE_KEY).");
        }
    }

    [Fact]
    public async Task Real_identification_of_a_trade_paperback_by_isbn()
    {
        RequireKey();
        var cv = new ComicVineClient(Client(SourceIds.ComicVine, TimeSpan.FromSeconds(1.5)), new FixedKey(Key));
        var ol = new OpenLibraryClient(Client(SourceIds.OpenLibrary, TimeSpan.FromSeconds(1)));
        var identifier = new BookIdentifier([ol], cv, NullLogger<BookIdentifier>.Instance);
        var book = new Core.Domain.BookMetadata
        {
            Id = "01ARYZ6S41TSV4RRFFQ69G5FAV",
            Title = "deadlyclass vol11",
            Identifiers = new Core.Domain.BookIdentifiers { Isbn = "9781534321236" },
        };

        var result = await identifier.IdentifyAsync(book, new MetadataSettings(), null, Ct);

        var best = result.Best;
        Assert.NotNull(best);
        Assert.Equal("4000-937068", best.Record.ComicVineId);
        Assert.Equal(("Deadly Class", "11"), (best.Record.SeriesName, best.Record.Number));
        Assert.Contains(best.Record.Credits, c => c.Name == "Rick Remender" && c.Role == Core.Domain.CreditRole.Writer);
        Assert.True(Proposal.CanAutoApprove(book, result, new MetadataSettings(), out var reason), reason);
    }
}
