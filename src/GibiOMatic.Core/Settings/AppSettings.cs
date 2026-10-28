using System.Text.Json;
using System.Text.Json.Serialization;
using GibiOMatic.Core.Domain;

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

    /// <summary>Root folder of the library (the "Archive") last opened on this machine.</summary>
    public string? LibraryPath { get; init; }

    /// <summary>Folder watched for new downloads (F5).</summary>
    public string? InboxPath { get; init; }

    /// <summary>What happens to a file of the watched folder once it is safely in the library.</summary>
    public AfterImportAction AfterImport { get; init; } = AfterImportAction.RecycleBin;

    /// <summary>Tray icon and starting with the system, so the inbox is watched with the window closed (F5).</summary>
    public BackgroundSettings Background { get; init; } = new();

    /// <summary>
    /// Which format becomes the main original when a book exists in several (SPEC §10),
    /// best first. Default CBZ &gt; PDF &gt; EPUB &gt; CBR.
    /// </summary>
    public IReadOnlyList<BookFormat> FormatPreference { get; init; } = DefaultFormatPreference;

    public MetadataSettings Metadata { get; init; } = new();

    /// <summary>Komga, Kindle… (SPEC §9). Per machine.</summary>
    public IReadOnlyList<DestinationConfig> Destinations { get; init; } = [];

    /// <summary>How the reader shows pages (F3.5).</summary>
    public ReaderSettings Reader { get; init; } = new();

    public static IReadOnlyList<BookFormat> DefaultFormatPreference { get; } =
        [BookFormat.Cbz, BookFormat.Pdf, BookFormat.Epub, BookFormat.Cbr];

    /// <summary>Keys written by newer versions of the app, preserved on save.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownProperties { get; init; }
}

/// <summary>Reader preferences shared by all books; the reading direction is per book.</summary>
public sealed record ReaderSettings
{
    public ReaderFit Fit { get; init; } = ReaderFit.Height;
    public bool TwoPages { get; init; }
    public bool ShowThumbnails { get; init; } = true;

    /// <summary>Text size of text EPUBs, in percent.</summary>
    public int TextScale { get; init; } = 100;
}

/// <summary>Running in the background (SPEC §6, item 7).</summary>
public sealed record BackgroundSettings
{
    /// <summary>
    /// Closing the window keeps the app in the tray. Null: not asked yet; the first close keeps it
    /// in the tray and asks.
    /// </summary>
    public bool? CloseToTray { get; init; }

    public StartupMode StartWithSystem { get; init; } = StartupMode.Off;
}

public enum AfterImportAction
{
    /// <summary>To the system's recycle bin (default); to <c>_imported</c> when the drive has none.</summary>
    RecycleBin,

    /// <summary>To <c>_imported</c>, inside the inbox, keeping the subfolders.</summary>
    MoveToImported,

    /// <summary>Deleted for good.</summary>
    Delete,
}

public enum StartupMode
{
    Off,

    /// <summary>Starts with only the tray icon (default when turned on).</summary>
    Tray,

    /// <summary>Starts with the window open.</summary>
    Window,
}

public enum ReaderFit
{
    /// <summary>The whole page fits in the window (default).</summary>
    Height,

    /// <summary>The page fills the width and scrolls vertically.</summary>
    Width,

    /// <summary>100%: one image pixel per screen pixel.</summary>
    Original,
}

public enum AppTheme
{
    Dark,
    Light,
    System,
}
