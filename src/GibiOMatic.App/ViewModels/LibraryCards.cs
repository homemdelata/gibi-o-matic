using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Core.Quality;
using GibiOMatic.Core.Text;
using GibiOMatic.Library;
using GibiOMatic.Library.Index;
using GibiOMatic.Library.Reading;

namespace GibiOMatic.App.ViewModels;

/// <summary>Shared look of covers without an image: a coloured placeholder chosen from the book id.</summary>
internal static class Placeholders
{
    // Colours of the prototype's placeholder covers.
    private static readonly Color[] Palette =
    [
        Color.Parse("#7A2E3B"), Color.Parse("#2F5D7C"), Color.Parse("#3B3B48"), Color.Parse("#8A6A1F"),
        Color.Parse("#3E7A52"), Color.Parse("#8C3A24"), Color.Parse("#5B4B8A"), Color.Parse("#2D4A3E"),
        Color.Parse("#6E1F24"), Color.Parse("#2B5C6B"), Color.Parse("#1F2A44"), Color.Parse("#4A5A2A"),
    ];

    public static Color ColorFor(string key)
    {
        var hash = 0;
        foreach (var c in key)
        {
            hash = unchecked((hash * 31) + c);
        }

        return Palette[(int)((uint)hash % Palette.Length)];
    }
}

public static class QualityText
{
    public static string BadgeOf(QualityBand band) => band switch
    {
        QualityBand.Ok => Strings.Quality_BadgeOk,
        QualityBand.Review => Strings.Quality_BadgeReview,
        _ => Strings.Quality_BadgeBad,
    };

    public static string Describe(QualityProblem p)
    {
        var detail = p.Detail ?? string.Empty;
        return p.Issue switch
        {
            QualityIssue.TitleAllLowercase => Strings.Issue_TitleAllLowercase,
            QualityIssue.TitleGluedWords => Strings.Issue_TitleGluedWords,
            QualityIssue.TitleFileName => Strings.Issue_TitleFileName,
            QualityIssue.TitleGeneric => Strings.Issue_TitleGeneric,
            QualityIssue.TitleExtraSpaces => Strings.Issue_TitleExtraSpaces,
            QualityIssue.TitleAllCaps => Strings.Issue_TitleAllCaps,
            QualityIssue.TitleJunk => Strings.Issue_TitleJunk,
            QualityIssue.TitleLeadingZero => Strings.Issue_TitleLeadingZero,
            QualityIssue.AuthorUnknown => detail.Length > 0 ? detail : Strings.Issue_AuthorUnknown,
            QualityIssue.AuthorSingleName => Format(Strings.Issue_AuthorSingleName, detail),
            QualityIssue.AuthorsGlued => Strings.Issue_AuthorsGlued,
            QualityIssue.AuthorBackslash => Strings.Issue_AuthorBackslash,
            QualityIssue.AuthorDuplicate => Strings.Issue_AuthorDuplicate,
            QualityIssue.AuthorsTooMany => Format(Strings.Issue_AuthorsTooMany, detail),
            QualityIssue.DateMissing => Strings.Issue_DateMissing,
            QualityIssue.DateHumble => Format(Strings.Issue_DateHumble, detail),
            QualityIssue.DateRepeated => Format(Strings.Issue_DateRepeated, detail, p.Count),
            QualityIssue.DateSameAsAdded => Format(Strings.Issue_DateSameAsAdded, detail),
            QualityIssue.SeriesInTitleOnly => Format(Strings.Issue_SeriesInTitleOnly, detail),
            QualityIssue.SeriesNumberMismatch => Format(Strings.Issue_SeriesNumberMismatch, detail.Split('|') is [var s, var t] ? s : detail, detail.Split('|') is [_, var t2] ? t2 : string.Empty),
            QualityIssue.InvalidIsbn => Format(Strings.Issue_InvalidIsbn, detail),
            QualityIssue.NoPublisher => Strings.Issue_NoPublisher,
            _ => p.Issue.ToString(),
        };
    }

    public static string DateOf(PartialDate date) => date switch
    {
        { Month: { } m, Day: { } d } => new DateOnly(date.Year, m, d).ToString("d", CultureInfo.CurrentCulture),
        { Month: { } m } => new DateOnly(date.Year, m, 1).ToString("Y", CultureInfo.CurrentCulture),
        _ => date.Year.ToString(CultureInfo.InvariantCulture),
    };

    public static string SeriesOf(SeriesInfo series) =>
        series.Number is { } n ? $"{series.Name} #{n}" : series.Name;

