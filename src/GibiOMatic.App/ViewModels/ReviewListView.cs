using System.Globalization;
using GibiOMatic.App.Resources;
using GibiOMatic.Core.Settings;
using GibiOMatic.Library.Identification;

namespace GibiOMatic.App.ViewModels;

public sealed record ReviewSortOption(ReviewSort Sort, string DisplayName);

/// <summary>
/// The order and the text filter of the Review's lists, by book and by series (SPEC §7). Skipped
/// books stay at the end whatever the order: "Skip" means "later".
/// </summary>
public static class ReviewListView
{
    public static IReadOnlyList<ReviewSortOption> Options { get; } =
    [
        new(ReviewSort.TitleAscending, Strings.Review_SortTitleAsc),
        new(ReviewSort.TitleDescending, Strings.Review_SortTitleDesc),
        new(ReviewSort.NewestFirst, Strings.Review_SortNewest),
        new(ReviewSort.OldestFirst, Strings.Review_SortOldest),
        new(ReviewSort.MostProblemsFirst, Strings.Review_SortProblems),
    ];

    /// <summary>Case and accents ignored: "saga" finds "Saga", "lafora" does not find "Lá Fora" but "la fora" does.</summary>
    public static bool Matches(string? text, string? filter) =>
        string.IsNullOrWhiteSpace(filter)
        || (text is not null && CultureInfo.CurrentCulture.CompareInfo.IndexOf(text, filter.Trim(),
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);

    public static IReadOnlyList<ReviewItemViewModel> Arrange(IEnumerable<ReviewItemViewModel> items, ReviewSort sort, string? filter)
    {
        var shown = items.Where(i => Matches(i.Title, filter) || Matches(i.Card.Book.MainFile?.DownloadedAs, filter));
        var skippedLast = shown.OrderBy(i => i.Identification.Skipped);
        return (sort switch
        {
            ReviewSort.TitleDescending => skippedLast.ThenByDescending(i => i.Title, StringComparer.CurrentCultureIgnoreCase),
            ReviewSort.NewestFirst => skippedLast.ThenByDescending(i => i.Card.Book.Metadata.CreatedAt).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase),
            ReviewSort.OldestFirst => skippedLast.ThenBy(i => i.Card.Book.Metadata.CreatedAt).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase),
            ReviewSort.MostProblemsFirst => skippedLast.ThenBy(i => i.Score).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => skippedLast.ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase),
        }).ToList();
    }

    /// <summary>A group is found by its name or by the title or file name of any of its books; its date is its newest book's.</summary>
    public static IReadOnlyList<ReviewGroup> Arrange(IEnumerable<ReviewGroup> groups, ReviewSort sort, string? filter)
    {
        var shown = groups.Where(g => Matches(g.Name, filter)
                                      || g.Books.Any(b => Matches(b.Title, filter) || Matches(b.Book.MainFile?.DownloadedAs, filter)));
        DateTimeOffset Added(ReviewGroup g) => g.Books.Count == 0 ? default : g.Books.Max(b => b.Book.Metadata.CreatedAt);
        return (sort switch
        {
            ReviewSort.TitleDescending => shown.OrderByDescending(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            ReviewSort.NewestFirst => shown.OrderByDescending(Added).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            ReviewSort.OldestFirst => shown.OrderBy(Added).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            ReviewSort.MostProblemsFirst => shown.OrderByDescending(g => g.HasWarnings).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => shown.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
        }).ToList();
    }
}
