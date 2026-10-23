using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
using GibiOMatic.App.Services.Tray;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Core.Secrets;
using GibiOMatic.Core.Settings;
using GibiOMatic.Metadata;
using GibiOMatic.Metadata.Http;
using GibiOMatic.Metadata.Sources;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.App.ViewModels;

public sealed record LanguageOption(string Code, string DisplayName);

public sealed record ThemeOption(AppTheme Theme, string DisplayName, bool IsAvailable);

/// <summary>A metadata source in the priority list.</summary>
public sealed partial class SourceOptionViewModel(string id, string name, string help, bool enabled) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Help { get; } = help;
    public bool NeedsKey => Id == SourceIds.ComicVine;

    [ObservableProperty]
    public partial bool Enabled { get; set; } = enabled;

    [ObservableProperty]
    public partial int Position { get; set; }
}

/// <summary>A format in the preference list (best first).</summary>
public sealed record FormatOption(BookFormat Format, int Position)
{
    public string Label => QualityText.FormatLabel(Format);
    public string Rank => Position.ToString(CultureInfo.CurrentCulture);
}

/// <summary>Settings (SPEC §11): folders and general options. Metadata sources arrive in F2.</summary>
public sealed partial class SettingsPageViewModel : PageViewModel
{
    private readonly ISettingsService _settings;
    private readonly LibrarySession _session;
    private readonly ILibraryService _libraries;
    private readonly IFolderPicker _folderPicker;
    private readonly ILogger<SettingsPageViewModel> _logger;
    private readonly ISecretStore _secrets;
    private readonly ComicVineKeyProvider _keys;
    private readonly ComicVineClient _comicVine;
    private readonly IStartupRegistration _startup;
    private readonly string _runningLanguage;

    public SettingsPageViewModel(
        ISettingsService settings,
        LibrarySession session,
        ILibraryService libraries,
        IFolderPicker folderPicker,
        ISecretStore secrets,
        ComicVineKeyProvider keys,
        ComicVineClient comicVine,
        IStartupRegistration startup,
        TrayController tray,
        UpdateChecker updates,
        ILogger<SettingsPageViewModel> logger)
    {
        _startup = startup;
        Update = new UpdateStatusViewModel(updates);
        TraySupported = tray.IsSupported;
        _secrets = secrets;
        _keys = keys;
        _comicVine = comicVine;
        _settings = settings;
        _session = session;
        _libraries = libraries;
        _folderPicker = folderPicker;
        _logger = logger;
        _runningLanguage = settings.Current.Language;
        Subtitle = Strings.Settings_Subtitle;
        LoadFrom(settings.Current);
    }

    public override string Title => Strings.Nav_Settings;