    public static string SizeOf(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.CurrentCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0", CultureInfo.CurrentCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0", CultureInfo.CurrentCulture) + " KB",
        _ => bytes.ToString(CultureInfo.CurrentCulture) + " B",
    };

    public static string FormatLabel(BookFormat format) => format.ToString().ToUpperInvariant();

    private static string Format(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);
}

/// <summary>Texts about a book's files on disk.</summary>
public static class BookFileText
{
    /// <summary>"Show in Explorer" on Windows, "Show in folder" elsewhere.</summary>
    public static string Reveal => OperatingSystem.IsWindows() ? Strings.Menu_RevealWindows : Strings.Menu_RevealOther;

    /// <summary>"The file X.pdf is no longer in the book folder…"</summary>
    public static string Missing(CatalogBook book) =>
        string.Format(CultureInfo.CurrentCulture, Strings.Missing_Help, book.MainFile?.Name ?? "—");
}

/// <summary>A book in the grid.</summary>
public sealed partial class BookCardViewModel : ViewModelBase
{
    private readonly LibraryInfo _library;
    private readonly ThumbnailCache _thumbnails;
    private readonly ICoverLoader _covers;
    private readonly Action<BookCardViewModel> _select;
    private readonly Action<BookCardViewModel>? _open;
    private readonly IBookActions? _actions;
    private bool _thumbnailRequested;
    private Bitmap? _thumbnail;

    public BookCardViewModel(CatalogBook book, QualityReport report, LibraryInfo library, ThumbnailCache thumbnails,
        ICoverLoader covers, Action<BookCardViewModel> select, Action<BookCardViewModel>? open = null, IBookActions? actions = null)
    {
        Book = book;
        _open = open;
        _actions = actions;
        Report = report;
        _library = library;
        _thumbnails = thumbnails;
        _covers = covers;
        _select = select;

        var m = book.Metadata;
        var parts = new List<string>
        {
            m.Publisher ?? Strings.Library_NoPublisher,
            m.Date?.Year.ToString(CultureInfo.InvariantCulture) ?? Strings.Library_NoDate,
            m.Series is { } s ? QualityText.SeriesOf(s) : Strings.Library_NoSeries,
        };
        MetaLine = string.Join(" · ", parts);
        PlaceholderColor = Placeholders.ColorFor(m.Id);
        SearchText = Normalize(string.Join(' ', [m.Title, m.Series?.Name, m.Publisher, .. m.Credits.Select(c => c.Name), .. m.Universes]));
    }

    public CatalogBook Book { get; }
    public QualityReport Report { get; }
    public string Id => Book.Metadata.Id;
    public string Title => Book.Metadata.Title;
    public string MetaLine { get; }
    public QualityBand Band => Report.Band;
    public string Badge => QualityText.BadgeOf(Report.Band);
    public Color PlaceholderColor { get; }

