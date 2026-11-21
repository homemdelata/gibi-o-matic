using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GibiOMatic.Metadata.Http;
using GibiOMatic.Metadata.Identification;
using GibiOMatic.Metadata.Sources;

namespace GibiOMatic.Metadata.Assisted;

/// <summary>
/// The app's MCP server (stdio, one JSON-RPC message per line) that gives Claude ComicVine (SPEC §8.4,
/// step 3): the app's own client, cache and rate limiter, so the key never reaches Claude and
/// ComicVine is never hammered. Written by hand: three tools need no SDK (decision of 2026-11-21).
/// </summary>
/// <param name="folder">The run's folder: covers are saved there for Claude to look at, and every call is logged.</param>
/// <param name="cover">Downloads a cover image and returns it as a JPEG to save, or null.</param>
/// <param name="maxCalls">Calls answered before the server says the budget is spent.</param>
public sealed class ComicVineMcpServer(IComicVineApi comicVine, string folder, Func<string, CancellationToken, Task<byte[]?>> cover, int maxCalls = 60)
{
    public const string ServerName = "gibiomatic";
    public const string LogFile = "comicvine.log";

    /// <summary>A volume's issues listed at most (a long series is cut, oldest first).</summary>
    private const int MaxIssues = 300;

    private int _calls;

