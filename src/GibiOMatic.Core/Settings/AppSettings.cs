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

    /// <summary>Root folder of the library (the "Arquivo") last opened on this machine.</summary>
    public string? LibraryPath { get; init; }

    /// <summary>Folder watched for new downloads (used from F5 on).</summary>
    public string? InboxPath { get; init; }

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