    /// <summary>Title and series without accents, in lower case, for searching.</summary>
    internal string SearchText { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Picked in selection mode (batch edit, fetch metadata).</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>The grid thumbnail, loaded the first time a visible card asks for it.</summary>
    public Bitmap? Thumbnail
    {
        get
        {
            if (!_thumbnailRequested)
            {
                _thumbnailRequested = true;
                _ = LoadThumbnailAsync();
            }

            return _thumbnail;
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    /// <summary>How far the book was read; null if it was never opened or marked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReading), nameof(IsRead), nameof(Progress), nameof(ProgressText), nameof(ReadMenuText))]
    public partial BookReading? Reading { get; set; }

    /// <summary>In the list of a Kindle destination (it is prepared and offered when the Kindle is connected).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KindleMenuText))]
    public partial bool IsInKindleList { get; set; }

    /// <summary>The right-click menu is offered (the Library's cards, not the Review's).</summary>
    public bool HasMenu => _actions is not null;

    /// <summary>The main original is not on disk (deleted or moved outside the app).</summary>
    [ObservableProperty]
    public partial bool IsFileMissing { get; set; }

    public string RevealText => BookFileText.Reveal;

    [RelayCommand]
    private void Reveal() => _actions?.Reveal(this);

    [RelayCommand]
    private void AskRemove() => _actions?.AskRemove(this);

    public string KindleMenuText => IsInKindleList ? Strings.Menu_RemoveKindle : Strings.Menu_AddKindle;

    public string ReadMenuText => IsRead ? Strings.Reading_MarkUnread : Strings.Reading_MarkRead;

    [RelayCommand]
    private Task ToggleKindleAsync() => _actions?.SetKindleAsync([this], !IsInKindleList) ?? Task.CompletedTask;

    [RelayCommand]
    private Task ToggleReadAsync() => _actions?.SetReadAsync(this, !IsRead) ?? Task.CompletedTask;

    [RelayCommand]
    private Task ClearProgressAsync() => _actions?.ClearProgressAsync(this) ?? Task.CompletedTask;

    [RelayCommand]
    private void Edit() => _actions?.Edit(this);

    [RelayCommand]
    private void Review() => _actions?.Review(this);

    public bool IsReading => Reading?.IsReading == true;

    public bool IsRead => Reading?.IsRead == true;

    /// <summary>From 0 to 1, for the bar at the bottom of the cover.</summary>
    public double Progress => Reading?.Fraction ?? 0;

    /// <summary>"42% read".</summary>
    public string ProgressText => string.Format(CultureInfo.CurrentCulture, Strings.Reading_Percent, Progress.ToString("0%", CultureInfo.CurrentCulture));

    [RelayCommand]
    private void Select() => _select(this);

    /// <summary>Double click: opens the book in the reader.</summary>
    [RelayCommand]
    private void Open() => _open?.Invoke(this);

    private async Task LoadThumbnailAsync()
    {
        var path = await _thumbnails.GetBookThumbnailAsync(_library, Book);
        if (path is null)
        {
            return;
        }

        _thumbnail = await _covers.LoadAsync(path, Conversion.Images.CoverImages.ThumbnailWidth);
        OnPropertyChanged(nameof(Thumbnail));
        OnPropertyChanged(nameof(HasThumbnail));
    }

    internal static string Normalize(string text) => TitleParser.RemoveDiacritics(text).ToLowerInvariant();
}

/// <summary>
/// Picks the covers of a series or universe card: the first volume in front and the next ones behind,
/// in reading order. The books come already sorted; volumes without a number go after the numbered ones.
/// </summary>
public static class CoverStack
{
    /// <summary>At most two covers behind the front one.</summary>
    public const int MaxBehind = 2;

    public static (T Front, IReadOnlyList<T> Behind) Pick<T>(IReadOnlyList<T> books, Func<T, string?> number)
    {
        var ordered = books.OrderBy(b => string.IsNullOrWhiteSpace(number(b)) ? 1 : 0).ToList();
        return (ordered[0], ordered.Skip(1).Take(MaxBehind).ToList());
    }
}

/// <summary>A card that stands for several books (series, universe): a stack of covers that opens.</summary>
public abstract partial class StackCardViewModel : ViewModelBase
{
    protected StackCardViewModel(string name, IReadOnlyList<BookCardViewModel> books)
    {
        Name = name;
        Books = books;
        (Cover, var behind) = CoverStack.Pick(books, b => b.Book.Metadata.Series?.Number);
        Behind1 = behind.ElementAtOrDefault(0);
        Behind2 = behind.ElementAtOrDefault(1);
        Band = books.Min(b => b.Band);
    }

    public string Name { get; }
    public IReadOnlyList<BookCardViewModel> Books { get; }

    /// <summary>The first volume, whose cover is in front.</summary>
    public BookCardViewModel Cover { get; }

    /// <summary>The next volumes, drawn behind the front cover (null when there are fewer books).</summary>
    public BookCardViewModel? Behind1 { get; }

    public BookCardViewModel? Behind2 { get; }

    public IReadOnlyList<BookCardViewModel> BehindCovers => new[] { Behind1, Behind2 }.OfType<BookCardViewModel>().ToList();

    /// <summary>The worst quality among the books.</summary>
    public QualityBand Band { get; }

    public string Badge => QualityText.BadgeOf(Band);

    /// <summary>Shown in the cover's bottom-left corner.</summary>
    public string BookCount => Books.Count.ToString(CultureInfo.CurrentCulture);

    /// <summary>"12 books", for screen readers and the tooltip of the counter.</summary>
    public string BookCountText => Books.Count == 1 ? Strings.Library_SeriesBooksOne : string.Format(CultureInfo.CurrentCulture, Strings.Library_SeriesBooksMany, Books.Count);

    public abstract string MetaLine { get; }

    [RelayCommand]
    private void Open() => OnOpen();

    protected abstract void OnOpen();
}

/// <summary>A series in the grid (Series view): opens to show its books.</summary>
public sealed class SeriesCardViewModel : StackCardViewModel
{
    private readonly Action<SeriesCardViewModel> _open;

    public SeriesCardViewModel(string name, IReadOnlyList<BookCardViewModel> books, Action<SeriesCardViewModel> open)
        : base(name, books)
    {
        _open = open;
        var publisher = books.Select(b => b.Book.Metadata.Publisher).FirstOrDefault(p => p is not null);
        string[] parts = publisher is null ? [Strings.Library_SeriesKind, BookCountText] : [Strings.Library_SeriesKind, BookCountText, publisher];
        MetaLine = string.Join(" · ", parts);
    }

    /// <summary>"Series · 3 books · Publisher".</summary>
    public override string MetaLine { get; }

    protected override void OnOpen() => _open(this);
}

/// <summary>A universe in the grid (Universos view): opens to show its series.</summary>
public sealed class UniverseCardViewModel : StackCardViewModel
{
    private readonly Action<UniverseCardViewModel> _open;

    public UniverseCardViewModel(string name, IReadOnlyList<BookCardViewModel> books, Action<UniverseCardViewModel> open)
        : base(name, books)
    {
        _open = open;
        var series = books.Select(b => b.Book.Metadata.Series?.Name ?? b.Title).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var seriesText = series == 1 ? Strings.Library_UniverseSeriesOne : string.Format(CultureInfo.CurrentCulture, Strings.Library_UniverseSeriesMany, series);
        MetaLine = string.Format(CultureInfo.CurrentCulture, Strings.Library_UniverseSummary, seriesText, BookCountText);
    }

    /// <summary>"3 series · 9 books".</summary>
    public override string MetaLine { get; }

    protected override void OnOpen() => _open(this);
}

/// <summary>One row of the virtualized grid; the last row may have fewer cards than columns.</summary>
public sealed record CardRow(IReadOnlyList<ViewModelBase> Cards, int Columns);

public enum CheckStatus
{
    Ok,
    Warn,
    Bad,
}

public sealed record CheckRow(string Field, string Value, CheckStatus Status)
{
    /// <summary>Resource key of the icon drawn in the status colour.</summary>
    public string Icon => Status switch
    {
        CheckStatus.Ok => "Icon.Check",
        CheckStatus.Warn => "Icon.Alert",
        _ => "Icon.Close",
    };
}

public sealed record FileRow(string Format, string Label, string Size, string Name, string? DownloadedAs, IRelayCommand Open);

/// <summary>The detail panel of the selected book.</summary>
public sealed partial class BookDetailsViewModel : ViewModelBase
{
    private readonly Action<BookFile>? _open;
    private readonly IReadingActions? _reading;
    private readonly IBookFileActions? _files;

    public BookDetailsViewModel(BookCardViewModel card, LibraryInfo library, ICoverLoader covers, Action<BookFile>? open = null,
        IReadingActions? reading = null, IBookFileActions? files = null)
    {
        Card = card;
        _open = open;
        _reading = reading;
        _files = files;
        MissingText = BookFileText.Missing(card.Book);
        card.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BookCardViewModel.Reading))
            {
                UpdateReading();
            }
        };
        UpdateReading();
        var m = card.Book.Metadata;
        var report = card.Report;

        var creative = m.Credits.Where(c => c.Role is not (CreditRole.Editor or CreditRole.Other)).Select(c => c.Name).Distinct().ToList();
        CreditsLine = string.Join(" · ", new[]
        {
            creative.Count > 0 ? string.Join(", ", creative) : Strings.Issue_AuthorUnknown,
            m.Publisher ?? Strings.Library_NoPublisher,
        });

        var authors = QualityAnalyzer.AuthorsOf(m);
        Checks =
        [
            Check(Strings.Details_Title, report, QualityField.Title, m.Title),
            Check(Strings.Details_Authors, report, QualityField.Authors, string.Join(", ", authors)),
            Check(Strings.Details_Date, report, QualityField.Date, m.Date is { } d ? QualityText.DateOf(d) : string.Empty),
            Check(Strings.Details_Series, report, QualityField.Series, m.Series is { } s ? QualityText.SeriesOf(s) : Strings.Details_NoSeriesOk),
        ];

        Files = m.Files.Select(f => new FileRow(
            QualityText.FormatLabel(f.Format),
            f.Role == FileRole.Original ? Strings.Details_FileOriginal : Strings.Details_FileExtra,
            QualityText.SizeOf(f.Bytes),
            f.Name,
            f.DownloadedAs,
            new RelayCommand(() => _open?.Invoke(f)))).ToList();

        ScoreLine = string.Format(CultureInfo.CurrentCulture, Strings.Details_Score, report.Score, QualityText.BadgeOf(report.Band));
        Universes = m.Universes;
        FolderPath = Path.Combine(library.RootPath, card.Book.Folder);
        _ = LoadCoverAsync(Path.Combine(FolderPath, LibraryLayout.CoverFileName), covers);
    }

