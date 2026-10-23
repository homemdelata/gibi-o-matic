namespace GibiOMatic.Core.Settings;

public interface ISettingsService
{
    /// <summary>The settings as last loaded or saved.</summary>
    AppSettings Current { get; }

    /// <summary>Loads the settings from disk; returns defaults if the file is missing or unreadable.</summary>
    Task<AppSettings> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(AppSettings settings, CancellationToken ct = default);

    /// <summary>Raised after the settings were saved, with the new ones.</summary>
    event EventHandler<AppSettings>? Saved;
}
