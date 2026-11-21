using System.Text.Json;
using System.Text.Json.Nodes;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Settings;
using GibiOMatic.Metadata.Assisted;
using GibiOMatic.Metadata.Identification;
using GibiOMatic.Metadata.Sources;

namespace GibiOMatic.Metadata.Tests;

/// <summary>The command line of Claude Code, as the app runs and reads it (SPEC §8.4), with recorded outputs.</summary>
public sealed class ClaudeCodeCliTests
{
    [Fact]
    public void The_answer_is_the_structured_output_of_the_last_json_line()
    {
        var stdout = """
            warning: something on stdout first
            {"type":"result","subtype":"success","is_error":false,"num_turns":17,"total_cost_usd":0.12,"structured_output":{"identified":true,"confidence":0.9,"evidence":["cover"]}}
            """;

        var result = ClaudeCodeCli.ParseOutput(stdout, TimeSpan.FromSeconds(23));

        Assert.True(result.Succeeded);
        Assert.Equal(17, result.Turns);
        Assert.Equal(0.9, JsonDocument.Parse(result.Output!).RootElement.GetProperty("confidence").GetDouble());
    }

    [Theory]
    [InlineData("""{"type":"result","subtype":"error_max_budget_usd","is_error":false,"num_turns":30}""", "error_max_budget_usd")]
    [InlineData("""{"type":"result","subtype":"success","is_error":true,"result":"Failed to authenticate"}""", "Failed to authenticate")]
    [InlineData("""{"type":"result","subtype":"success","is_error":false,"num_turns":3}""", "no-answer")]
    [InlineData("", "no-output")]
    [InlineData("not json at all", "unreadable-output")]
    public void Runs_without_an_answer_say_why(string stdout, string error)
    {
        var result = ClaudeCodeCli.ParseOutput(stdout, TimeSpan.Zero);

        Assert.False(result.Succeeded);
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void Login_and_version_are_read_from_the_cli()
    {
        Assert.Equal((true, "claude.ai"), ClaudeCodeCli.ParseAuthStatus("""{"loggedIn":true,"authMethod":"claude.ai","subscriptionType":"max"}"""));
        Assert.Equal((false, null), ClaudeCodeCli.ParseAuthStatus("""{"loggedIn":false}"""));
        Assert.Equal((false, null), ClaudeCodeCli.ParseAuthStatus("Not logged in"));
        Assert.Equal("2.1.316", ClaudeCodeCli.ParseVersion("2.1.316 (Claude Code)\n"));
    }

    [Fact]
    public void A_run_is_restricted_to_the_tools_the_mcp_server_and_the_guard_of_the_app()
    {
        var run = new ClaudeRun("C:/run", "haiku", "low", AssistedPrompts.BookRequest, AssistedPrompts.Quick, AssistedPrompts.BookSchema,
            ["Read", "WebSearch", "WebFetch"], "C:/run/mcp.json", "C:/run/settings.json", TimeSpan.FromMinutes(5), 3m);

        var args = ClaudeCodeCli.Arguments(run);

        string After(string flag) => args[args.ToList().IndexOf(flag) + 1];
        Assert.Equal("-p", args[0]);
        Assert.Equal("haiku", After("--model"));
        Assert.Equal("low", After("--effort"));
        Assert.Equal("json", After("--output-format"));
        Assert.Equal(AssistedPrompts.BookSchema, After("--json-schema"));
        Assert.Equal("Read,WebSearch,WebFetch", After("--tools"));
        Assert.Equal("C:/run/mcp.json", After("--mcp-config"));
        Assert.Equal("C:/run/settings.json", After("--settings"));
        Assert.Equal("dontAsk", After("--permission-mode"));
        Assert.Equal("3", After("--max-budget-usd"));
        Assert.Contains("--restricted", args);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Contains("--no-session-persistence", args);
    }

    [Theory]
    [InlineData(AssistedLevel.Quick)]
    [InlineData(AssistedLevel.Deep)]
    [InlineData(AssistedLevel.Group)]
    public void The_schemas_are_valid_json(AssistedLevel level)
    {
        var schema = JsonNode.Parse(AssistedPrompts.Schema(level))!;
        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.Contains("ComicVine", AssistedPrompts.SystemPrompt(level), StringComparison.Ordinal);
    }
}

/// <summary>The site guard hook (SPEC §8.4, step 4).</summary>
public sealed class SiteGuardTests
{
    private static readonly IReadOnlyList<string> Sites = ["amazon.com", "imagecomics.com"];

