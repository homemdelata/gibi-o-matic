using System.Globalization;
using GibiOMatic.Core.Domain;
using GibiOMatic.Metadata.Http;
using GibiOMatic.Metadata.Identification;
using GibiOMatic.Metadata.Sources;
using GibiOMatic.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace GibiOMatic.Metadata.Tests;

/// <summary>Matching inside a volume for the Review by series (SPEC §7.1), on recorded ComicVine answers.</summary>
public sealed class VolumeMatcherTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly CvVolume DeadlyClass = new(90001, "Deadly Class", 2014, "Image", 4);

    /// <summary>The answer of <c>issues/?filter=volume:90001</c>, as ComicVine writes it.</summary>
    private const string DeadlyClassIssues = """
        {"status_code":1,"error":"OK","number_of_total_results":4,"results":[
         {"id":5001,"name":"Reagan Youth","issue_number":"1","cover_date":"2014-11-01","store_date":"2014-11-12","volume":{"id":90001,"name":"Deadly Class"},
          "image":{"small_url":"https://cv/s/5001.jpg","original_url":"https://cv/o/5001.jpg"}},
         {"id":5002,"name":"Kids of the Black Hole","issue_number":"2","cover_date":"2015-06-01","store_date":"2015-06-10","volume":{"id":90001,"name":"Deadly Class"}},
         {"id":5003,"name":"Volume 3: The Snake Pit","issue_number":"3","cover_date":"2016-01-01","store_date":"2016-01-13","volume":{"id":90001,"name":"Deadly Class"}},
         {"id":5005,"name":"Volume 5","issue_number":"5","cover_date":"2017-01-01","volume":{"id":90001,"name":"Deadly Class"}}]}
        """;

    private (VolumeMatcher Matcher, FakeHttp Http) Create(Func<HttpRequestMessage, string> answer)
    {
        var http = new FakeHttp(r => FakeHttp.Json(answer(r)));
        var client = new SourceClient("comicvine", new HttpClient(http), new ResponseCache(_temp.Path), new RateLimiter(TimeSpan.Zero, 1000),
            new RetryPolicy(1, TimeSpan.Zero, TimeSpan.Zero), TimeSpan.FromDays(1), NullLogger.Instance);
        return (new VolumeMatcher(new ComicVineClient(client, new FixedKey("k"))), http);
    }

    private static BookMetadata Book(string id, string title) => new() { Id = id, Title = title, Publisher = "Image" };

    private static MetadataCandidate Own(string volumeName, int volumeId, string number) => new(new MetadataRecord
    {
        Source = SourceIds.ComicVine,
        Title = $"{volumeName}, Vol. {number}",
        SeriesName = "Deadly Class",
        Number = number,
        Kind = BookKind.Tpb,
        ComicVineId = "4000-77" + number,
        ComicVineVolumeId = volumeId,
        VolumeName = volumeName,
        IssueName = "Volume " + number,
    }, 0.8, [MatchReason.Title]);

    [Fact]
    public async Task Each_book_gets_the_issue_with_its_number_then_its_own_candidate_then_nothing()
    {
        var (matcher, http) = Create(_ => DeadlyClassIssues);
        VolumeBook[] books =
        [
            new(Book("a", "Deadly Class, Vol. 1"), "1", null),
            new(Book("b", "DeadlyClass_v3"), "3", Own("Deadly Class", 71032, "11")),
            new(Book("c", "Deadly Class Vol. 4"), "4", Own("Deadly Class: Die for Me", 95000, "4")),
            new(Book("d", "Deadly Class Vol. 6"), "6", Own("Deadly Class: Love Like Blood", 96000, "1")),
            new(Book("e", "Deadly Class - Extra"), null, Own("Deadly Class: Extra", 97000, "1")),
        ];

        var matches = await matcher.MatchAsync(DeadlyClass, books, Ct);

        Assert.Equal([VolumeMatchKind.VolumeIssue, VolumeMatchKind.VolumeIssue, VolumeMatchKind.OwnCandidate, VolumeMatchKind.None, VolumeMatchKind.None],
            matches.Select(m => m.Kind));
        Assert.Equal("4000-5001", matches[0].Candidate!.Record.ComicVineId);
        Assert.Equal("1", matches[0].Candidate!.Record.Number);
        Assert.Equal(90001, matches[1].Candidate!.Record.ComicVineVolumeId);
        Assert.Equal("3", matches[1].Candidate!.Record.Number);
        Assert.Equal("4000-774", matches[2].Candidate!.Record.ComicVineId);

        // One request for the whole group, filtered by the volume.
        var request = Assert.Single(http.Requests);
        Assert.Contains("/issues/", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("filter=volume:90001", Uri.UnescapeDataString(request.RequestUri.Query), StringComparison.Ordinal);

        // Cached: matching the group again asks nothing.
        await matcher.MatchAsync(DeadlyClass, books, Ct);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Long_volumes_are_read_in_pages_of_100()
    {
        var (matcher, http) = Create(r =>
        {
            var offset = r.RequestUri!.Query.Contains("offset=100", StringComparison.Ordinal) ? 100 : 0;
            var count = offset == 0 ? 100 : 50;
            var issues = Enumerable.Range(offset + 1, count).Select(n =>
                "{\"id\":" + (n + 7000).ToString(CultureInfo.InvariantCulture) + ",\"issue_number\":\"" + n.ToString(CultureInfo.InvariantCulture)
                + "\",\"volume\":{\"id\":4,\"name\":\"Spawn\"}}");
            return "{\"status_code\":1,\"error\":\"OK\",\"results\":[" + string.Join(',', issues) + "]}";
        });

        var issues = await matcher.GetIssuesAsync(new CvVolume(4, "Spawn", 1992, "Image", 150), Ct);

        Assert.Equal(150, issues.Issues.Count);
        Assert.Equal(2, http.Requests.Count);
        var match = VolumeMatcher.Match(issues, new VolumeBook(Book("s", "Spawn #120"), "120", null));
        Assert.Equal("4000-7120", match.Candidate!.Record.ComicVineId);
    }

    [Fact]
    public async Task A_link_gives_its_volume()
    {
        var (matcher, _) = Create(r => r.RequestUri!.AbsolutePath.Contains("/issue/", StringComparison.Ordinal)
            ? """{"status_code":1,"error":"OK","results":{"id":5003,"issue_number":"3","volume":{"id":90001,"name":"Deadly Class"}}}"""
            : """{"status_code":1,"error":"OK","results":{"id":90001,"name":"Deadly Class","start_year":"2014","publisher":{"name":"Image"},"count_of_issues":4}}""");

        var byIssue = await matcher.FromLinkAsync("https://comicvine.gamespot.com/deadly-class-3/4000-5003/", Ct);
        var byVolume = await matcher.FromLinkAsync("https://comicvine.gamespot.com/deadly-class/4050-90001/", Ct);

        Assert.Equal(90001, byIssue?.Id);
        Assert.Equal("Deadly Class", byVolume?.Name);
        Assert.Null(await matcher.FromLinkAsync("https://example.com", Ct));
    }

    [Fact]
    public async Task A_search_puts_the_volumes_that_fit_the_group_first()
    {
        var (matcher, http) = Create(_ => """
            {"status_code":1,"error":"OK","results":[
             {"id":1,"name":"Saga","start_year":"1999","publisher":{"name":"Other Press"},"count_of_issues":70},
             {"id":2,"name":"Saga","start_year":"2010","publisher":{"name":"Image Comics"},"count_of_issues":1},
             {"id":3,"name":"Saga","start_year":"2012","publisher":{"name":"Image"},"count_of_issues":66,
              "image":{"small_url":"https://cv/s/3.jpg","original_url":"https://cv/o/3.jpg"}},
             {"id":4,"name":"Saga of the Swamp Thing","start_year":"1982","publisher":{"name":"DC Comics"},"count_of_issues":45}]}
            """);

        var found = await matcher.SearchAsync("Saga", "Image", 9, Ct);

        Assert.Equal([3, 2, 1, 4], found.Select(v => v.Id));
        Assert.Equal("https://cv/s/3.jpg", found[0].CoverUrl);
        var request = Assert.Single(http.Requests);
        Assert.Contains("resources=volume", request.RequestUri!.Query, StringComparison.Ordinal);

        // Nothing known about the group: ComicVine's own order.
        Assert.Equal([1, 2, 3, 4], VolumeMatcher.Rank(found.OrderBy(v => v.Id).ToList(), null, 0).Select(v => v.Id));
    }

    [Theory]
    [InlineData("Deadly Class", "Volume 3: The Snake Pit", null, "The Snake Pit")]
    [InlineData("Deadly Class: A Fond Farewell Part One", "Volume 11", null, "A Fond Farewell Part One")]
    [InlineData("Spawn Origins Collection", "The Kingdom, Part 2", null, "The Kingdom, Part 2")]
    [InlineData("Saga", null, null, null)]
    [InlineData(null, null, "Deadly Class: Reagan Youth #1 · Vol. 1", "Reagan Youth")]
    public void The_name_field_is_the_issue_name_or_the_volume_subtitle(string? volume, string? issue, string? source, string? expected) =>
        Assert.Equal(expected, VolumeMatcher.NameOf(new MetadataRecord { Source = SourceIds.ComicVine, VolumeName = volume, IssueName = issue, SourceName = source }));
}
