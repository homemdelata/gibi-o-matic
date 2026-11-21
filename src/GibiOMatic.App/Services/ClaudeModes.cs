using System.Text;
using GibiOMatic.Conversion.Images;
using GibiOMatic.Core;
using GibiOMatic.Core.Secrets;
using GibiOMatic.Metadata;
using GibiOMatic.Metadata.Assisted;
using GibiOMatic.Metadata.Http;
using GibiOMatic.Metadata.Identification;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace GibiOMatic.App.Services;

/// <summary>
/// The two ways Claude Code starts the app during a run (SPEC §8.4): as the ComicVine MCP server
/// (<c>--claude-mcp &lt;folder&gt;</c>) and as the site guard hook (<c>--claude-hook &lt;folder&gt;</c>).
/// Both talk through standard input and output, open no window and end on their own.
/// </summary>
internal static class ClaudeModes
{
    /// <summary>The exit code, or null when the arguments ask for neither mode.</summary>
    public static int? TryRun(string[] args)
    {
        if (args.Length != 2 || !Directory.Exists(args[1]))
        {
            return null;
        }

        return args[0] switch
        {
            ClaudeBridge.HookMode => RunHook(args[1]),
            ClaudeBridge.McpMode => RunMcp(args[1]),
            _ => null,
        };
    }

    private static int RunHook(string folder)
    {
        using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        var call = input.ReadToEnd();
        var sitesFile = Path.Combine(folder, SiteGuard.SitesFile);
        var sites = File.Exists(sitesFile) ? File.ReadAllLines(sitesFile).Where(l => l.Length > 0).ToList() : [];
        var (output, log) = SiteGuard.Decide(call, sites);
        try
        {
            File.AppendAllText(Path.Combine(folder, SiteGuard.LogFile), log + Environment.NewLine);
        }
        catch (IOException)
        {
        }

        if (output is not null)
        {
            using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            stdout.Write(output);
        }

        return 0;
    }

    private static int RunMcp(string folder)
    {
        var paths = AppPaths.ForCurrentUser();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(paths.LogsDirectory, "claude-mcp-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();
        try
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddSerilog(dispose: false));
            services.AddSingleton(ServiceRegistration.BuildConfiguration());
            if (OperatingSystem.IsWindows())
            {
                services.AddSingleton<ISecretStore, DpapiSecretStore>();
            }
            else
            {
                services.AddSingleton<ISecretStore, UnsupportedSecretStore>();
            }

            services.AddSingleton(paths);
            services.AddMetadataSources(paths);
            using var provider = services.BuildServiceProvider();
            var covers = provider.GetRequiredService<CoverImageCache>();
            var server = new ComicVineMcpServer(provider.GetRequiredService<IComicVineApi>(), folder, async (url, ct) =>
                await covers.GetAsync(url, ct) is { } path ? CoverImages.Scale(await File.ReadAllBytesAsync(path, ct), 800, 800) : null);

            using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            server.RunAsync(input, output, CancellationToken.None).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "The ComicVine MCP server stopped");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
