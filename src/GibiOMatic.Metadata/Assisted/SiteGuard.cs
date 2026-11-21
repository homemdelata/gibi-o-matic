using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GibiOMatic.Metadata.Assisted;

/// <summary>
/// Keeps Claude's web use on the user's list of sites (SPEC §8.4, step 4). Claude Code runs the
/// app as a <c>PreToolUse</c> hook before every tool call: a web search gets <c>allowed_domains</c>
/// forced to the list, a page fetch outside the list is denied, and every call is logged.
/// </summary>
public static partial class SiteGuard
{
    public const string SitesFile = "sites.txt";
    public const string LogFile = "tools.log";

    /// <summary>
    /// Domains that make every web search fail when they are in <c>allowed_domains</c> ("not
    /// accessible to our user agent"), found in the spike of 2026-11-21.
    /// </summary>
    public static IReadOnlyList<string> RejectedBySearch { get; } = ["humblebundle.com"];

    /// <summary>
    /// The list as typed, cleaned: "https://www.Amazon.com/books" → "amazon.com"; duplicates, invalid
    /// entries and the domains the search rejects are dropped (returned in <paramref name="dropped"/>).
    /// </summary>
    public static IReadOnlyList<string> Clean(IEnumerable<string> typed, out IReadOnlyList<string> dropped)
    {
        var kept = new List<string>();
        var removed = new List<string>();
        foreach (var raw in typed)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var text = raw.Trim().ToLowerInvariant();
            text = Regex.Replace(text, "^[a-z]+://", string.Empty);
            text = text.Split('/', '?', '#')[0].Trim('.');
            if (text.StartsWith("www.", StringComparison.Ordinal))
            {
                text = text[4..];
            }

            if (!Domain().IsMatch(text) || RejectedBySearch.Any(r => text == r || text.EndsWith("." + r, StringComparison.Ordinal)))
            {
                removed.Add(raw.Trim());
                continue;
            }

            if (!kept.Contains(text))
            {
                kept.Add(text);
            }
        }

        dropped = removed;
        return kept;
    }

    /// <summary>A host is allowed when it is a site of the list or a subdomain of one.</summary>
    public static bool IsAllowed(string? host, IReadOnlyList<string> sites)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        host = host.ToLowerInvariant();
        return sites.Any(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal));
    }

    /// <summary>
    /// The hook's answer for one tool call (<paramref name="input"/> is what Claude Code writes to the
    /// hook's standard input): the JSON to print (null to say nothing) and the line to log.
    /// </summary>
    public static (string? Output, string Log) Decide(string input, IReadOnlyList<string> sites)
    {
        JsonObject call;
        try
        {
            call = JsonNode.Parse(input) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return (Deny("Unreadable tool call."), "unreadable call");
        }

        var tool = call["tool_name"]?.GetValue<string>() ?? string.Empty;
        var toolInput = call["tool_input"] as JsonObject ?? [];
        switch (tool)
        {
            case "WebSearch":
            {
                var updated = (JsonObject)toolInput.DeepClone();
                updated["allowed_domains"] = new JsonArray(sites.Select(s => (JsonNode)JsonValue.Create(s)).ToArray());
                updated.Remove("blocked_domains");
                var output = new JsonObject
                {
                    ["hookSpecificOutput"] = new JsonObject
                    {
                        ["hookEventName"] = "PreToolUse",
                        ["permissionDecision"] = "allow",
                        ["updatedInput"] = updated,
                    },
                };
                return (output.ToJsonString(), "search: " + toolInput["query"]?.ToString());
            }

            case "WebFetch":
            {
                var url = toolInput["url"]?.ToString();
                var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
                return IsAllowed(host, sites)
                    ? (Allow(), "fetch: " + url)
                    : (Deny("Only these sites are allowed: " + string.Join(", ", sites)), "BLOCKED fetch: " + url);
            }

            default:
                return (null, "tool: " + tool);
        }
    }

    /// <summary>
    /// The <c>--settings</c> file of a run: the hook (the app itself, started with
    /// <paramref name="hookCommand"/>) and the permissions for the tools of the run.
    /// </summary>
    public static string Settings(string hookCommand, bool web)
    {
        var allow = new JsonArray("mcp__gibiomatic", "Read");
        if (web)
        {
            allow.Add("WebSearch");
            allow.Add("WebFetch");
        }

        var settings = new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["PreToolUse"] = new JsonArray(new JsonObject
                {
                    ["matcher"] = "WebSearch|WebFetch|Read|mcp__gibiomatic__.*",
                    ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = hookCommand }),
                }),
            },
            ["permissions"] = new JsonObject { ["allow"] = allow },
        };
        return settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// A command line for Claude Code's hook, which runs it through a shell: forward slashes and
    /// double quotes work in both the Windows shells and sh.
    /// </summary>
    public static string Command(string executable, IEnumerable<string> args) =>
        string.Join(' ', new[] { executable }.Concat(args).Select(a => "\"" + a.Replace('\\', '/') + "\""));

    private static string Allow() => new JsonObject
    {
        ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PreToolUse", ["permissionDecision"] = "allow" },
    }.ToJsonString();

    private static string Deny(string reason) => new JsonObject
    {
        ["hookSpecificOutput"] = new JsonObject
        {
            ["hookEventName"] = "PreToolUse",
            ["permissionDecision"] = "deny",
            ["permissionDecisionReason"] = reason,
        },
    }.ToJsonString();

    [GeneratedRegex(@"^(?=.{3,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")]
    private static partial Regex Domain();
}
