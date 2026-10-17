using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Core.Settings;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.App.ViewModels;

public sealed record LanguageOption(string Code, string DisplayName);

public sealed record ThemeOption(AppTheme Theme, string DisplayName, bool IsAvailable);

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
    private readonly string _runningLanguage;

    public SettingsPageViewModel(
        ISettingsService settings,
        LibrarySession session,
        ILibraryService libraries,
        IFolderPicker folderPicker,
        ILogger<SettingsPageViewModel> logger)
    {
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
        new(AppTheme.Light, Strings.Theme_Light, IsAvailable: false),
        new(AppTheme.System, Strings.Theme_System, IsAvailable: false),
    ];

    public string LibraryPath => _session.Current?.RootPath ?? string.Empty;

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
        var updated = _settings.Current with
        {
            InboxPath = string.IsNullOrWhiteSpace(InboxPath) ? null : InboxPath.Trim(),
            Language = SelectedLanguage.Code,
            Theme = SelectedTheme.Theme,
            FormatPreference = Formats.Select(f => f.Format).ToList(),
        };

        try
        {
            await _settings.SaveAsync(updated, ct);
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
    }

    private void SetStatus(string message, bool isError)
    {
        StatusIsError = isError;
        StatusMessage = message;
    }
}
