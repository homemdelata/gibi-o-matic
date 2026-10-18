using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
using GibiOMatic.Conversion;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Text;
using GibiOMatic.Destinations;
using GibiOMatic.Destinations.Komga;
using GibiOMatic.Library;
using GibiOMatic.Library.Editing;
using GibiOMatic.Library.Index;
using GibiOMatic.Metadata.Identification;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.App.ViewModels;

public sealed record RoleOption(CreditRole Role, string Label);

/// <param name="Kind"><c>null</c> = keep each book's kind (batch).</param>
public sealed record KindOption(BookKind? Kind, string Label);

public sealed partial class CreditRowViewModel(RoleOption role, string name, Action<CreditRowViewModel> remove) : ViewModelBase
{
    /// <summary>
    /// Ignores <c>null</c>: a ComboBox whose items go away (the page leaving the screen) writes it
    /// back, and a credit always has a role.
    /// </summary>
    public RoleOption Role
    {
        get;
        set
        {
            if (value is not null)
            {
                SetProperty(ref field, value);
            }
        }
    } = role;

    [ObservableProperty]
    public partial string Name { get; set; } = name;

    [RelayCommand]
    private void Remove() => remove(this);
}

public sealed partial class HistoryRowViewModel(HistoryEntry entry, Func<HistoryEntry, Task> restore) : ViewModelBase
{
    public HistoryEntry Entry { get; } = entry;
    public string When => Entry.SavedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string Title => Entry.Metadata.Title;

    [RelayCommand]
    private Task Restore() => restore(Entry);
}

/// <summary>
/// The Editor (SPEC §7): every field of one book, or the fields in common of a batch (empty fields
/// change nothing; "Numerar em sequência" numbers the batch). Saving keeps the previous version in
/// the history, so it can be undone.
/// </summary>
public sealed partial class EditorPageViewModel : PageViewModel
{
    private readonly LibrarySession _session;
    private readonly BookEditor _editor;
    private readonly ICoverLoader _covers;
    private readonly IFilePicker _filePicker;
    private readonly ILogger<EditorPageViewModel> _logger;
    private IReadOnlyList<string> _bookIds = [];
    private bool _loading;
    private List<CatalogBook> _books = [];

    public EditorPageViewModel(LibrarySession session, BookEditor editor, ICoverLoader covers, IFilePicker filePicker, ILogger<EditorPageViewModel> logger)
    {
        _session = session;
        _editor = editor;
        _covers = covers;
        _filePicker = filePicker;
        _logger = logger;
        Subtitle = Strings.Editor_Subtitle;
        Roles =
        [
            new(CreditRole.Writer, Strings.Role_Writer), new(CreditRole.Artist, Strings.Role_Artist), new(CreditRole.Inker, Strings.Role_Inker),
            new(CreditRole.Colorist, Strings.Role_Colorist), new(CreditRole.Letterer, Strings.Role_Letterer), new(CreditRole.Cover, Strings.Role_Cover),
            new(CreditRole.Editor, Strings.Role_Editor), new(CreditRole.Translator, Strings.Role_Translator), new(CreditRole.Other, Strings.Role_Other),
        ];
        SelectedKind = Kinds[0];
    }

    public override string Title => Strings.Nav_Editor;

    public IReadOnlyList<RoleOption> Roles { get; }

