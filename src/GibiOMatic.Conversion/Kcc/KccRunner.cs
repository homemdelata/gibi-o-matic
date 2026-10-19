using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Conversion.Kcc;

/// <summary>
/// Runs the KCC command-line tool as an external process (SPEC §9.3): output read line by line for
/// progress, a timeout, and the whole process tree killed on cancel. KCC exits with 0 even when
/// KindleGen failed, so the MOBI itself is what counts as success.
/// </summary>
public sealed class KccRunner(ILogger<KccRunner> logger) : IKccRunner
{
    private const int KeptLines = 40;

    public async Task<string> ConvertAsync(KccJob job, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(job.OutputDirectory);
        var start = new ProcessStartInfo(job.ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = job.OutputDirectory,
        };
        foreach (var arg in KccTool.Arguments(job))
        {
            start.ArgumentList.Add(arg);
        }

        // KCC is Python: UTF-8 output whatever the console, and KindleGen found where the Kindle Previewer put it.
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONUTF8"] = "1";
        if (KccTool.KindleGenDirectory() is { } kindleGen)
        {
            start.Environment["PATH"] = kindleGen + Path.PathSeparator + start.Environment["PATH"];
        }

        var lines = new Queue<string>();
        var errors = new List<string>();
        void OnLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (lines)
            {
                lines.Enqueue(line);
                if (lines.Count > KeptLines)
                {
                    lines.Dequeue();
                }

                if (KccTool.IsError(line))
                {
                    errors.Add(line.Trim());
                }
            }

            if (KccTool.ProgressOf(line) is { } fraction)
            {
                progress?.Report(fraction);
            }
        }

        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        logger.LogInformation("KCC: {Input} → {Output} ({Profile})", job.Input, job.OutputDirectory, job.Options.Profile);
        try
        {
            if (!process.Start())
            {
                throw new KccException($"KCC could not be started: {job.ExecutablePath}");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new KccException($"KCC could not be started ({job.ExecutablePath}): {ex.Message}");
        }

        try
        {
            // Converting can take every core for minutes: the rest of the machine comes first.
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            logger.LogDebug(ex, "Could not lower the KCC's priority");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(job.Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            throw new KccException($"KCC took longer than {job.Timeout.TotalMinutes:0} minutes and was stopped.");
        }

        // Let the last lines arrive.
        process.WaitForExit();
        string tail;
        lock (lines)
        {
            tail = string.Join('\n', lines);
        }

        logger.LogDebug("KCC output:\n{Output}", tail);
        var outputs = Directory.EnumerateFiles(job.OutputDirectory, "*.mobi").ToList();
        if (outputs.Count == 1 && errors.Count == 0)
        {
            progress?.Report(1);
            return outputs[0];
        }

        if (outputs.Count > 1)
        {
            throw new KccException($"KCC split the book into {outputs.Count} files (too big for one); that is not supported yet.");
        }

        var reason = errors.Count > 0 ? string.Join(" · ", errors.Distinct().Take(3)) : tail.Split('\n').LastOrDefault() ?? string.Empty;
        throw new KccException($"KCC did not write the book (exit code {process.ExitCode}): {reason}");
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
            logger.LogWarning(ex, "KCC could not be stopped");
        }
    }
}
