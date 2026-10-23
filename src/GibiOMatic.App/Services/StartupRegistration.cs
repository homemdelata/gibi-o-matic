using System.Runtime.Versioning;
using GibiOMatic.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace GibiOMatic.App.Services;

/// <summary>Starting the app with the system (SPEC §6, item 7). Applied when the settings are saved and on every start.</summary>
public interface IStartupRegistration
{
    bool IsSupported { get; }

    /// <summary>Registers, updates (the app may have moved) or removes the entry. Returns false if it failed.</summary>
    bool Apply(StartupMode mode);
}

/// <summary>Command line that the system runs: the app, and <c>--tray</c> to start with only the tray icon.</summary>
public static class StartupCommand
{
    public const string TrayArgument = "--tray";

    public static string For(string executable, StartupMode mode) =>
        mode == StartupMode.Tray ? $"\"{executable}\" {TrayArgument}" : $"\"{executable}\"";
}

/// <summary>Windows: a value under the user's <c>Run</c> key (no administrator needed).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsStartupRegistration(ILogger<WindowsStartupRegistration> logger, string? keyPath = null, string? executable = null)
    : IStartupRegistration
{
    public const string ValueName = "Gibi-o-matic";

    private readonly string _keyPath = keyPath ?? @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsSupported => (executable ?? Environment.ProcessPath) is not null;

    public bool Apply(StartupMode mode)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
            if (mode == StartupMode.Off || (executable ?? Environment.ProcessPath) is not { } exe)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(ValueName, StartupCommand.For(exe, mode));
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            logger.LogWarning(ex, "Could not change the start with Windows");
            return false;
        }
    }
}

/// <summary>Linux: a desktop entry in <c>~/.config/autostart</c> (freedesktop autostart).</summary>
public sealed class LinuxStartupRegistration(ILogger<LinuxStartupRegistration> logger, string? autostartFolder = null, string? executable = null)
    : IStartupRegistration
{
    public const string FileName = "gibiomatic.desktop";

    private string Folder => autostartFolder ?? Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
            ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart");

    // An AppImage runs from a temporary mount: the start entry must point at the .AppImage file itself.
    private string? Executable => executable ?? Environment.GetEnvironmentVariable("APPIMAGE") ?? Environment.ProcessPath;

    public bool IsSupported => Executable is not null;

    public bool Apply(StartupMode mode)
    {
        var file = Path.Combine(Folder, FileName);
        try
        {
            if (mode == StartupMode.Off || Executable is not { } exe)
            {
                File.Delete(file);
            }
            else
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(file, DesktopEntry(exe, mode));
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            logger.LogWarning(ex, "Could not change the start with the system");
            return false;
        }
    }

    public static string DesktopEntry(string executable, StartupMode mode) => $"""
        [Desktop Entry]
        Type=Application
        Name=Gibi-o-matic
        Exec={StartupCommand.For(executable, mode)}
        Terminal=false
        X-GNOME-Autostart-enabled=true

        """;
}