    // Language names are shown in their own language, as is customary.
    public IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new("pt-BR", "Português (Brasil)"),
        new("en", "English"),
    ];

    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new(AppTheme.Dark, Strings.Theme_Dark, IsAvailable: true),
        new(AppTheme.Light, Strings.Theme_Light, IsAvailable: true),
        new(AppTheme.System, Strings.Theme_System, IsAvailable: true),
    ];

    public string LibraryPath => _session.Current?.RootPath ?? string.Empty;

    /// <summary>Metadata sources in priority order (SPEC §8.1).</summary>
    public ObservableCollection<SourceOptionViewModel> Sources { get; } = [];

    /// <summary>"Sempre revisar": never approve automatically.</summary>
    [ObservableProperty]
    public partial bool AlwaysReview { get; set; }

    /// <summary>A new ComicVine key typed by the user (never shown back).</summary>
    [ObservableProperty]
    public partial string? ComicVineKeyInput { get; set; }

    [ObservableProperty]
    public partial string? KeyStatus { get; private set; }

    [ObservableProperty]
    public partial bool KeyStatusIsError { get; private set; }

    public bool CanStoreSecrets => _secrets.IsSupported;

    /// <summary>Which format becomes the main original when a book exists in several (SPEC §10).</summary>
    public ObservableCollection<FormatOption> Formats { get; } = [];

    [ObservableProperty]
    public partial string? InboxPath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestartRequired))]
    public partial LanguageOption SelectedLanguage { get; set; }

    [ObservableProperty]
    public partial ThemeOption SelectedTheme { get; set; }

    public bool RestartRequired => SelectedLanguage.Code != _runningLanguage;

    /// <summary>Version and updates (F6).</summary>
    public UpdateStatusViewModel Update { get; }

    // ---- Background (F5) ----

    public bool TraySupported { get; }

    public bool StartupSupported => _startup.IsSupported;

    public string StartWithSystemLabel => OperatingSystem.IsWindows() ? Strings.Settings_StartWithWindows : Strings.Settings_StartWithSystem;

    /// <summary>Closing the window keeps the app in the tray (on until the user says otherwise).</summary>
    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    [ObservableProperty]
    public partial bool StartWithSystem { get; set; }

    /// <summary>With <see cref="StartWithSystem"/>: only the tray icon (default) or the window.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartWithWindow))]
    public partial bool StartInTray { get; set; }

    public bool StartWithWindow
    {
        get => !StartInTray;
        set => StartInTray = !value;
    }

    [ObservableProperty]
    public partial string? StatusMessage { get; private set; }

    [ObservableProperty]
    public partial bool StatusIsError { get; private set; }

    [RelayCommand]
    private async Task OpenOtherLibraryAsync(CancellationToken ct)
    {
        var folder = await _folderPicker.PickFolderAsync(Strings.Picker_OpenTitle, ct);
        if (folder is null)
        {
            return;
        }

        try
        {
            // The window replaces the whole shell when the library changes.
            await _session.OpenAsync(folder, ct);
        }
        catch (Exception ex) when (LibrarySession.IsExpectedFailure(ex))
        {
            _logger.LogWarning(ex, "Could not open {Folder} as a library", folder);
            SetStatus(LibrarySession.DescribeError(ex), isError: true);
        }
    }

    [RelayCommand]
    private async Task RebuildIndexAsync(CancellationToken ct)
    {
        if (_session.Current is not { } library)
        {
            return;
        }

        try
        {
            var result = await _libraries.RebuildIndexAsync(library, ct);
            SetStatus(result.Issues.Count == 0
                ? string.Format(CultureInfo.CurrentCulture, Strings.Settings_RebuildDone, result.IndexedBooks)
                : string.Format(CultureInfo.CurrentCulture, Strings.Settings_RebuildIssues, result.IndexedBooks, result.Issues.Count),
                isError: false);
        }
        catch (Exception ex) when (LibrarySession.IsExpectedFailure(ex))
        {
            _logger.LogError(ex, "Index rebuild failed for library {LibraryId}", library.Id);
            SetStatus(LibrarySession.DescribeError(ex), isError: true);
        }
    }

    [RelayCommand]
    private async Task ChooseInboxAsync(CancellationToken ct)
    {
        var folder = await _folderPicker.PickFolderAsync(Strings.Picker_InboxTitle, ct);
        if (folder is not null)
        {
            InboxPath = folder;
        }
    }

    public override async Task OnNavigatedToAsync(CancellationToken ct = default) => await UpdateKeyStatusAsync(ct);

    private async Task UpdateKeyStatusAsync(CancellationToken ct)
    {
        KeyStatusIsError = false;
        if (!_secrets.IsSupported)
        {
            KeyStatus = await _keys.GetKeyAsync(ct) is null ? Strings.Key_Unsupported : Strings.Key_FromEnvironment;
            return;
        }

        KeyStatus = await _keys.IsFromConfigurationAsync(ct)
            ? Strings.Key_FromEnvironment
            : await _keys.GetKeyAsync(ct) is null ? Strings.Key_Missing : Strings.Key_Stored;
    }

    /// <summary>The key is stored right away (protected with DPAPI), not with "Salvar".</summary>
    [RelayCommand]
    private async Task SaveKeyAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ComicVineKeyInput) || !_secrets.IsSupported)
        {
            return;
        }

        await _secrets.SetAsync(SecretNames.ComicVineApiKey, ComicVineKeyInput.Trim(), ct);
        ComicVineKeyInput = null;
        await UpdateKeyStatusAsync(ct);
        await TestKeyAsync(ct);
    }

    [RelayCommand]
    private async Task TestKeyAsync(CancellationToken ct)
    {
        if (await _keys.GetKeyAsync(ct) is null)
        {
            await UpdateKeyStatusAsync(ct);
            return;
        }

        try
        {
            var ok = await _comicVine.TestKeyAsync(ct);
            KeyStatusIsError = !ok;
            KeyStatus = ok ? Strings.Key_Works : Strings.Key_Rejected;
        }
        catch (MetadataSourceException ex)
        {
            _logger.LogWarning(ex, "ComicVine key test failed");
            KeyStatusIsError = true;
            KeyStatus = string.Format(CultureInfo.CurrentCulture, Strings.Note_Unavailable, "ComicVine");
        }
    }

    [RelayCommand]
    private async Task RemoveKeyAsync(CancellationToken ct)
    {
        if (_secrets.IsSupported)
        {
            await _secrets.RemoveAsync(SecretNames.ComicVineApiKey, ct);
        }

        await UpdateKeyStatusAsync(ct);
    }

    [RelayCommand]
    private void MoveSourceUp(SourceOptionViewModel option) => MoveSource(option, -1);

    [RelayCommand]
    private void MoveSourceDown(SourceOptionViewModel option) => MoveSource(option, +1);

    private void MoveSource(SourceOptionViewModel option, int delta)
    {
        var index = Sources.IndexOf(option);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Sources.Count)
        {
            return;
        }

        Sources.Move(index, target);
        NumberSources();
    }

    private void NumberSources()
    {
        for (var i = 0; i < Sources.Count; i++)
        {
            Sources[i].Position = i + 1;
        }
    }

    private void SetSources(MetadataSettings metadata)
    {
        Sources.Clear();
        foreach (var source in metadata.Normalized().Sources)
        {
            var (name, help) = source.Id switch
            {
                SourceIds.OpenLibrary => ("Open Library", Strings.Source_OpenLibraryHelp),
                SourceIds.ComicVine => ("ComicVine", Strings.Source_ComicVineHelp),
                _ => ("Google Books", Strings.Source_GoogleBooksHelp),
            };
            Sources.Add(new SourceOptionViewModel(source.Id, name, help, source.Enabled));
        }

        NumberSources();
    }

    [RelayCommand]
    private void MoveFormatUp(FormatOption option) => MoveFormat(option, -1);

    [RelayCommand]
    private void MoveFormatDown(FormatOption option) => MoveFormat(option, +1);

    private void MoveFormat(FormatOption option, int delta)
    {
        var order = Formats.Select(f => f.Format).ToList();
        var index = order.IndexOf(option.Format);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= order.Count)
        {
            return;
        }

        (order[index], order[target]) = (order[target], order[index]);
        SetFormats(order);
    }

    private void SetFormats(IEnumerable<BookFormat> order)
    {
        Formats.Clear();
        foreach (var (format, i) in order.Select((f, i) => (f, i)))
        {
            Formats.Add(new FormatOption(format, i + 1));
        }
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        // The library is not a "preference": it stays as is.
        LoadFrom(new AppSettings { LibraryPath = _settings.Current.LibraryPath });
        StatusMessage = null;
    }

    [RelayCommand]
    private async Task SaveAsync(CancellationToken ct)
    {
        var inbox = string.IsNullOrWhiteSpace(InboxPath) ? null : InboxPath.Trim();
        if (inbox is not null && LibraryPath.Length > 0 && (IsInside(inbox, LibraryPath) || IsInside(LibraryPath, inbox)))
        {
            SetStatus(Strings.Settings_InboxInsideLibrary, isError: true);
            return;
        }

        var background = _settings.Current.Background;
        var startup = !StartWithSystem ? StartupMode.Off : StartInTray ? StartupMode.Tray : StartupMode.Window;
        var updated = _settings.Current with
        {
            InboxPath = inbox,
            Background = background with
            {
                // Not asked yet and left as is: the first close still asks.
                CloseToTray = background.CloseToTray is null && CloseToTray ? null : CloseToTray,
                StartWithSystem = startup,
            },
            Language = SelectedLanguage.Code,
            Theme = SelectedTheme.Theme,
            FormatPreference = Formats.Select(f => f.Format).ToList(),
            Metadata = new MetadataSettings
            {
                Sources = Sources.Select(src => new SourceSetting(src.Id, src.Enabled)).ToList(),
                AlwaysReview = AlwaysReview,
            },
        };

        try
        {
            await _settings.SaveAsync(updated, ct);
            if (StartupSupported && (startup != background.StartWithSystem || startup != StartupMode.Off) && !_startup.Apply(startup))
            {
                SetStatus(Strings.Settings_StartupFailed, isError: true);
                return;
            }

            SetStatus(Strings.Settings_Saved, isError: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save settings");
            SetStatus(string.Format(Strings.Error_FileAccess, ex.Message), isError: true);
        }
    }

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(SelectedLanguage), nameof(SelectedTheme))]
    private void LoadFrom(AppSettings settings)
    {
        InboxPath = settings.InboxPath;
        SelectedLanguage = Languages.FirstOrDefault(l => l.Code == settings.Language) ?? Languages[0];
        SelectedTheme = Themes.FirstOrDefault(t => t.Theme == settings.Theme && t.IsAvailable) ?? Themes[0];
        SetFormats(settings.FormatPreference);
        SetSources(settings.Metadata);
        AlwaysReview = settings.Metadata.AlwaysReview;
        CloseToTray = settings.Background.CloseToTray ?? true;
        StartWithSystem = settings.Background.StartWithSystem != StartupMode.Off;
        StartInTray = settings.Background.StartWithSystem != StartupMode.Window;
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    private static bool IsInside(string path, string folder)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(folder), Path.GetFullPath(path));
        return relative == "." || (!Path.IsPathRooted(relative) && !relative.StartsWith("..", StringComparison.Ordinal));
    }

    public override void Dispose() => Update.Dispose();

    private void SetStatus(string message, bool isError)
    {
        StatusIsError = isError;
        StatusMessage = message;
    }
}