    [Fact]
    public void The_list_is_cleaned_and_the_domains_that_break_the_search_are_dropped()
    {
        var kept = SiteGuard.Clean(["https://www.Amazon.com/books", "imagecomics.com", "amazon.com", "", "humblebundle.com", "not a site", "store.humblebundle.com"],
            out var dropped);

        Assert.Equal(["amazon.com", "imagecomics.com"], kept);
        Assert.Equal(["humblebundle.com", "not a site", "store.humblebundle.com"], dropped);
    }

    [Theory]
    [InlineData("amazon.com", true)]
    [InlineData("www.amazon.com", true)]
    [InlineData("AMAZON.com", true)]
    [InlineData("notamazon.com", false)]
    [InlineData("amazon.com.evil.net", false)]
    [InlineData(null, false)]
    public void A_host_is_allowed_when_it_is_a_site_of_the_list_or_a_subdomain(string? host, bool allowed) =>
        Assert.Equal(allowed, SiteGuard.IsAllowed(host, Sites));

    [Fact]
    public void A_web_search_gets_the_list_forced_on_it()
    {
        var (output, log) = SiteGuard.Decide("""{"tool_name":"WebSearch","tool_input":{"query":"snotgirl vol 4","blocked_domains":["x.com"]}}""", Sites);

        var decision = JsonNode.Parse(output!)!["hookSpecificOutput"]!;
        Assert.Equal("allow", decision["permissionDecision"]!.GetValue<string>());
        Assert.Equal(["amazon.com", "imagecomics.com"], decision["updatedInput"]!["allowed_domains"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Null(decision["updatedInput"]!["blocked_domains"]);
        Assert.Equal("snotgirl vol 4", decision["updatedInput"]!["query"]!.GetValue<string>());
        Assert.Equal("search: snotgirl vol 4", log);
    }

    [Theory]
    [InlineData("https://imagecomics.com/comics/releases/snotgirl-vol-4", "allow")]
    [InlineData("https://comicvine.gamespot.com/api/issue/4000-1/", "deny")]
    [InlineData("not a url", "deny")]
    public void A_page_fetch_outside_the_list_is_denied(string url, string decision)
    {
        var (output, log) = SiteGuard.Decide(JsonSerializer.Serialize(new { tool_name = "WebFetch", tool_input = new { url } }), Sites);

        Assert.Equal(decision, JsonNode.Parse(output!)!["hookSpecificOutput"]!["permissionDecision"]!.GetValue<string>());
        Assert.Equal(decision == "deny", log.StartsWith("BLOCKED", StringComparison.Ordinal));
    }

    [Fact]
    public void Other_tools_are_only_logged()
    {
        var (output, log) = SiteGuard.Decide("""{"tool_name":"mcp__gibiomatic__comicvine_search","tool_input":{}}""", Sites);

        Assert.Null(output);
        Assert.Equal("tool: mcp__gibiomatic__comicvine_search", log);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_settings_hold_the_hook_and_allow_only_the_tools_of_the_run(bool web)
    {
        var command = SiteGuard.Command(@"C:\Program Files\Gibi-o-matic\GibiOMatic.exe", ["--claude-hook", @"C:\runs\a b"]);
        var settings = JsonNode.Parse(SiteGuard.Settings(command, web))!;

        Assert.Equal("\"C:/Program Files/Gibi-o-matic/GibiOMatic.exe\" \"--claude-hook\" \"C:/runs/a b\"", command);
        Assert.Equal(command, settings["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        var allow = settings["permissions"]!["allow"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("mcp__gibiomatic", allow);
        Assert.Equal(web, allow.Contains("WebSearch"));
    }
}

/// <summary>The app's ComicVine MCP server (SPEC §8.4, step 3), over the fake ComicVine.</summary>
public sealed class ComicVineMcpServerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ComicVineMcpServer Server(FakeComicVine cv, int maxCalls = 60) =>
        new(cv, _temp.Path, (_, _) => Task.FromResult<byte[]?>([1, 2, 3]), maxCalls);

    private static JsonNode Reply(string? json) => JsonNode.Parse(json!)!;

    private static JsonNode Content(string? json) => JsonNode.Parse(Reply(json)["result"]!["content"]![0]!["text"]!.GetValue<string>())!;

    [Fact]
    public async Task It_answers_the_handshake_and_lists_three_tools()
    {
        var server = Server(new FakeComicVine());

        var init = Reply(await server.HandleAsync("""{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""", Ct));
        var notification = await server.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", Ct);
        var tools = Reply(await server.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Ct));

        Assert.Equal("2025-06-18", init["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Null(notification);
        Assert.Equal(["comicvine_search", "comicvine_volume", "comicvine_issue"],
            tools["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Search_volume_and_issue_come_from_the_apps_client_and_the_cover_is_saved()
    {
        var cv = FakeComicVine.DeadlyClass();
        var server = Server(cv);

        var search = Content(await server.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"comicvine_search","arguments":{"query":"Deadly Class"}}}""", Ct));
        var volume = Content(await server.HandleAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"comicvine_volume","arguments":{"volumeId":144057}}}""", Ct));
        cv.Details[937068] = cv.Details[937068] with { CoverFullUrl = "https://comicvine.gamespot.com/a/uploads/original/1.jpg" };
        var issue = Content(await server.HandleAsync("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"comicvine_issue","arguments":{"issueId":"https://comicvine.gamespot.com/x/4000-937068/"}}}""", Ct));

        Assert.Contains(search.AsArray(), v => v!["volumeId"]!.GetValue<int>() == 144057);
        Assert.Equal("Volume 11", volume["issues"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("comicvine-cover-937068.jpg", issue["coverFile"]!.GetValue<string>());
        Assert.True(File.Exists(_temp.Combine("comicvine-cover-937068.jpg")));
        Assert.Equal(3, File.ReadAllLines(_temp.Combine(ComicVineMcpServer.LogFile)).Length);
    }

    [Fact]
    public async Task The_budget_of_calls_is_enforced()
    {
        var server = Server(FakeComicVine.DeadlyClass(), maxCalls: 1);
        const string call = """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"comicvine_search","arguments":{"query":"x"}}}""";

        await server.HandleAsync(call, Ct);
        var second = Reply(await server.HandleAsync(call, Ct));

        Assert.True(second["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Bad_input_is_an_error_not_a_crash()
    {
        var server = Server(new FakeComicVine());

        var missing = Reply(await server.HandleAsync("""{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"comicvine_issue","arguments":{"issueId":"abc"}}}""", Ct));
        var unknown = Reply(await server.HandleAsync("""{"jsonrpc":"2.0","id":7,"method":"nope"}""", Ct));
        var garbage = Reply(await server.HandleAsync("{", Ct));

        Assert.True(missing["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal(-32601, unknown["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32700, garbage["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void The_config_starts_the_app_with_the_run_folder()
    {
        var config = JsonNode.Parse(ComicVineMcpServer.Config("C:/app/GibiOMatic.exe", ["--claude-mcp", "C:/run"]))!;

        var server = config["mcpServers"]![ComicVineMcpServer.ServerName]!;
        Assert.Equal("C:/app/GibiOMatic.exe", server["command"]!.GetValue<string>());
        Assert.Equal(["--claude-mcp", "C:/run"], server["args"]!.AsArray().Select(a => a!.GetValue<string>()));
    }
}

/// <summary>Claude's answers turned into candidates, merged and approved (SPEC §8.4, step 5 and "Approval").</summary>
public sealed class AssistedRecordsTests
{
    private static MetadataCandidate CvCandidate(string id, double confidence = 0.7, AssistedEvidence? assisted = null) =>
        new(new MetadataRecord
        {
            Source = SourceIds.ComicVine,
            Title = "Saga, Vol. 1",
            SeriesName = "Saga",
            Number = "1",
            Kind = BookKind.Tpb,
            Publisher = "Image",
            ComicVineId = id,
            ComicVineVolumeId = 1,
        }, confidence, [MatchReason.Title]) { Assisted = assisted };

    private static AssistedEvidence Evidence(AssistedLevel level = AssistedLevel.Quick, bool? cover = true, bool missing = false) => new()
    {
        Level = level,
        Model = "haiku",
        CoverMatches = cover,
        EditionMissing = missing,
    };

    [Fact]
    public void An_edition_missing_from_comicvine_is_built_from_the_answer_with_its_series()
    {
        var answer = new BookAnswer
        {
            Identified = true,
            Title = "Snotgirl, Vol. 4: Bitch Factor",
            Series = "Snotgirl",
            Number = "4",
            Kind = "tpb",
            Publisher = "Image Comics",
            ReleaseDate = "2022-03-16",
            Isbn = "978-1-5343-2991-1",
            PageCount = 128,
            Credits = [new AnswerCredit("Writer", "Bryan Lee O'Malley"), new AnswerCredit("Artist, Colorist", "Leslie Hung"), new AnswerCredit("Book design", "Rob Liefeld")],
            Sources = new Dictionary<string, string> { ["isbn"] = "indicia page-004" },
            Confidence = 0.92,
        };

        var candidate = AssistedRecords.FromAnswer(answer, AssistedRecords.Evidence(answer, AssistedLevel.Deep, "sonnet", null, editionMissing: true),
            new CvVolume(98765, "Snotgirl", 2016, "Image", 20));

        Assert.Equal(SourceIds.Claude, candidate.Record.Source);
        Assert.Equal("Snotgirl", candidate.Record.SeriesName);
        Assert.Equal(98765, candidate.Record.ComicVineVolumeId);
        Assert.Null(candidate.Record.ComicVineId);
        Assert.Equal("9781534329911", candidate.Record.Isbn);
        Assert.Equal(new PartialDate(2022, 3, 16), candidate.Record.Date);
        Assert.Equal(BookKind.Tpb, candidate.Record.Kind);
        Assert.Equal([new Credit(CreditRole.Writer, "Bryan Lee O'Malley"), new Credit(CreditRole.Artist, "Leslie Hung"), new Credit(CreditRole.Colorist, "Leslie Hung")],
            candidate.Record.Credits);
        Assert.Equal(0.92, candidate.Confidence);
        Assert.Contains(ProposalField.Writers, candidate.Assisted!.Unticked);
        Assert.Equal("indicia page-004", candidate.Assisted.FieldSources["isbn"]);
    }

    [Fact]
    public void Claudes_candidates_come_first_and_replace_its_older_answers_and_the_algorithms_all_stay()
    {
        var algorithm = new IdentificationResult(BookQuery.From(new BookMetadata { Id = "b", Title = "Saga 1" }),
            [CvCandidate("4000-1", 0.8), CvCandidate("4000-2", 0.5), CvCandidate("4000-9", 0.9, Evidence())], []);
        var differentCover = CvCandidate("4000-3", 0.99, Evidence(cover: false));
        var deep = CvCandidate("4000-2", 0.9, Evidence(AssistedLevel.Deep));

        var merged = AssistedRecords.Merge(algorithm, [differentCover, deep]);

        Assert.Equal(["4000-2", "4000-3", "4000-1", "4000-2"], merged.Candidates.Select(c => c.Record.ComicVineId));
        Assert.NotNull(merged.Candidates[0].Assisted);
    }

    [Theory]
    [InlineData(true, false, 0.9, false)]
    [InlineData(false, false, 0.9, true)]
    [InlineData(true, true, 0.99, true)]
    [InlineData(true, false, 0.5, true)]
    public void A_quick_answer_asks_for_a_deep_one_when_it_is_weak(bool cover, bool missing, double confidence, bool deep) =>
        Assert.Equal(deep, AssistedRecords.NeedsDeep(CvCandidate("4000-1", confidence, Evidence(cover: cover, missing: missing)), 0.8));

    private static readonly ClaudeSettings On = new() { Enabled = true, AutoApprove = true };

    private static readonly BookMetadata Book = new() { Id = "b", Title = "saga_v1" };

    private static IdentificationResult Algorithm(string id) =>
        new(BookQuery.From(Book), [CvCandidate(id, 0.6)], []);

    [Fact]
    public void It_approves_only_when_every_condition_holds()
    {
        var candidate = CvCandidate("4000-1", 0.97, Evidence());

        Assert.True(AssistedApproval.CanApprove(Book, candidate, Algorithm("4000-1"), [], On, new MetadataSettings(), out var reason), reason);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("always-review")]
    [InlineData("low-confidence")]
    [InlineData("no-edition-record")]
    [InlineData("cover")]
    [InlineData("no-agreement")]
    [InlineData("conflict")]
    public void Each_condition_alone_sends_the_book_to_the_review(string failing)
    {
        var claude = failing == "off" ? On with { AutoApprove = false } : On;
        var metadata = new MetadataSettings { AlwaysReview = failing == "always-review" };
        var candidate = CvCandidate(failing == "no-edition-record" ? "4050-1" : "4000-1", failing == "low-confidence" ? 0.9 : 0.97,
            Evidence(cover: failing == "cover" ? null : true));
        var book = failing == "conflict" ? Book with { Title = "Something Else Entirely", Series = new SeriesInfo("Other", "7") } : Book;
        var algorithm = Algorithm(failing == "no-agreement" ? "4000-2" : "4000-1");

        Assert.False(AssistedApproval.CanApprove(book, candidate, algorithm, [], claude, metadata, out var reason));
        Assert.Equal(failing == "always-review" ? "off" : failing, reason);
    }

    [Fact]
    public void The_quick_and_the_deep_search_agreeing_is_an_independent_agreement()
    {
        var quick = CvCandidate("4000-1", 0.97, Evidence());
        var deep = CvCandidate("4000-1", 0.95, Evidence(AssistedLevel.Deep));

        Assert.True(AssistedApproval.CanApprove(Book, quick, Algorithm("4000-2"), [quick, deep], On, new MetadataSettings(), out _));
        Assert.False(AssistedApproval.CanApprove(Book, quick, Algorithm("4000-2"), [quick], On, new MetadataSettings(), out _));
    }
}
