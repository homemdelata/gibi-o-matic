using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GibiOMatic.App.Resources;
using GibiOMatic.Conversion;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Text;
using GibiOMatic.Library.Identification;

namespace GibiOMatic.App.ViewModels;

/// <summary>A book the metadata can be copied from.</summary>
public sealed class PasteSourceViewModel(BookMetadata book)
{
    public BookMetadata Book { get; } = book;
    public string Title => Book.Title;

    /// <summary>"Image Comics · 2012 · Saga #1".</summary>
    public string Line { get; } = string.Join(" · ", new[]
    {
        book.Publisher,
        book.Date?.Year.ToString(CultureInfo.InvariantCulture),
        book.Series is { } s ? QualityText.SeriesOf(s) : null,
    }.Where(p => !string.IsNullOrWhiteSpace(p)));

    internal string SearchText { get; } = BookCardViewModel.Normalize(string.Join(' ', book.Title, book.Series?.Name, book.Publisher));
}

/// <summary>One checkbox of the paste panel.</summary>
public sealed partial class PasteFieldOption(PasteField field, string label, bool isChecked) : ViewModelBase
{
    public PasteField Field { get; } = field;
    public string Label { get; } = label;

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = isChecked;
}

/// <summary>
/// The Editor's "Paste metadata…" panel (SPEC §7): the book to copy from (the copied one chosen
/// already, or searched in the library) and the fields to copy. Pasting only fills the form.
/// </summary>
public sealed partial class PastePromptViewModel(Action<BookMetadata, IReadOnlyList<PasteField>> paste) : ViewModelBase
{
    /// <summary>Most books listed at once; the search narrows the rest.</summary>
    public const int MaxResults = 60;

    private List<PasteSourceViewModel> _all = [];

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    /// <summary>"Paste metadata into “Saga, Vol. 2”".</summary>
    [ObservableProperty]
    public partial string? Heading { get; private set; }

    [ObservableProperty]
    public partial string? SearchText { get; set; }

    public ObservableCollection<PasteSourceViewModel> Results { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PasteCommand))]
    public partial PasteSourceViewModel? Source { get; set; }

    [ObservableProperty]
    public partial bool HasNoResults { get; private set; }

    public IReadOnlyList<PasteFieldOption> Fields { get; private set; } = [];

    /// <summary>Opens the panel for <paramref name="target"/>, listing <paramref name="books"/> (the target excluded).</summary>
    public void Open(BookMetadata target, IEnumerable<BookMetadata> books, BookMetadata? copied, IReadOnlyList<PasteField> checkedFields)
    {
        Heading = string.Format(CultureInfo.CurrentCulture, Strings.Paste_Heading, target.Title);
        var series = SeriesGrouping.SeriesKey(target.Series?.Name ?? TitleParser.Parse(target.Title).Name);
        _all = books.Where(b => b.Id != target.Id)
            .OrderBy(b => series is not null && SeriesGrouping.SeriesKey(b.Series?.Name ?? TitleParser.Parse(b.Title).Name) == series ? 0 : 1)
            .ThenBy(b => b.Title, NaturalStringComparer.Instance)
            .Select(b => new PasteSourceViewModel(b))
            .ToList();
        PasteFieldOption Option(PasteField field, string label) => new(field, label, checkedFields.Contains(field));
        Fields =
        [
            Option(PasteField.Series, Strings.Paste_FieldSeries), Option(PasteField.Kind, Strings.Field_Kind),
            Option(PasteField.Publisher, Strings.Paste_FieldPublisher), Option(PasteField.Credits, Strings.Editor_Credits),
            Option(PasteField.Language, Strings.Editor_Language), Option(PasteField.Universes, Strings.Editor_Universes),
            Option(PasteField.OneShot, Strings.Paste_FieldOneShot), Option(PasteField.Title, Strings.Field_Title),
            Option(PasteField.Date, Strings.Field_Date), Option(PasteField.Summary, Strings.Field_Summary),
        ];
        OnPropertyChanged(nameof(Fields));
        SearchText = null;
        Filter();
        Source = copied is null || copied.Id == target.Id ? null : _all.FirstOrDefault(s => s.Book.Id == copied.Id) ?? new PasteSourceViewModel(copied);
        if (Source is not null && !Results.Contains(Source))
        {
            Results.Insert(0, Source);
        }

        IsOpen = true;
    }

    partial void OnSearchTextChanged(string? value) => Filter();

    private void Filter()
    {
        var words = BookCardViewModel.Normalize(SearchText ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var chosen = Source;
        Results.Clear();
        foreach (var source in _all.Where(s => words.All(w => s.SearchText.Contains(w, StringComparison.Ordinal))).Take(MaxResults))
        {
            Results.Add(source);
        }

        HasNoResults = Results.Count == 0;
        // The ListBox clears its selection when the items change; the chosen book stays chosen.
        Source = chosen;
    }

    [RelayCommand(CanExecute = nameof(CanPaste))]
    private void Paste()
    {
        if (Source is not { } source)
        {
            return;
        }

        IsOpen = false;
        paste(source.Book, Fields.Where(f => f.IsChecked).Select(f => f.Field).ToList());
    }

    private bool CanPaste() => Source is not null;

    [RelayCommand]
    public void Cancel() => IsOpen = false;
}
