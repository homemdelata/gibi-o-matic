using System.Net;
using GibiOMatic.Core.Domain;
using GibiOMatic.Metadata.Http;
using GibiOMatic.Metadata.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace GibiOMatic.Metadata.Tests;

/// <summary>Answers requests from a script and records what was asked.</summary>
public sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
}

internal sealed class FixedKey(string? key) : IComicVineKeyProvider
{
    public Task<string?> GetKeyAsync(CancellationToken ct) => Task.FromResult(key);
}

public sealed class SourceClientTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly RetryPolicy FastRetry = new(3, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(20));

    private SourceClient Client(FakeHttp http, RetryPolicy? retry = null) =>
        new("test", new HttpClient(http), new ResponseCache(_temp.Path), new RateLimiter(TimeSpan.Zero, 1000), retry ?? FastRetry,
            TimeSpan.FromDays(1), NullLogger.Instance);

    [Fact]
    public async Task Responses_are_cached_without_the_secret_and_sent_with_our_user_agent()
    {
        var http = new FakeHttp(_ => FakeHttp.Json("""{"ok":true}"""));
        var client = Client(http);

        var first = await client.GetAsync("search", "https://api.example/search?q=saga", "api_key=SECRET123", Ct);
        var second = await client.GetAsync("search", "https://api.example/search?q=saga", "api_key=SECRET123", Ct);

        Assert.Equal(first, second);
        var request = Assert.Single(http.Requests);
        Assert.Contains("api_key=SECRET123", request.RequestUri!.Query);
        Assert.Contains("GibiOMatic", request.Headers.UserAgent.ToString());
        var cached = Directory.GetFiles(_temp.Path, "*.json", SearchOption.AllDirectories);
        Assert.DoesNotContain("SECRET123", await File.ReadAllTextAsync(Assert.Single(cached), Ct));
    }

    [Fact]
    public async Task Server_errors_are_retried_with_backoff()
    {
        var calls = 0;
        var http = new FakeHttp(_ => ++calls < 3 ? FakeHttp.Json("oops", HttpStatusCode.BadGateway) : FakeHttp.Json("""{"ok":1}"""));

        var body = await Client(http).GetAsync("r", "https://api.example/x", null, Ct);

        Assert.Equal("""{"ok":1}""", body);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Rate_limits_wait_and_retry_and_quota_errors_are_not_cached()
    {
        var calls = 0;
        var http = new FakeHttp(_ => ++calls switch
        {
            1 => FakeHttp.Json("slow down", HttpStatusCode.TooManyRequests),
            2 => FakeHttp.Json("""{"status_code":107}"""),
            _ => FakeHttp.Json("""{"status_code":1}"""),
        });

        var body = await Client(http).GetAsync("r", "https://api.example/y", null, Ct,
            isRateLimited: b => b.Contains("107"), isCacheable: b => b.Contains("\"status_code\":1"));

        Assert.Equal("""{"status_code":1}""", body);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Giving_up_raises_a_source_error()
    {
        var http = new FakeHttp(_ => FakeHttp.Json("down", HttpStatusCode.ServiceUnavailable));

        var ex = await Assert.ThrowsAsync<MetadataSourceException>(() => Client(http).GetAsync("r", "https://api.example/z", null, Ct));

        Assert.Equal(MetadataSourceError.Unavailable, ex.Error);
        Assert.Equal(3, http.Requests.Count);
    }

    [Fact]
    public async Task The_limiter_spaces_requests_and_respects_the_hourly_budget()
    {
        var limiter = new RateLimiter(TimeSpan.FromMilliseconds(60), perHour: 2);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await limiter.WaitAsync("search", Ct);
        await limiter.WaitAsync("search", Ct);
        Assert.True(watch.ElapsedMilliseconds >= 50, $"took {watch.ElapsedMilliseconds} ms");

        // A third "search" this hour has to wait; another resource does not.
        await limiter.WaitAsync("issue", Ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.WaitAsync("search", cts.Token));
    }
}

public sealed class SourceParsingTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SourceClient Client(FakeHttp http) =>
        new("t", new HttpClient(http), new ResponseCache(_temp.Path), new RateLimiter(TimeSpan.Zero, 1000),
            new RetryPolicy(1, TimeSpan.Zero, TimeSpan.Zero), TimeSpan.FromDays(1), NullLogger.Instance);

    [Fact]
    public async Task ComicVine_issue_credits_keep_only_creative_roles()
    {
        var http = new FakeHttp(_ => FakeHttp.Json("""
            {"status_code":1,"error":"OK","results":{"id":937068,"name":"Volume 11","issue_number":"1",
             "cover_date":"2022-05-01","store_date":"2022-06-01","volume":{"id":144057,"name":"Deadly Class: A Fond Farewell Part One"},
             "person_credits":[{"name":"Lee Loughridge","role":"colorist"},{"name":"Rick Remender","role":"writer"},
               {"name":"Rus Wooton","role":"letterer, designer"},{"name":"Wes Craig","role":"artist"},{"name":"Some Editor","role":"editor"}],
             "description":"<p>Trade paperback collecting <a href=\"/x\">Deadly Class</a> #49-52.</p>","site_detail_url":"https://comicvine/x"}}
            """));
        var cv = new ComicVineClient(Client(http), new FixedKey("k"));

        var issue = await cv.GetIssueAsync(937068, Ct);

        Assert.NotNull(issue);
        Assert.Equal(new PartialDate(2022, 6, 1), issue.Date);
        Assert.Equal("Trade paperback collecting Deadly Class #49-52.", issue.Description);
        Assert.Equal(
            [new Credit(CreditRole.Writer, "Rick Remender"), new Credit(CreditRole.Artist, "Wes Craig"),
             new Credit(CreditRole.Colorist, "Lee Loughridge"), new Credit(CreditRole.Letterer, "Rus Wooton")],
            issue.Credits);
    }

    [Fact]
    public async Task ComicVine_invalid_key_is_reported()
    {
        var http = new FakeHttp(_ => FakeHttp.Json("""{"status_code":100,"error":"Invalid API Key","results":[]}"""));
        var cv = new ComicVineClient(Client(http), new FixedKey("bad"));

        var ex = await Assert.ThrowsAsync<MetadataSourceException>(() => cv.SearchVolumesAsync("Saga", Ct));

        Assert.Equal(MetadataSourceError.InvalidKey, ex.Error);
        Assert.False(await cv.TestKeyAsync(Ct));
    }

    [Fact]
    public async Task ComicVine_without_a_key_does_not_call_the_api()
    {
        var http = new FakeHttp(_ => throw new InvalidOperationException("no call expected"));
        var cv = new ComicVineClient(Client(http), new FixedKey(null));

        Assert.False(await cv.HasKeyAsync(Ct));
        await Assert.ThrowsAsync<MetadataSourceException>(() => cv.SearchVolumesAsync("Saga", Ct));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Open_library_book_by_isbn()
    {
        var http = new FakeHttp(_ => FakeHttp.Json("""
            {"ISBN:9781534321236":{"title":"Deadly Class, Vol. 11","subtitle":"A Fond Farewell, Part One",
              "authors":[{"name":"Rick Remender"},{"name":"Wes Craig"}],"number_of_pages":128,
              "publishers":[{"name":"Image Comics"}],"publish_date":"2022","url":"http://openlibrary.org/books/OL1M"}}
            """));

        var record = await new OpenLibraryClient(Client(http)).LookupIsbnAsync("9781534321236", Ct);

        Assert.NotNull(record);
        Assert.Equal("Deadly Class, Vol. 11: A Fond Farewell, Part One", record.Title);
        Assert.Equal(("Image Comics", 128, new PartialDate(2022)), (record.Publisher, record.PageCount, record.Date));
        Assert.Null(await new OpenLibraryClient(Client(new FakeHttp(_ => FakeHttp.Json("{}")))).LookupIsbnAsync("9781607066019", Ct));
    }

    [Theory]
    [InlineData("2022-06-01", 2022, 6, 1)]
    [InlineData("2022", 2022, null, null)]
    [InlineData("June 1, 2022", 2022, 6, 1)]
    [InlineData("June 2022", 2022, 6, null)]
    [InlineData("Jun 2022", 2022, 6, null)]
    public void Dates_in_api_formats(string text, int year, int? month, int? day) =>
        Assert.Equal(new PartialDate(year, month, day), Json.Date(text));
}
