using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Metadata.Assisted;

/// <summary>Whether Claude Code can be used: installed, and logged in on the command line.</summary>
public sealed record ClaudeStatus(bool Installed, string? Version, bool LoggedIn, string? AuthMethod, string? Path)
{
    public static ClaudeStatus Missing { get; } = new(false, null, false, null, null);

    public bool IsReady => Installed && LoggedIn;
}

/// <summary>One <c>claude -p</c> run (SPEC §8.4, "One run").</summary>
/// <param name="Folder">The working folder: book.json, cover, pages, mcp.json, settings.json.</param>
/// <param name="Tools">Claude Code's own tools allowed ("Read", "WebSearch", "WebFetch").</param>
public sealed record ClaudeRun(
    string Folder,
    string Model,
    string Effort,
    string Request,
    string SystemPrompt,
    string Schema,
    IReadOnlyList<string> Tools,
    string McpConfigPath,
    string SettingsPath,
    TimeSpan Timeout,
    decimal MaxBudgetUsd);

/// <summary>The user's Claude Code (a fake replaces it in tests).</summary>
public interface IClaudeCode
{
    Task<ClaudeStatus> GetStatusAsync(CancellationToken ct);

    Task<ClaudeRunResult> RunAsync(ClaudeRun run, CancellationToken ct);
}