    public IReadOnlyList<KindOption> Kinds { get; } =
    [
        new(null, Strings.Kind_Keep), new(BookKind.Tpb, Strings.Kind_Tpb), new(BookKind.Issue, Strings.Kind_Issue),
        new(BookKind.Omnibus, Strings.Kind_Omnibus), new(BookKind.Artbook, Strings.Kind_Artbook), new(BookKind.Book, Strings.Kind_Book),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSingle), nameof(IsEmpty))]
    public partial bool HasBooks { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSingle), nameof(KindChoices))]
    public partial bool IsBatch { get; private set; }

    /// <summary>"Manter" only makes sense in a batch.</summary>
    public IReadOnlyList<KindOption> KindChoices => IsBatch ? Kinds : Kinds.Skip(1).ToList();

    public bool IsSingle => HasBooks && !IsBatch;

    public bool IsEmpty => !HasBooks;

    [ObservableProperty]
    public partial string? Heading { get; private set; }

    [ObservableProperty]
    public partial string? BatchNotice { get; private set; }

    // Fields
    [ObservableProperty]
    public partial string? BookTitle { get; set; }

    [ObservableProperty]
    public partial string? SeriesName { get; set; }

    [ObservableProperty]
    public partial string? Number { get; set; }

    [ObservableProperty]
    public partial bool NumberInSequence { get; set; }

    [ObservableProperty]
    public partial string? DateText { get; set; }

    [ObservableProperty]
    public partial string? Publisher { get; set; }

    [ObservableProperty]
    public partial string? Imprint { get; set; }

    [ObservableProperty]
    public partial string? Isbn { get; set; }

    [ObservableProperty]
    public partial string? Language { get; set; }

    [ObservableProperty]
    public partial string? Summary { get; set; }

    /// <summary>Ignores <c>null</c>, which the ComboBox writes back when the page leaves the screen.</summary>
    public KindOption SelectedKind
    {
        get;
        set
        {
            if (value is not null && SetProperty(ref field, value))
            {
                UpdatePreview();
            }
        }
    } = null!;

    /// <summary>Three states in a batch: <c>null</c> keeps each book as it is.</summary>
    [ObservableProperty]
    public partial bool? OneShot { get; set; }

    [ObservableProperty]
    public partial bool MarkReviewed { get; set; } = true;

    public ObservableCollection<CreditRowViewModel> Credits { get; } = [];

    public ObservableCollection<HistoryRowViewModel> History { get; } = [];

    [ObservableProperty]
    public partial bool HasNoHistory { get; private set; }

    [ObservableProperty]
    public partial Bitmap? Cover { get; private set; }

    [ObservableProperty]
    public partial string? KomgaPreview { get; private set; }

    [ObservableProperty]
    public partial string? KindlePreview { get; private set; }

    [ObservableProperty]
    public partial string? Message { get; private set; }

    [ObservableProperty]
    public partial bool MessageIsError { get; private set; }

    public override void SetBooks(IReadOnlyList<string> bookIds) => _bookIds = bookIds;

    public override Task OnNavigatedToAsync(CancellationToken ct = default) => LoadAsync(ct);

    private async Task LoadAsync(CancellationToken ct)
    {
        Message = null;
        if (_session.Index is not { } index || _bookIds.Count == 0)
        {
            HasBooks = false;
            return;
        }

        var all = (await index.GetBooksAsync(ct)).ToDictionary(b => b.Metadata.Id);
        _books = _bookIds.Where(all.ContainsKey).Select(id => all[id])
            .OrderBy(b => b.Metadata.Series?.Name ?? b.Metadata.Title, NaturalStringComparer.Instance)
            .ThenBy(b => b.Metadata.Series?.Number ?? string.Empty, NaturalStringComparer.Instance)
            .ThenBy(b => b.Metadata.Title, NaturalStringComparer.Instance)
            .ToList();
        HasBooks = _books.Count > 0;
        IsBatch = _books.Count > 1;
        if (!HasBooks)
        {
            return;
        }

        _loading = true;
        try
        {
            if (IsBatch)
            {
                LoadBatch();
            }
            else
            {
                await LoadSingleAsync(_books[0], ct);
            }
        }
        finally
        {
            _loading = false;
        }

        UpdatePreview();
    }

    private async Task LoadSingleAsync(CatalogBook book, CancellationToken ct)
    {
        var m = book.Metadata;
        Heading = m.Title;
        BatchNotice = null;
        BookTitle = m.Title;
        SeriesName = m.Series?.Name;
        Number = m.Series?.Number;
        NumberInSequence = false;
        DateText = Proposal.DateText(m.Date);
        Publisher = m.Publisher;
        Imprint = m.Imprint;
        Isbn = m.Identifiers.Isbn;
        Language = m.Language;
        Summary = m.Summary;
        SelectedKind = Kinds.First(k => k.Kind == m.Kind);
        OneShot = m.OneShot ?? false;
        MarkReviewed = true;
        Credits.Clear();
        foreach (var credit in m.Credits)
        {
            Credits.Add(new CreditRowViewModel(Roles.First(r => r.Role == credit.Role), credit.Name, RemoveCredit));
        }

        await LoadHistoryAsync(m.Id, ct);
        Cover = null;
        if (_session.Current is { } library)
        {
            var path = Path.Combine(library.RootPath, book.Folder, LibraryLayout.CoverFileName);
            Cover = File.Exists(path) ? await _covers.LoadAsync(path, 420, ct) : null;
        }
    }

    /// <summary>A batch starts with the values every book shares; the others stay empty ("vários").</summary>
    private void LoadBatch()
    {
        var first = _books[0].Metadata;
        Heading = string.Format(CultureInfo.CurrentCulture, Strings.Editor_BatchHeading, _books.Count);
        BatchNotice = string.Format(CultureInfo.CurrentCulture, Strings.Editor_BatchNotice, _books.Count);
        string? Common(Func<BookMetadata, string?> value) => _books.Select(b => value(b.Metadata)).Distinct().Count() == 1 ? value(first) : null;
        BookTitle = null;
        SeriesName = Common(m => m.Series?.Name);
        Number = null;
        NumberInSequence = false;
        DateText = Common(m => Proposal.DateText(m.Date));
        Publisher = Common(m => m.Publisher);
        Imprint = Common(m => m.Imprint);
        Isbn = null;
        Language = Common(m => m.Language);
        Summary = null;
        SelectedKind = Kinds[0];
        OneShot = _books.Select(b => b.Metadata.OneShot ?? false).Distinct().Count() == 1 ? first.OneShot ?? false : null;
        MarkReviewed = false;
        Credits.Clear();
        History.Clear();
        Cover = null;
    }

    private async Task LoadHistoryAsync(string bookId, CancellationToken ct)
    {
        History.Clear();
        if (_session.Current is not { } library)
        {
            return;
        }

        foreach (var entry in (await _editor.GetHistoryAsync(library, bookId, ct)).Take(8))
        {
            History.Add(new HistoryRowViewModel(entry, RestoreAsync));
        }

        HasNoHistory = History.Count == 0;
    }

    partial void OnBookTitleChanged(string? value) => UpdatePreview();

    partial void OnSeriesNameChanged(string? value) => UpdatePreview();

    partial void OnNumberChanged(string? value) => UpdatePreview();

    partial void OnDateTextChanged(string? value) => UpdatePreview();

    partial void OnOneShotChanged(bool? value) => UpdatePreview();


    /// <summary>Names the destinations will give the copy (SPEC §7, "prévia dos nomes").</summary>
    private void UpdatePreview()
    {
        if (_loading)
        {
            return;
        }

        if (!HasBooks)
        {
            KomgaPreview = KindlePreview = null;
            return;
        }

        var preview = Apply(_books[0].Metadata, 0, out _);
        // The first Komga destination's names, or the defaults when there is none yet.
        KomgaPreview = _session.Destinations?.Destinations.FirstOrDefault(d => d.Config.Type == KomgaDestinationType.TypeId)?.Config is { } komga
            ? OutputNames.KomgaPath(preview, komga.OneShotsFolder, komga.SeriesTemplate, komga.OneShotTemplate) + ".cbz"
            : OutputNames.Komga(preview);
        KindlePreview = OutputNames.Kindle(preview);
    }

    /// <summary>The book with the form applied (empty fields change nothing in a batch).</summary>
    private BookMetadata Apply(BookMetadata book, int position, out string? error)
    {
        error = null;
        var date = book.Date;
        if (!string.IsNullOrWhiteSpace(DateText))
        {
            date = ParseDate(DateText);
            if (date is null)
            {
                error = Strings.Editor_InvalidDate;
                date = book.Date;
            }
        }
        else if (!IsBatch)
        {
            date = null;
        }

        string? Text(string? value, string? current) => IsBatch ? (string.IsNullOrWhiteSpace(value) ? current : value.Trim()) : Clean(value);

        var seriesName = Text(SeriesName, book.Series?.Name);
        var number = NumberInSequence && int.TryParse(Number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)
            ? (start + position).ToString(CultureInfo.InvariantCulture)
            : Text(Number, book.Series?.Number);

        var result = book with
        {
            Title = IsBatch ? book.Title : Clean(BookTitle) ?? book.Title,
            Series = seriesName is null ? null : new SeriesInfo(seriesName, number, book.Series?.Volume),
            Date = date,
            Publisher = Text(Publisher, book.Publisher),
            Imprint = Text(Imprint, book.Imprint),
            Language = Text(Language, book.Language),
            Summary = Text(Summary, book.Summary),
            Identifiers = book.Identifiers with { Isbn = Text(Isbn is null ? null : Core.Text.Isbn.Normalize(Isbn), book.Identifiers.Isbn) },
            Kind = SelectedKind.Kind ?? book.Kind,
            OneShot = OneShot ?? book.OneShot,
        };

        if (!IsBatch)
        {
            result = result with
            {
                Credits = Credits.Where(c => !string.IsNullOrWhiteSpace(c.Name))
                    .Select(c => new Credit(c.Role.Role, c.Name.Trim())).Distinct().OrderBy(c => c.Role).ToList(),
            };
        }

        return result;
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static PartialDate? ParseDate(string text)
    {
        var parts = text.Trim().Split('-');
        if (parts.Length is < 1 or > 3 || !parts.All(p => p.All(char.IsAsciiDigit) && p.Length > 0))
        {
            return null;
        }

        var year = int.Parse(parts[0], CultureInfo.InvariantCulture);
        int? month = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : null;
        int? day = parts.Length > 2 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : null;
        if (year is < 1800 or > 2200 || month is < 1 or > 12 || (day is not null && (month is null || day < 1 || day > DateTime.DaysInMonth(year, month.Value))))
        {
            return null;
        }

        return new PartialDate(year, month, day);
    }

    [RelayCommand]
    private void AddCredit() => Credits.Add(new CreditRowViewModel(Roles[0], string.Empty, RemoveCredit));

    private void RemoveCredit(CreditRowViewModel row) => Credits.Remove(row);

    [RelayCommand]
    private async Task SaveAsync(CancellationToken ct)
    {
        if (_session.Current is not { } library || !HasBooks)
        {
            return;
        }

        Apply(_books[0].Metadata, 0, out var error);
        if (error is not null)
        {
            SetMessage(error, isError: true);
            return;
        }

        try
        {
            for (var i = 0; i < _books.Count; i++)
            {
                var position = i;
                await _editor.UpdateAsync(library, _books[i].Metadata.Id, current =>
                {
                    var updated = Apply(current, position, out _);
                    return MarkReviewed
                        ? updated with { Review = new ReviewInfo { Status = ReviewStatus.Approved, ApprovedAt = DateTimeOffset.UtcNow, MatchedBy = MatchedBy.Manual } }
                        : updated;
                }, ct);

                if (MarkReviewed && _session.Identification is { } queue)
                {
                    await queue.MarkApprovedAsync(_books[i].Metadata.Id, ct);
                }
            }

            SetMessage(IsBatch ? string.Format(CultureInfo.CurrentCulture, Strings.Editor_SavedMany, _books.Count) : Strings.Editor_Saved, isError: false);
            var message = Message;
            await LoadAsync(ct);
            Message = message;
        }
        catch (Exception ex) when (LibrarySession.IsExpectedFailure(ex))
        {
            _logger.LogError(ex, "Could not save books");
            SetMessage(string.Format(CultureInfo.CurrentCulture, Strings.Error_FileAccess, ex.Message), isError: true);
        }
    }

    /// <summary>Leaves without saving; the next visit loads the books again.</summary>
    [RelayCommand]
    private void Discard() => RequestNavigation(NavPage.Library);

    [RelayCommand]
    private void SearchOnline()
    {
        if (HasBooks)
        {
            RequestNavigation(NavPage.Review, [_books[0].Metadata.Id]);
        }
    }

    [RelayCommand]
    private void BackToLibrary() => RequestNavigation(NavPage.Library);

    [RelayCommand]
    private async Task ChangeCoverAsync(CancellationToken ct)
    {
        if (_session.Current is not { } library || !IsSingle)
        {
            return;
        }

        var image = await _filePicker.PickImageAsync(Strings.Picker_CoverTitle, ct);
        if (image is null)
        {
            return;
        }

        if (await _editor.SetCoverAsync(library, _books[0].Metadata.Id, image, ct))
        {
            await LoadAsync(ct);
            SetMessage(Strings.Editor_CoverChanged, isError: false);
        }
        else
        {
            SetMessage(Strings.Editor_CoverInvalid, isError: true);
        }
    }

    [RelayCommand]
    private async Task UseFirstPageAsync(CancellationToken ct)
    {
        if (_session.Current is { } library && IsSingle && await _editor.UseFirstPageAsync(library, _books[0].Metadata.Id, ct))
        {
            await LoadAsync(ct);
            SetMessage(Strings.Editor_CoverChanged, isError: false);
        }
    }

    private async Task RestoreAsync(HistoryEntry entry)
    {
        if (_session.Current is not { } library || !IsSingle)
        {
            return;
        }

        await _editor.RestoreAsync(library, _books[0].Metadata.Id, entry);
        await LoadAsync(CancellationToken.None);
        SetMessage(string.Format(CultureInfo.CurrentCulture, Strings.Editor_Restored, entry.SavedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)), isError: false);
    }

    private void SetMessage(string message, bool isError)
    {
        MessageIsError = isError;
        Message = message;
    }
}
