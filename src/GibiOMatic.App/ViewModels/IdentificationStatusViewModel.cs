using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
using GibiOMatic.Metadata;
using GibiOMatic.Metadata.Identification;

namespace GibiOMatic.App.ViewModels;

/// <summary>"Buscando metadados: “Saga, Vol. 1” · 37 na fila · Pausar", shown while identification runs.</summary>
public sealed partial class IdentificationStatusViewModel : ViewModelBase, IDisposable
{
    private readonly LibrarySession _session;
    private readonly Debouncer _refresh;

    public IdentificationStatusViewModel(LibrarySession session, IUiThread ui)
    {
        _session = session;
        _refresh = new Debouncer(ui, TimeSpan.FromMilliseconds(250), RefreshAsync);
        if (session.Identification is { } queue)
        {
            queue.Changed += OnChanged;
        }
    }

    [ObservableProperty]
    public partial bool IsVisible { get; private set; }

    [ObservableProperty]
    public partial string? Text { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseLabel))]
    public partial bool IsPaused { get; private set; }

    public string PauseLabel => IsPaused ? Strings.Inbox_Resume : Strings.Inbox_Pause;

    public async Task RefreshAsync()
    {
        if (_session.Identification is not { } queue)
        {
            return;
        }

        var queued = (await queue.GetCountsAsync()).Queued;
        IsPaused = queue.IsPaused;
        IsVisible = queued > 0;
        Text = IsPaused
            ? string.Format(CultureInfo.CurrentCulture, Strings.Identify_Paused, queued)
            : queue.CurrentTitle is { } title
                ? string.Format(CultureInfo.CurrentCulture, Strings.Identify_Running, title, queued)
                : string.Format(CultureInfo.CurrentCulture, Strings.Identify_Waiting, queued);
    }

    [RelayCommand]
    private async Task TogglePauseAsync()
    {
        if (_session.Identification is not { } queue)
        {
            return;
        }

        if (queue.IsPaused)
        {
            await queue.ResumeAsync();
        }
        else
        {
            await queue.PauseAsync();
        }

        await RefreshAsync();
    }

    private void OnChanged(object? sender, EventArgs e) => _refresh.Trigger();

    public void Dispose()
    {
        if (_session.Identification is { } queue)
        {
            queue.Changed -= OnChanged;
        }

        _refresh.Dispose();
    }
}

/// <summary>Texts of the identification shared by the Review and the Editor.</summary>
public static class IdentificationText
{
    public static string FieldLabel(ProposalField field) => field switch
    {
        ProposalField.Title => Strings.Field_Title,
        ProposalField.Series => Strings.Field_Series,
        ProposalField.Number => Strings.Field_Number,
        ProposalField.Kind => Strings.Field_Kind,
        ProposalField.Date => Strings.Field_Date,
        ProposalField.Publisher => Strings.Field_Publisher,
        ProposalField.Writers => Strings.Role_Writer,
        ProposalField.Artists => Strings.Role_Artist,
        ProposalField.Inkers => Strings.Role_Inker,
        ProposalField.Colorists => Strings.Role_Colorist,
        ProposalField.Letterers => Strings.Role_Letterer,
        ProposalField.CoverArtists => Strings.Role_Cover,
        ProposalField.Isbn => Strings.Field_Isbn,
        ProposalField.Summary => Strings.Field_Summary,
        ProposalField.OneShot => Strings.Field_OneShot,
        _ => field.ToString(),
    };

    public static string Reasons(MetadataCandidate candidate)
    {
        var parts = candidate.Reasons.Distinct().Select(r => r switch
        {
            MatchReason.Isbn => Strings.Reason_Isbn,
            MatchReason.Title => Strings.Reason_Title,
            MatchReason.Number => Strings.Reason_Number,
            MatchReason.Publisher => Strings.Reason_Publisher,
            MatchReason.Year => Strings.Reason_Year,
            MatchReason.Kind => Strings.Reason_Kind,
            MatchReason.SecondSource => Strings.Reason_SecondSource,
            _ => r.ToString(),
        });
        return string.Join(" + ", parts);
    }

    public static string SourceName(string source) => source switch
    {
        Metadata.SourceIds.ComicVine => "ComicVine",
        Metadata.SourceIds.OpenLibrary => "Open Library",
        Metadata.SourceIds.GoogleBooks => "Google Books",
        _ => source,
    };

    /// <summary>"comicvine:no-key;openlibrary:Unavailable" → sentences for the user.</summary>
    public static string? Notes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return null;
        }

        var sentences = notes.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(note =>
        {
            var parts = note.Split(':', 2);
            var source = SourceName(parts[0]);
            return parts.Length < 2 ? note : parts[1] switch
            {
                "no-key" => Strings.Note_NoKey,
                "InvalidKey" => Strings.Note_InvalidKey,
                "RateLimited" => string.Format(CultureInfo.CurrentCulture, Strings.Note_RateLimited, source),
                _ => string.Format(CultureInfo.CurrentCulture, Strings.Note_Unavailable, source),
            };
        });
        return string.Join(' ', sentences.Distinct());
    }

    public static CheckStatus LevelOf(double confidence) =>
        confidence >= 0.8 ? CheckStatus.Ok : confidence >= 0.4 ? CheckStatus.Warn : CheckStatus.Bad;
}
