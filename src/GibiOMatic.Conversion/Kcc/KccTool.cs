using System.Globalization;

namespace GibiOMatic.Conversion.Kcc;

/// <summary>Device and processing options of a Kindle conversion (SPEC §9.3).</summary>
public sealed record KccOptions
{
    /// <summary>KCC device profile; <c>KCS</c> = Kindle Colorsoft (1272×1696, colour).</summary>
    public string Profile { get; init; } = "KCS";

    /// <summary>Keep colour: without <c>--forcecolor</c> KCC turns every page grey, even for a colour Kindle.</summary>
    public bool Color { get; init; } = true;

    /// <summary>Right-to-left reading and splitting (manga).</summary>
    public bool Manga { get; init; }

    /// <summary>Double pages: 0 split, 1 rotate, 2 both; null = KCC's default (split).</summary>
    public int? Splitter { get; init; }

    /// <summary>Cropping: 0 off, 1 margins, 2 margins and page numbers; null = KCC's default (2).</summary>
    public int? Cropping { get; init; }

    /// <summary>More KCC options, typed by the user in "Avançado" (split like a command line).</summary>
    public string? ExtraArguments { get; init; }
}

/// <summary>One conversion: a CBZ in, one MOBI out.</summary>
/// <param name="ExecutablePath">The KCC command-line tool (<c>kcc-c2e</c>).</param>
public sealed record KccJob(string ExecutablePath, string Input, string OutputDirectory, KccOptions Options, string Title, string? Author,
    TimeSpan Timeout);

public sealed class KccException(string message) : Exception(message);

/// <summary>Converts a CBZ for a Kindle with the Kindle Comic Converter.</summary>
public interface IKccRunner
{
    /// <summary>Returns the path of the MOBI written to <see cref="KccJob.OutputDirectory"/>.</summary>
    Task<string> ConvertAsync(KccJob job, IProgress<double>? progress, CancellationToken ct);
}

/// <summary>
/// The pieces of a KCC run that need no process: arguments, progress read from its output, and
/// where the tools are. KCC writes a MOBI that is also an AZW3 ("dual"); the KCC authors keep the
/// <c>.mobi</c> extension for compatibility.
/// </summary>
public static class KccTool
{
    /// <summary>Executable names of the KCC command-line tool, as installed or as released on GitHub.</summary>
    private static readonly string[] ExecutableNames = ["kcc-c2e.exe", "kcc-c2e", "kcc_c2e.exe"];

    public static IReadOnlyList<string> Arguments(KccJob job)
    {
        var o = job.Options;
        var args = new List<string> { "-p", o.Profile, "-f", "MOBI", "-o", job.OutputDirectory, "-t", job.Title };
        if (!string.IsNullOrWhiteSpace(job.Author))
        {
            args.AddRange(["-a", job.Author]);
        }

        if (o.Color)
        {
            args.Add("--forcecolor");
        }

        if (o.Manga)
        {
            args.Add("-m");
        }

        if (o.Splitter is { } splitter)
        {
            args.AddRange(["-r", splitter.ToString(CultureInfo.InvariantCulture)]);
        }

        if (o.Cropping is { } cropping)
        {
            args.AddRange(["-c", cropping.ToString(CultureInfo.InvariantCulture)]);
        }

        args.AddRange(SplitCommandLine(o.ExtraArguments));
        args.Add(job.Input);
        return args;
    }

    /// <summary>How far the run got, from the stage KCC just printed (it prints no percentages).</summary>
    public static double? ProgressOf(string line) => line.Trim() switch
    {
        var l when l.StartsWith("Preparing source images", StringComparison.Ordinal) => 0.05,
        var l when l.StartsWith("Checking images", StringComparison.Ordinal) => 0.1,
        var l when l.StartsWith("Processing images", StringComparison.Ordinal) => 0.15,
        var l when l.StartsWith("imgFileProcessing", StringComparison.Ordinal) => 0.6,
        var l when l.StartsWith("Creating EPUB", StringComparison.Ordinal) => 0.65,
        var l when l.StartsWith("Creating MOBI", StringComparison.Ordinal) => 0.75,
        var l when l.StartsWith("makeBook", StringComparison.Ordinal) => 1.0,
        _ => null,
    };

    /// <summary>A line that explains a failure (KCC exits with 0 even when KindleGen failed).</summary>
    public static bool IsError(string line) =>
        line.Contains("ERROR", StringComparison.Ordinal) || line.Contains("Error:", StringComparison.Ordinal)
        || line.StartsWith("Traceback", StringComparison.Ordinal);

    /// <summary>The KCC tool: the configured path, the one the app downloaded, then the PATH.</summary>
    public static string? Locate(string? configured, string toolsDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        var downloaded = Path.Combine(toolsDirectory, "kcc");
        if (Directory.Exists(downloaded)
            && Directory.EnumerateFiles(downloaded, "kcc-c2e*.exe").OrderDescending(StringComparer.Ordinal).FirstOrDefault() is { } tool)
        {
            return tool;
        }

        return OnPath(ExecutableNames);
    }

    /// <summary>
    /// Folder of Amazon's KindleGen, which KCC needs to write MOBI: on the PATH, or inside the Kindle
    /// Previewer (Windows), which KCC's command line does not look into by itself.
    /// </summary>
    public static string? KindleGenDirectory()
    {
        if (OnPath(["kindlegen.exe", "kindlegen"]) is { } onPath)
        {
            return Path.GetDirectoryName(onPath);
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var previewer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Amazon", "Kindle Previewer 3", "lib", "fc", "bin");
        return File.Exists(Path.Combine(previewer, "kindlegen.exe")) ? previewer : null;
    }

    private static string? OnPath(IEnumerable<string> names)
    {
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var folder in folders)
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(folder.Trim('"'), name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry.
                }
            }
        }

        return null;
    }

    /// <summary>Splits "--gamma 1.8 --title \"A B\"" the way a shell would (quotes group words).</summary>
    public static IReadOnlyList<string> SplitCommandLine(string? text)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return args;
        }

        var current = new System.Text.StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    any = false;
                }
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }

        if (any)
        {
            args.Add(current.ToString());
        }

        return args;
    }
}
