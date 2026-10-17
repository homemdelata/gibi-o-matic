using System.Text.Json;
using GibiOMatic.Core.IO;
using GibiOMatic.Core.Json;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Core.Settings;

public sealed class JsonSettingsService(AppPaths paths, ILogger<JsonSettingsService> logger) : ISettingsService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppSettings Current { get; private set; } = new();

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Current = await ReadAsync(ct);
            return Current;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(ct);
        try
        {
            var json = JsonSerializer.Serialize(settings with { Schema = AppSettings.CurrentSchema }, GibiJson.Options);
            await AtomicFile.WriteAllTextAsync(paths.SettingsFile, json, ct);
            Current = settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AppSettings> ReadAsync(CancellationToken ct)
    {
        var file = paths.SettingsFile;
        if (!File.Exists(file))
        {
            return new AppSettings();
        }

        try
        {
            await using var stream = File.OpenRead(file);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, GibiJson.Options, ct);
            return Normalize(settings ?? new AppSettings());
        }
        catch (JsonException ex)
        {
            // Keep the broken file for inspection instead of silently overwriting it later.
            var backup = file + ".bad";
            logger.LogWarning(ex, "Settings file is invalid; using defaults and keeping a copy at {Backup}", backup);
            File.Copy(file, backup, overwrite: true);
            return new AppSettings();
        }
    }

    private static AppSettings Normalize(AppSettings settings) =>
        AppSettings.SupportedLanguages.Contains(settings.Language)
            ? settings
            : settings with { Language = AppSettings.DefaultLanguage };
}
