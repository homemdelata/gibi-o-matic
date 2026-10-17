namespace GibiOMatic.Core;

/// <summary>
/// Per-machine locations used by the app. Settings roam with the user profile (config);
/// the index, caches and logs are local and never live inside a synced folder (data).
/// </summary>
public sealed record AppPaths(string ConfigDirectory, string DataDirectory)
{
    public string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");
    public string SecretsDirectory => Path.Combine(ConfigDirectory, "secrets");
    public string IndexDirectory => Path.Combine(DataDirectory, "index");
    public string LogsDirectory => Path.Combine(DataDirectory, "logs");
    public string ThumbnailsDirectory => Path.Combine(DataDirectory, "thumbs");

    /// <summary>
    /// Windows: %APPDATA%\GibiOMatic and %LOCALAPPDATA%\GibiOMatic.
    /// Linux: ~/.config/gibiomatic and ~/.local/share/gibiomatic.
    /// </summary>
    public static AppPaths ForCurrentUser()
    {
        var folderName = OperatingSystem.IsWindows() ? "GibiOMatic" : "gibiomatic";
        return new AppPaths(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), folderName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), folderName));
    }
}