    public BookCardViewModel Card { get; }
    public string Title => Card.Title;

    /// <summary>Opens the main file in the reader.</summary>
    [RelayCommand]
    private void Open()
    {
        if (Card.Book.MainFile is { } file)
        {
            _open?.Invoke(file);
        }
    }
    public string CreditsLine { get; }

    /// <summary>Shown while <see cref="BookCardViewModel.IsFileMissing"/>.</summary>
    public string MissingText { get; }

    public string RevealText => BookFileText.Reveal;

    [RelayCommand]
    private void Reveal() => _files?.Reveal(Card);

    [RelayCommand]
    private void AskRemove() => _files?.AskRemove(Card);

    /// <summary>Universes the book belongs to (shown as pills under the credits).</summary>
    public IReadOnlyList<string> Universes { get; }

    /// <summary>"Reading · 34%", "Read on 10/18/2026" or "Not read".</summary>
    [ObservableProperty]
    public partial string ReadingText { get; private set; } = string.Empty;

    [RelayCommand]
    private Task MarkReadAsync() => _reading?.SetReadAsync(Card, true) ?? Task.CompletedTask;

    [RelayCommand]
    private Task MarkUnreadAsync() => _reading?.SetReadAsync(Card, false) ?? Task.CompletedTask;

    [RelayCommand]
    private Task ClearProgressAsync() => _reading?.ClearProgressAsync(Card) ?? Task.CompletedTask;

