using System.Runtime.InteropServices;
using System.Security.Cryptography;
using GibiOMatic.Core;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Destinations.Kindle;

/// <summary>
/// Downloads the KCC command-line tool from its official GitHub release when the user asks for it.
/// The app does not ship the KCC (its build bundles PyMuPDF, which is AGPL): the user gets the
/// author's own file. The version and its SHA-256 are fixed here, so a changed file is refused.
/// </summary>
public sealed class KccInstaller(HttpClient http, AppPaths paths, ILogger<KccInstaller> logger)
{
    public const string Version = "12.0.0";

    /// <summary>Size of the download, for the button ("85 MB").</summary>
    public const long DownloadBytes = 89_546_400;

    private const string Url = "https://github.com/ciromattia/kcc/releases/download/v12.0.0/tool_cli_c2e_12.0.0_AMD64.exe";
    private const string Sha256 = "8300504c7d6685eaa63a803e490c7ef2f87a4c9e43cc49c3e361fada892d85ff";

    /// <summary>Only the Windows x64 tool is pinned; elsewhere the user installs the KCC and points to it.</summary>
    public static bool IsAvailable => OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64;

    public string TargetPath => Path.Combine(paths.ToolsDirectory, "kcc", $"kcc-c2e-{Version}.exe");

    /// <summary>Returns the installed tool's path.</summary>
    public async Task<string> InstallAsync(IProgress<double>? progress, CancellationToken ct)
    {
        if (!IsAvailable)
        {
            throw new PlatformNotSupportedException("The KCC download is only available for Windows (x64).");
        }

        var target = TargetPath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + ".part";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url);
            request.Headers.UserAgent.ParseAdd($"GibiOMatic/{typeof(KccInstaller).Assembly.GetName().Version?.ToString(3) ?? "0"}");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? DownloadBytes;
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long read = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    progress?.Report(Math.Min(1, (double)read / total));
                }
            }

            await using (var check = File.OpenRead(partial))
            {
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, ct));
                if (hash != Sha256)
                {
                    throw new InvalidDataException($"The downloaded KCC does not match the expected file (SHA-256 {hash}).");
                }
            }

            File.Move(partial, target, overwrite: true);
            logger.LogInformation("KCC {Version} installed at {Path}", Version, target);
            return target;
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }
}
