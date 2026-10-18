using GibiOMatic.Conversion.Kcc;
using Microsoft.Extensions.Logging.Abstractions;

namespace GibiOMatic.Conversion.Tests;

public sealed class KccTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A stand-in for kcc-c2e: prints KCC's stages and writes &lt;input name&gt;.mobi into the -o folder,
    /// with the arguments it got. FAKE_KCC=fail prints KindleGen's error and writes nothing (still exit 0,
    /// like the real one); FAKE_KCC=hang never ends.
    /// </summary>
    private string FakeKcc(string mode = "ok")
    {
        if (OperatingSystem.IsWindows())
        {
            var path = _temp.Combine("kcc-" + mode + ".cmd");
            File.WriteAllText(path, $$"""
                @echo off
                setlocal EnableDelayedExpansion
                set out=
                set all=
                :loop
                if "%~1"=="" goto done
                if "%~1"=="-o" set out=%~2
                set all=!all! %1
                set last=%~1
                shift
                goto loop
                :done
                echo Preparing source images...
                echo Processing images...
                if "{{mode}}"=="hang" ping -n 60 127.0.0.1 > nul
                echo Creating MOBI files...
                if "{{mode}}"=="fail" (
                  echo Error: KindleGen failed to create MOBI!
                  exit /b 0
                )
                for %%F in ("%last%") do set name=%%~nF
                echo !all!> "%out%\!name!.mobi"
                echo makeBook: 1.0 seconds
                exit /b 0
                """.Replace("\n", "\r\n"));
            return path;
        }

        var script = _temp.Combine("kcc-" + mode + ".sh");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            out=""
            all="$*"
            while [ $# -gt 0 ]; do
              if [ "$1" = "-o" ]; then out="$2"; fi
              last="$1"
              shift
            done
            echo "Preparing source images..."
            echo "Processing images..."
            if [ "{{mode}}" = "hang" ]; then sleep 60; fi
            echo "Creating MOBI files..."
            if [ "{{mode}}" = "fail" ]; then echo "Error: KindleGen failed to create MOBI!"; exit 0; fi
            name=$(basename "$last" .cbz)
            echo "$all" > "$out/$name.mobi"
            echo "makeBook: 1.0 seconds"
            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    private KccJob Job(string tool, KccOptions? options = null, TimeSpan? timeout = null)
    {
        var input = _temp.Combine("in", "book.cbz");
        Directory.CreateDirectory(Path.GetDirectoryName(input)!);
        File.WriteAllText(input, "cbz");
        return new KccJob(tool, input, _temp.Combine("out"), options ?? new KccOptions(), "Saga, Vol. 1", "Brian K. Vaughan",
            timeout ?? TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Arguments_keep_colour_and_put_the_input_last()
    {
        var job = new KccJob("kcc", "in.cbz", "out", new KccOptions { Manga = true, Splitter = 1, Cropping = 0, ExtraArguments = "--gamma 1.8 --title \"A B\"" },
            "Saga", "Vaughan", TimeSpan.FromMinutes(1));

        Assert.Equal(
            ["-p", "KCS", "-f", "MOBI", "-o", "out", "-t", "Saga", "-a", "Vaughan", "--forcecolor", "-m", "-r", "1", "-c", "0", "--gamma", "1.8", "--title", "A B", "in.cbz"],
            KccTool.Arguments(job));
        Assert.DoesNotContain("--forcecolor", KccTool.Arguments(job with { Options = new KccOptions { Color = false }, Author = null }));
        Assert.DoesNotContain("-a", KccTool.Arguments(job with { Author = null }));
    }

    [Fact]
    public void The_configured_tool_wins_and_a_missing_one_is_not_found()
    {
        var tool = _temp.Combine("tools", "kcc", "kcc-c2e-12.0.0.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(tool)!);
        File.WriteAllText(tool, "x");

        Assert.Equal(tool, KccTool.Locate(null, _temp.Combine("tools")));
        Assert.Equal(tool, KccTool.Locate(tool, _temp.Combine("elsewhere")));
        Assert.Null(KccTool.Locate(_temp.Combine("missing.exe"), _temp.Combine("tools")));
    }

    [Fact]
    public async Task A_run_reports_its_stages_and_returns_the_mobi()
    {
        var steps = new List<double>();
        var runner = new KccRunner(NullLogger<KccRunner>.Instance);

        var mobi = await runner.ConvertAsync(Job(FakeKcc()), new SyncProgress(steps), Ct);

        Assert.Equal(Path.Combine(_temp.Combine("out"), "book.mobi"), mobi);
        var args = await File.ReadAllTextAsync(mobi, Ct);
        Assert.Contains("--forcecolor", args);
        Assert.Contains("KCS", args);
        Assert.Contains(0.05, steps);
        Assert.Equal(1, steps[^1]);
        Assert.True(steps.SequenceEqual(steps.Order()), "progress only goes forward");
    }

    [Fact]
    public async Task KindleGen_failing_is_an_error_even_with_exit_code_zero()
    {
        var runner = new KccRunner(NullLogger<KccRunner>.Instance);

        var error = await Assert.ThrowsAsync<KccException>(() => runner.ConvertAsync(Job(FakeKcc("fail")), null, Ct));

        Assert.Contains("KindleGen failed", error.Message);
    }

    [Fact]
    public async Task A_stuck_run_is_stopped_after_the_timeout()
    {
        var runner = new KccRunner(NullLogger<KccRunner>.Instance);

        var error = await Assert.ThrowsAsync<KccException>(() =>
            runner.ConvertAsync(Job(FakeKcc("hang"), timeout: TimeSpan.FromSeconds(2)), null, Ct));

        Assert.Contains("stopped", error.Message);
    }

    [Fact]
    public async Task A_missing_tool_is_reported()
    {
        var runner = new KccRunner(NullLogger<KccRunner>.Instance);

        await Assert.ThrowsAsync<KccException>(() => runner.ConvertAsync(Job(_temp.Combine("nope.exe")), null, Ct));
    }

    /// <summary>
    /// The real KCC, when GIBIOMATIC_KCC points to kcc-c2e (it is not in the repository nor in CI):
    /// a colour CBZ becomes a MOBI with the Colorsoft profile.
    /// </summary>
    [Fact]
    public async Task The_real_kcc_writes_a_kindle_book()
    {
        var tool = Environment.GetEnvironmentVariable("GIBIOMATIC_KCC");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(tool) || !File.Exists(tool), "Set GIBIOMATIC_KCC to kcc-c2e to run against the real KCC.");
        var input = SyntheticBooks.Cbz(_temp.Combine("in", "saga.cbz"), pages: 4);
        var steps = new List<double>();

        var mobi = await new KccRunner(NullLogger<KccRunner>.Instance).ConvertAsync(
            new KccJob(tool!, input, _temp.Combine("out"), new KccOptions(), "Saga, Vol. 1", "Brian K. Vaughan", TimeSpan.FromMinutes(5)),
            new SyncProgress(steps), Ct);

        var bytes = await File.ReadAllBytesAsync(mobi, Ct);
        Assert.Equal("BOOKMOBI", System.Text.Encoding.ASCII.GetString(bytes, 60, 8));
        Assert.True(bytes.AsSpan().IndexOf("Saga, Vol. 1"u8) >= 0, "the title is in the book");
        Assert.Equal(1, steps[^1]);
    }

    /// <summary>Reports synchronously (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class SyncProgress(List<double> steps) : IProgress<double>
    {
        public void Report(double value)
        {
            lock (steps)
            {
                steps.Add(value);
            }
        }
    }
}