    private void UpdateReading() => ReadingText = Card.Reading switch
    {
        { ReadAt: { } at } => string.Format(CultureInfo.CurrentCulture, Strings.Reading_ReadOn, at.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)),
        { IsReading: true } r => string.Format(CultureInfo.CurrentCulture, Strings.Reading_InProgress, r.Fraction.ToString("0%", CultureInfo.CurrentCulture)),
        _ => Strings.Reading_NotRead,
    };

    public IReadOnlyList<CheckRow> Checks { get; }
    public IReadOnlyList<FileRow> Files { get; }
    public string ScoreLine { get; }
    public string FolderPath { get; }

    [ObservableProperty]
    public partial Bitmap? Cover { get; private set; }

    /// <summary>The book's copy in each destination (filled after the panel opens).</summary>
    public System.Collections.ObjectModel.ObservableCollection<CopyRow> Copies { get; } = [];

    private static CheckRow Check(string field, QualityReport report, QualityField qualityField, string value)
    {
        if (report.WorstIn(qualityField) is not { } problem)
        {
            return new CheckRow(field, value, CheckStatus.Ok);
        }

        return new CheckRow(field, QualityText.Describe(problem), problem.Severity >= 3 ? CheckStatus.Bad : CheckStatus.Warn);
    }

    private async Task LoadCoverAsync(string path, ICoverLoader covers)
    {
        // A missing cover loads as null.
        Cover = await covers.LoadAsync(path, 300);
    }
}

/// <summary>What the detail panel can do to a book's reading status.</summary>
public interface IReadingActions
{
    Task SetReadAsync(BookCardViewModel card, bool read);

    Task ClearProgressAsync(BookCardViewModel card);
}

/// <summary>Where a book is on disk, and taking it out of the library.</summary>
public interface IBookFileActions
{
    /// <summary>Shows the main original (or the book folder) in the file manager.</summary>
    void Reveal(BookCardViewModel card);

    /// <summary>Asks whether to remove the book from the library.</summary>
    void AskRemove(BookCardViewModel card);
}

/// <summary>What the right-click menu of a book in the Library does.</summary>
public interface IBookActions : IReadingActions, IBookFileActions
{
    /// <summary>Puts books in the Kindle's list (they are prepared at once) or takes them out.</summary>
    Task SetKindleAsync(IReadOnlyList<BookCardViewModel> cards, bool inList);

    void Edit(BookCardViewModel card);

    void Review(BookCardViewModel card);
}

/// <summary>A book in "Continue reading": opens the reader where it was left.</summary>
public sealed partial class ContinueReadingViewModel(BookCardViewModel card, Action<BookCardViewModel> open) : ViewModelBase
{
    public BookCardViewModel Card { get; } = card;

    [RelayCommand]
    private void Open() => open(Card);
}

/// <summary>A book's copy in one destination: "Home Komga · Up to date".</summary>
public sealed record CopyRow(string Destination, string State, StatusKind Kind, string? Path);
