using System.Reflection;
using GibiOMatic.Core.Settings;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace GibiOMatic.App.Services;

/// <summary>A newer version found on the update source.</summary>
public sealed record AppUpdate(string Version, object Handle);

/// <summary>Updates of the installed app (F6: Velopack, GitHub Releases).</summary>
public interface IAppUpdater
{
    /// <summary>False when the app was not installed by the installer (development, zip): no updates.</summary>
    bool IsSupported { get; }

    string CurrentVersion { get; }

    /// <summary>The newer version, or null. Throws when the source cannot be reached.</summary>
    Task<AppUpdate?> CheckAsync(CancellationToken ct);

    Task DownloadAsync(AppUpdate update, IProgress<int>? progress, CancellationToken ct);

    /// <summary>Applies the downloaded update once the app exits, and starts it again.</summary>
    void ApplyOnExit(AppUpdate update);
}

public sealed class VelopackUpdater : IAppUpdater
{
    public const string RepositoryUrl = "https://github.com/homemdelata/gibi-o-matic";

    /// <summary>
    /// A folder with packages made by <c>tools/publish.ps1</c>, used instead of GitHub: lets an
    /// installed app be updated from local builds, to test updates while the repository is private.
    /// </summary>
    public const string SourceVariable = "GIBIOMATIC_UPDATE_SOURCE";

    private readonly UpdateManager _manager;

    public VelopackUpdater(ILogger<VelopackUpdater> logger)
    {
        var folder = Environment.GetEnvironmentVariable(SourceVariable);
        _manager = new UpdateManager(CreateSource(folder));
        if (!string.IsNullOrWhiteSpace(folder))
        {
            logger.LogInformation("Updates come from the local folder {Folder} ({Variable})", folder, SourceVariable);
        }
    }

    public static IUpdateSource CreateSource(string? folder) =>
        string.IsNullOrWhiteSpace(folder)
            ? new GithubSource(RepositoryUrl, accessToken: null, prerelease: false)
            : new SimpleFileSource(new DirectoryInfo(folder.Trim()));

    public bool IsSupported => _manager.IsInstalled;

    public string CurrentVersion => _manager.IsInstalled ? _manager.CurrentVersion!.ToString() : AppInfo.Version;

    public async Task<AppUpdate?> CheckAsync(CancellationToken ct)
    {
        var info = await _manager.CheckForUpdatesAsync().WaitAsync(ct);
        return info is null ? null : new AppUpdate(info.TargetFullRelease.Version.ToString(), info);
    }

    public Task DownloadAsync(AppUpdate update, IProgress<int>? progress, CancellationToken ct) =>
        _manager.DownloadUpdatesAsync((UpdateInfo)update.Handle, p => progress?.Report(p), ct);

    // Started again with the window: the user asked for the update from it.
    public void ApplyOnExit(AppUpdate update) =>
        _manager.WaitExitThenApplyUpdates(((UpdateInfo)update.Handle).TargetFullRelease, silent: false, restart: true, []);
}

public static class AppInfo
{
    /// <summary>"0.6.1-alpha.0.3+751a8612…": MinVer's version, then the commit (SourceLink).</summary>
    private static readonly string Informational =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>"0.6.0", or "0.6.1-alpha.0.3" between tags (MinVer), without the commit.</summary>
    public static string Version { get; } = Informational.Split('+')[0];

    /// <summary>"751a861": the commit built; null when the build didn't know it.</summary>
    public static string? Commit { get; } = Informational.Split('+') is [_, { Length: >= 7 } sha, ..] ? sha[..7] : null;

    /// <summary>Built with uncommitted changes (<c>GibiLocalChanges</c>, set by Directory.Build.targets).</summary>
    public static bool HasLocalChanges { get; } = typeof(AppInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(a => a is { Key: "GibiLocalChanges", Value: "true" });
}

public enum UpdateState
{
    /// <summary>Not installed by the installer: nothing to check.</summary>
    Unsupported,
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,

    /// <summary>The source could not be reached (offline, or the repository is still private).</summary>
    Failed,
}

/// <summary>
/// Looks for a new version shortly after the start and once a day, unless turned off in the
/// Settings, and installs it when the user asks (F6). Shared by the banner in the shell and by the
/// Settings.
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IAppUpdater _updater;
    private readonly IUiThread _ui;
    private readonly ISettingsService _settings;
    private readonly ILogger<UpdateChecker> _logger;
    private Timer? _timer;

    public UpdateChecker(IAppUpdater updater, IUiThread ui, ISettingsService settings, ILogger<UpdateChecker> logger)
    {
        _updater = updater;
        _ui = ui;
        _settings = settings;
        _logger = logger;
        State = updater.IsSupported ? UpdateState.Idle : UpdateState.Unsupported;
    }

    public string CurrentVersion => _updater.CurrentVersion;

    public UpdateState State { get; private set; }

    public AppUpdate? Available { get; private set; }

    public int DownloadProgress { get; private set; }

    /// <summary>Raised on the UI thread when the state or the progress changes.</summary>
    public event EventHandler? Changed;

    /// <summary>The update is ready to be applied: the app must exit (it starts again updated).</summary>
    public event EventHandler? RestartRequested;

    public void Start()
    {
        if (_updater.IsSupported)
        {
            _timer ??= new Timer(_ => _ui.Post(() => _ = CheckOnScheduleAsync()), null, FirstCheck, Interval);
        }
    }

    /// <summary>The check of the timer: skipped while the automatic checks are off.</summary>
    public Task CheckOnScheduleAsync(CancellationToken ct = default) =>
        _settings.Current.CheckForUpdates ? CheckAsync(ct) : Task.CompletedTask;

    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (!_updater.IsSupported || State is UpdateState.Checking or UpdateState.Downloading)
        {
            return;
        }

        SetState(UpdateState.Checking);
        try
        {
            Available = await _updater.CheckAsync(ct);
            SetState(Available is null ? UpdateState.UpToDate : UpdateState.Available);
            if (Available is not null)
            {
                _logger.LogInformation("Version {Version} is available", Available.Version);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Offline, rate-limited, or the repository is private: try again next time.
            _logger.LogInformation(ex, "Could not look for updates");
            SetState(UpdateState.Failed);
        }
    }

    /// <summary>Downloads the update, then asks the app to exit so it is applied and the app restarts.</summary>
    public async Task InstallAsync(CancellationToken ct = default)
    {
        if (Available is not { } update || State == UpdateState.Downloading)
        {
            return;
        }

        SetState(UpdateState.Downloading);
        try
        {
            await _updater.DownloadAsync(update, new Progress<int>(p =>
            {
                DownloadProgress = p;
                Changed?.Invoke(this, EventArgs.Empty);
            }), ct);
            _updater.ApplyOnExit(update);
            _logger.LogInformation("Restarting to install version {Version}", update.Version);
            RestartRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not download version {Version}", update.Version);
            SetState(UpdateState.Failed);
        }
    }

    private void SetState(UpdateState state)
    {
        State = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _timer?.Dispose();
}