/// <summary>
/// The <c>claude</c> command line as an external process, like the KCC: the app holds no Anthropic
/// key and never calls the Anthropic API; the run uses the user's own login and plan.
/// </summary>
public sealed class ClaudeCodeCli(ILogger<ClaudeCodeCli> logger) : IClaudeCode
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Where the <c>claude</c> executable is, or null. The native installer puts it in ~/.local/bin;
    /// npm puts a <c>claude.cmd</c> in %APPDATA%\npm that only starts
    /// <c>node_modules\@anthropic-ai\claude-code\bin\claude.exe</c>, which is used instead: through
    /// cmd.exe the arguments would break at line breaks and <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c> would
    /// be read as commands (found on 2026-11-21). A launcher that is not an executable is never run.
    /// </summary>
    public static string? Locate()
    {
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"))
            .Append(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (var folder in folders)
        {
            try
            {
                if (Executable(folder) is { } path)
                {
                    return path;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }

        return null;
    }

    /// <summary>The executable <c>claude</c> of one folder: itself, or the one an npm launcher starts.</summary>
    internal static string? Executable(string folder)
    {
        if (!OperatingSystem.IsWindows())
        {
            var path = System.IO.Path.Combine(folder, "claude");
            return File.Exists(path) ? path : null;
        }

        var exe = System.IO.Path.Combine(folder, "claude.exe");
        if (File.Exists(exe))
        {
            return exe;
        }

        var npm = System.IO.Path.Combine(folder, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        return File.Exists(System.IO.Path.Combine(folder, "claude.cmd")) && File.Exists(npm) ? npm : null;
    }

    public async Task<ClaudeStatus> GetStatusAsync(CancellationToken ct)
    {
        if (Locate() is not { } path)
        {
            return ClaudeStatus.Missing;
        }

        var version = await ExecuteAsync(path, ["--version"], null, StatusTimeout, ct);
        if (version.ExitCode != 0)
        {
            return ClaudeStatus.Missing with { Installed = true, Path = path };
        }

        var auth = await ExecuteAsync(path, ["auth", "status"], null, StatusTimeout, ct);
        var (loggedIn, method) = ParseAuthStatus(auth.Output);
        return new ClaudeStatus(true, ParseVersion(version.Output), loggedIn, method, path);
    }

    public async Task<ClaudeRunResult> RunAsync(ClaudeRun run, CancellationToken ct)
    {
        if (Locate() is not { } path)
        {
            return ClaudeRunResult.Failed("not-installed", TimeSpan.Zero);
        }

        var started = Stopwatch.StartNew();
        logger.LogInformation("Claude Code: {Model} ({Effort}) in {Folder}", run.Model, run.Effort, run.Folder);
        var result = await ExecuteAsync(path, Arguments(run), run.Folder, run.Timeout, ct);
        if (result.TimedOut)
        {
            return ClaudeRunResult.Failed("timeout", started.Elapsed);
        }

        var parsed = ParseOutput(result.Output, started.Elapsed);
        if (parsed.Error is "no-output" or "unreadable-output" && FirstLine(result.Errors) is { } reason)
        {
            // Nothing on stdout: what claude printed on stderr says why ("Invalid API key", an unknown option…).
            parsed = parsed with { Error = $"{parsed.Error}: {Shorten(reason)}" };
        }

        if (!parsed.Succeeded)
        {
            logger.LogWarning("Claude Code failed ({Error}, exit {Exit}): {Stderr}", parsed.Error, result.ExitCode, Tail(result.Errors));
        }

        return parsed;
    }

    /// <summary>The arguments of a run, in order (SPEC §8.4): one per element, never through a shell.</summary>
    public static IReadOnlyList<string> Arguments(ClaudeRun run) =>
    [
        "-p", run.Request,
        "--model", run.Model,
        "--effort", run.Effort,
        "--output-format", "json",
        "--json-schema", run.Schema,
        "--append-system-prompt", run.SystemPrompt,
        "--restricted",
        "--tools", string.Join(',', run.Tools),
        "--strict-mcp-config",
        "--mcp-config", run.McpConfigPath,
        "--settings", run.SettingsPath,
        "--permission-mode", "dontAsk",
        "--no-session-persistence",
        "--max-budget-usd", run.MaxBudgetUsd.ToString(CultureInfo.InvariantCulture),
    ];

    /// <summary>
    /// Reads the JSON that <c>--output-format json</c> prints: the answer is in <c>structured_output</c>;
    /// <c>is_error</c> and <c>subtype</c> say why there is none. <c>total_cost_usd</c> is ignored: on a
    /// subscription it is only what the API would have charged.
    /// </summary>
    public static ClaudeRunResult ParseOutput(string stdout, TimeSpan duration)
    {
        // Warnings may come before the JSON line; the result is the last line that parses.
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (!line.StartsWith('{'))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var turns = root.Int("num_turns") ?? 0;
                var subtype = root.Str("subtype");
                var isError = root.Prop("is_error") is { ValueKind: JsonValueKind.True };
                if (root.Prop("structured_output") is { ValueKind: JsonValueKind.Object } output && !isError)
                {
                    return new ClaudeRunResult(output.GetRawText(), null, turns, duration);
                }

                var reason = isError ? root.Str("result") ?? subtype ?? "error" : subtype is null or "success" ? "no-answer" : subtype;
                return new ClaudeRunResult(null, Shorten(reason), turns, duration);
            }
            catch (JsonException)
            {
            }
        }

        return ClaudeRunResult.Failed(stdout.Length == 0 ? "no-output" : "unreadable-output", duration);
    }

    /// <summary><c>claude auth status</c> answers JSON with <c>loggedIn</c> and <c>authMethod</c>.</summary>
    public static (bool LoggedIn, string? Method) ParseAuthStatus(string output)
    {
        var start = output.IndexOf('{', StringComparison.Ordinal);
        if (start < 0)
        {
            return (false, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(output[start..]);
            return (doc.RootElement.Prop("loggedIn") is { ValueKind: JsonValueKind.True }, doc.RootElement.Str("authMethod"));
        }
        catch (JsonException)
        {
            return (false, null);
        }
    }

    /// <summary>"2.1.316 (Claude Code)" → "2.1.316".</summary>
    public static string? ParseVersion(string output) =>
        output.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(p => char.IsDigit(p[0]));

    private static string Shorten(string text) => text.Length <= 200 ? text : text[..200];

    private static string? FirstLine(string text) =>
        text.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    private static readonly char[] LineBreaks = ['\r', '\n'];

    private static string Tail(string text) => text.Length <= 500 ? text : text[^500..];

    private sealed record Execution(int ExitCode, string Output, string Errors, bool TimedOut);

    private async Task<Execution> ExecuteAsync(string path, IReadOnlyList<string> args, string? folder, TimeSpan timeout, CancellationToken ct)
    {
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (folder is not null)
        {
            start.WorkingDirectory = folder;
        }

        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        // A parent Claude session's variables (ANTHROPIC_BASE_URL among them) break the login of the child.
        foreach (var name in start.Environment.Keys.Where(k =>
                     k.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase) || k.StartsWith("ANTHROPIC", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(name);
        }

        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        var errors = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (output)
                {
                    output.AppendLine(e.Data);
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (errors)
                {
                    errors.AppendLine(e.Data);
                }
            }
        };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            logger.LogWarning(ex, "Claude Code could not be started");
            return new Execution(-1, string.Empty, ex.Message, false);
        }

        // Without input, -p waits 3 s for stdin: it is closed right away.
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            return new Execution(-1, output.ToString(), errors.ToString(), true);
        }

        process.WaitForExit();
        return new Execution(process.ExitCode, output.ToString(), errors.ToString(), false);
    }

    private void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(ex, "Claude Code could not be stopped");
        }
    }
}