    private static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken ct)
    {
        while (await input.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (await HandleAsync(line, ct) is { } reply)
            {
                await output.WriteLineAsync(reply.AsMemory(), ct);
                await output.FlushAsync(ct);
            }
        }
    }

    /// <summary>The reply to one message, or null for a notification.</summary>
    public async Task<string?> HandleAsync(string message, CancellationToken ct)
    {
        JsonObject request;
        try
        {
            request = JsonNode.Parse(message) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return Error(null, -32700, "Parse error");
        }

        var id = request["id"]?.DeepClone();
        var method = request["method"]?.ToString();
        if (id is null)
        {
            return null; // notifications/initialized and the like
        }

        switch (method)
        {
            case "initialize":
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = request["params"]?["protocolVersion"]?.ToString() ?? "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = "1" },
                });
            case "tools/list":
                return Result(id, new JsonObject { ["tools"] = Tools() });
            case "tools/call":
                var name = request["params"]?["name"]?.ToString() ?? string.Empty;
                var args = request["params"]?["arguments"] as JsonObject ?? [];
                return Result(id, await CallAsync(name, args, ct));
            case "ping":
                return Result(id, new JsonObject());
            default:
                return Error(id, -32601, "Method not found");
        }
    }

    private async Task<JsonObject> CallAsync(string name, JsonObject args, CancellationToken ct)
    {
        await LogAsync($"{name} {args.ToJsonString()}", ct);
        if (++_calls > maxCalls)
        {
            return Text("error: the budget of ComicVine calls for this run is spent; answer with what you have.", isError: true);
        }

        try
        {
            object? result = name switch
            {
                "comicvine_search" => await SearchAsync(args, ct),
                "comicvine_volume" => await VolumeAsync(args, ct),
                "comicvine_issue" => await IssueAsync(args, ct),
                _ => null,
            };
            return result is null ? Text("error: unknown tool " + name, isError: true) : Text(JsonSerializer.Serialize(result, Compact));
        }
        catch (MetadataSourceException ex)
        {
            return Text("error: ComicVine " + ex.Error, isError: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or FormatException or InvalidOperationException)
        {
            return Text("error: " + ex.GetType().Name, isError: true);
        }
    }

    private async Task<object> SearchAsync(JsonObject args, CancellationToken ct)
    {
        var query = args["query"]?.ToString() ?? throw new FormatException("query");
        var volumes = await comicVine.SearchVolumesAsync(query, ct, 20);
        return volumes.Select(v => new
        {
            volumeId = v.Id, name = v.Name, startYear = v.StartYear, publisher = v.Publisher, issues = v.IssueCount,
            url = $"https://comicvine.gamespot.com/volume/4050-{v.Id}/",
        });
    }

    private async Task<object> VolumeAsync(JsonObject args, CancellationToken ct)
    {
        var id = IntOf(args["volumeId"]);
        var volume = await comicVine.GetVolumeAsync(id, ct) ?? throw new InvalidOperationException("No such volume.");
        var issues = await comicVine.GetVolumeIssuesAsync(volume.Id, Math.Min(volume.IssueCount, MaxIssues), ct);
        return new
        {
            volumeId = volume.Id, name = volume.Name, startYear = volume.StartYear, publisher = volume.Publisher, issueCount = volume.IssueCount,
            url = $"https://comicvine.gamespot.com/volume/4050-{volume.Id}/",
            issues = issues.Take(MaxIssues).Select(i => new
            {
                issueId = i.Id, number = i.IssueNumber, name = i.Name, date = Proposal.DateText(i.Date),
                url = $"https://comicvine.gamespot.com/issue/4000-{i.Id}/",
            }),
            truncated = volume.IssueCount > MaxIssues,
        };
    }

    private async Task<object> IssueAsync(JsonObject args, CancellationToken ct)
    {
        var id = IntOf(args["issueId"]);
        var issue = await comicVine.GetIssueAsync(id, ct) ?? throw new InvalidOperationException("No such issue.");
        string? coverFile = null;
        if ((issue.CoverFullUrl ?? issue.CoverUrl) is { } url && await cover(url, ct) is { } jpeg)
        {
            coverFile = $"comicvine-cover-{issue.Id}.jpg";
            await File.WriteAllBytesAsync(Path.Combine(folder, coverFile), jpeg, ct);
        }

        return new
        {
            issueId = issue.Id, volume = issue.VolumeName, volumeId = issue.VolumeId, number = issue.IssueNumber, name = issue.Name,
            date = Proposal.DateText(issue.Date),
            description = issue.Description is { Length: > 900 } d ? d[..900] : issue.Description,
            credits = issue.Credits.Take(12).Select(c => $"{c.Name} ({c.Role.ToString().ToLowerInvariant()})"),
            coverFile,
            url = issue.SiteUrl ?? $"https://comicvine.gamespot.com/issue/4000-{issue.Id}/",
        };
    }

    private static int IntOf(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<int>(out var n) => n,
        JsonValue v when v.TryGetValue<long>(out var l) && l is > 0 and <= int.MaxValue => (int)l,
        JsonValue v when v.TryGetValue<double>(out var d) && d is > 0 and <= int.MaxValue => (int)d,
        JsonValue v when v.TryGetValue<string>(out var s) && BookIdentifier.ParseComicVineLink(s) is { } link => link.Id,
        JsonValue v when v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        _ => throw new FormatException("id"),
    };

    private static JsonArray Tools()
    {
        static JsonObject Tool(string name, string description, string property, string type, string about) => new()
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { [property] = new JsonObject { ["type"] = type, ["description"] = about } },
                ["required"] = new JsonArray(property),
            },
        };

        return
        [
            Tool("comicvine_search", "Search ComicVine volumes (series, and collected editions that are volumes of their own) by name.",
                "query", "string", "Words of the series or edition name"),
            Tool("comicvine_volume", "A ComicVine volume with its issues (a collected edition is often an issue inside a volume, named 'Vol. N').",
                "volumeId", "integer", "The number after 4050-"),
            Tool("comicvine_issue", "Details of a ComicVine issue: name, date, description, credits; its cover is saved to coverFile in the current folder (Read it to compare).",
                "issueId", "integer", "The number after 4000-"),
        ];
    }

    private static JsonObject Text(string text, bool isError = false)
    {
        var result = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
        if (isError)
        {
            result["isError"] = true;
        }

        return result;
    }

    private static string Result(JsonNode id, JsonObject result) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();

    private static string Error(JsonNode? id, int code, string message) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString();

    private async Task LogAsync(string line, CancellationToken ct)
    {
        try
        {
            await File.AppendAllTextAsync(Path.Combine(folder, LogFile), line + Environment.NewLine, ct);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The <c>--mcp-config</c> of a run: this server, started as the app with <paramref name="args"/>.</summary>
    public static string Config(string executable, IReadOnlyList<string> args) =>
        new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [ServerName] = new JsonObject
                {
                    ["command"] = executable,
                    ["args"] = new JsonArray(args.Select(a => (JsonNode)JsonValue.Create(a)).ToArray()),
                },
            },
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
