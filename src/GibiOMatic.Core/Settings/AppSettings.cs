using System.Text.Json;
using System.Text.Json.Serialization;

namespace GibiOMatic.Core.Settings;

/// <summary>Per-machine settings stored in <c>settings.json</c> (SPEC §11). Never holds secrets.</summary>
public sealed record AppSettings
{
    public const int CurrentSchema = 1;
    public const string DefaultLanguage = "pt-BR";

    public static IReadOnlyList<string> SupportedLanguages { get; } = ["pt-BR", "en"];

    public int Schema { get; init; } = CurrentSchema;
    public string Language { get; init; } = DefaultLanguage;
    public AppTheme Theme { get; init; } = AppTheme.Dark;

    /// <summary>Root folder of the library (the "Arquivo") last opened on this machine.</summary>
    public string? LibraryPath { get; init; }

    /// <summary>Folder watched for new downloads (used from F5 on).</summary>
    public string? InboxPath { get; init; }

    /// <summary>Keys written by newer versions of the app, preserved on save.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownProperties { get; init; }
}

public enum AppTheme
{
    Dark,
    Light,
    System,
}
