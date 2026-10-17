using System.Globalization;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Quality;
using GibiOMatic.Core.Settings;
using GibiOMatic.Core.Text;

namespace GibiOMatic.Metadata.Identification;

public enum ProposalField
{
    Title,
    Series,
    Number,
    Kind,
    Date,
    Publisher,
    Writers,
    Artists,
    Inkers,
    Colorists,
    Letterers,
    CoverArtists,
    Isbn,
    Summary,
    OneShot,

    /// <summary>Replace the book's cover.jpg with the source's cover. Never chosen by default.</summary>
    Cover,
}

/// <summary>A row of the "campo · atual · proposto" table.</summary>
/// <param name="Changed">The proposal has a value and it differs from the current one.</param>
/// <param name="Conflict">The current value looks good and the proposal would replace it with a different one.</param>
public sealed record FieldChange(ProposalField Field, string? Current, string? Proposed, bool Changed, bool Conflict);

/// <summary>Compares a book with a candidate record and applies the chosen fields.</summary>
public static class Proposal
{
    private static readonly (ProposalField Field, CreditRole Role)[] CreditFields =
    [
        (ProposalField.Writers, CreditRole.Writer),
        (ProposalField.Artists, CreditRole.Artist),
        (ProposalField.Inkers, CreditRole.Inker),
        (ProposalField.Colorists, CreditRole.Colorist),
        (ProposalField.Letterers, CreditRole.Letterer),
        (ProposalField.CoverArtists, CreditRole.Cover),
    ];

    public static IReadOnlyList<FieldChange> Compare(BookMetadata book, MetadataRecord record, QualityReport? report = null)
    {
        report ??= QualityAnalyzer.Analyze(book);
        var rows = new List<FieldChange>
        {
            // Any change of spelling is offered; only a different title counts as a conflict.
            Row(ProposalField.Title, book.Title, record.Title, (a, b) => a.Trim() == b.Trim(), !report.HasProblemIn(QualityField.Title), SameTitle),
            Row(ProposalField.Series, book.Series?.Name, record.IsOneShot ? null : record.SeriesName,
                (a, b) => TextMatch.Normalize(a) == TextMatch.Normalize(b), !report.HasProblemIn(QualityField.Series)),
            Row(ProposalField.Number, book.Series?.Number, record.IsOneShot ? null : record.Number, TextMatch.SameNumber,
                !report.HasProblemIn(QualityField.Series)),
            Row(ProposalField.Kind, KindText(book.Kind), record.Kind is { } k ? KindText(k) : null, string.Equals, isGood: false),
            Row(ProposalField.Date, DateText(book.Date), DateText(record.Date), SameDate, !report.HasProblemIn(QualityField.Date)),
            Row(ProposalField.Publisher, book.Publisher, record.Publisher, TextMatch.SamePublisher, !report.HasProblemIn(QualityField.Publisher)),
        };

        foreach (var (field, role) in CreditFields)
        {
            var current = Names(book.Credits, role);
            var proposed = Names(record.Credits, role);
            var good = role != CreditRole.Writer || !report.HasProblemIn(QualityField.Authors);
            rows.Add(Row(field, current, proposed, SameNames, good));
        }

        rows.Add(Row(ProposalField.Isbn, book.Identifiers.Isbn, record.Isbn, (a, b) => Isbn.Normalize(a) == Isbn.Normalize(b),
            !report.HasProblemIn(QualityField.Identifier)));
        rows.Add(Row(ProposalField.Summary, book.Summary, record.Summary, (a, b) => a.Trim() == b.Trim(), isGood: false));
        if (record.CoverFullUrl is not null)
        {
            // Texts for the table; the images themselves are shown next to the title and in the candidate.
            rows.Add(new FieldChange(ProposalField.Cover, "cover:file", "cover:source", Changed: true, Conflict: false));
        }

        if (record.IsOneShot && book.OneShot != true)
        {
            rows.Add(new FieldChange(ProposalField.OneShot, book.OneShot == false ? "não" : null, "sim", Changed: true, Conflict: false));
        }

        return rows;
    }

    /// <summary>
    /// The fields to apply by default: those that change. The cover is never among them: the
    /// file's own cover is kept unless the user picks the source's (decision of 2026-10-17).
    /// </summary>
    public static IReadOnlySet<ProposalField> DefaultSelection(IEnumerable<FieldChange> rows) =>
        rows.Where(r => r.Changed && r.Field != ProposalField.Cover).Select(r => r.Field).ToHashSet();

    public static BookMetadata Apply(BookMetadata book, MetadataRecord record, IReadOnlySet<ProposalField> fields)
    {
        var result = book;
        if (fields.Contains(ProposalField.Title) && record.Title is { } title)
        {
            result = result with { Title = title };
        }

        if (fields.Contains(ProposalField.Series) || fields.Contains(ProposalField.Number))
        {
            var name = fields.Contains(ProposalField.Series) ? record.SeriesName : book.Series?.Name;
            var number = fields.Contains(ProposalField.Number) ? record.Number : book.Series?.Number;
            result = result with { Series = name is null ? null : new SeriesInfo(name, number, book.Series?.Volume) };
        }

        if (fields.Contains(ProposalField.Kind) && record.Kind is { } kind)
        {
            result = result with { Kind = kind };
        }

        if (fields.Contains(ProposalField.Date) && record.Date is { } date)
        {
            result = result with { Date = date };
        }

        if (fields.Contains(ProposalField.Publisher) && record.Publisher is { } publisher)
        {
            result = result with { Publisher = publisher };
        }

        var credits = result.Credits.ToList();
        foreach (var (field, role) in CreditFields)
        {
            if (fields.Contains(field) && record.Credits.Any(c => c.Role == role))
            {
                credits.RemoveAll(c => c.Role == role);
                credits.AddRange(record.Credits.Where(c => c.Role == role));
            }
        }

        result = result with { Credits = credits.OrderBy(c => c.Role).ToList() };

        if (fields.Contains(ProposalField.Isbn) && record.Isbn is { } isbn)
        {
            result = result with { Identifiers = result.Identifiers with { Isbn = isbn } };
        }

        if (fields.Contains(ProposalField.Summary) && record.Summary is { } summary)
        {
            result = result with { Summary = summary };
        }

        if (fields.Contains(ProposalField.OneShot) && record.IsOneShot)
        {
            result = result with { OneShot = true };
        }

        // The ComicVine id always comes along: it identifies the match.
        if (record.ComicVineId is { } cv)
        {
            result = result with { Identifiers = result.Identifiers with { ComicVine = cv } };
        }

        return result;
    }

    /// <summary>
    /// Safe auto-approval (SPEC §8.2 item 3): strong evidence, no good field overwritten by a
    /// different value, confidence ≥ 0.95, and "Sempre revisar" off.
    /// </summary>
    public static bool CanAutoApprove(BookMetadata book, IdentificationResult result, MetadataSettings settings, out string reason)
    {
        if (settings.AlwaysReview)
        {
            reason = "always-review";
            return false;
        }

        if (result.Best is not { } best)
        {
            reason = "no-candidate";
            return false;
        }

        if (best.Confidence < 0.95)
        {
            reason = "low-confidence";
            return false;
        }

        var comicVineOnly = best.Record.Source == SourceIds.ComicVine
                            && best.Reasons.Contains(MatchReason.Title) && best.Reasons.Contains(MatchReason.Number)
                            && best.Reasons.Contains(MatchReason.Publisher) && best.Reasons.Contains(MatchReason.Year)
                            && result.Candidates.Count(c => c.Confidence > 0.5) == 1;
        if (!best.IsbnMatch && !best.ConfirmedBySecondSource && !comicVineOnly)
        {
            reason = "weak-evidence";
            return false;
        }

        if (Compare(book, best.Record).Any(r => r.Conflict))
        {
            reason = "conflict";
            return false;
        }

        reason = "ok";
        return true;
    }

    /// <param name="same">Values that need no change.</param>
    /// <param name="equivalent">Different spellings of the same value (not a conflict); defaults to <paramref name="same"/>.</param>
    private static FieldChange Row(ProposalField field, string? current, string? proposed, Func<string, string, bool> same, bool isGood,
        Func<string, string, bool>? equivalent = null)
    {
        current = string.IsNullOrWhiteSpace(current) ? null : current;
        proposed = string.IsNullOrWhiteSpace(proposed) ? null : proposed;
        var changed = proposed is not null && (current is null || !same(current, proposed));
        var conflict = changed && current is not null && isGood && !(equivalent ?? same)(current, proposed!);
        return new FieldChange(field, current, proposed, changed, conflict);
    }

    private static bool SameTitle(string a, string b) =>
        TitleParser.BookKey(a) == TitleParser.BookKey(b) || TextMatch.Similarity(a, b) >= 0.9;

    private static bool SameDate(string a, string b)
    {
        // "2022" equals "2022-06": a year alone does not contradict a fuller date.
        var shorter = a.Length <= b.Length ? a : b;
        var longer = a.Length <= b.Length ? b : a;
        return longer.StartsWith(shorter, StringComparison.Ordinal);
    }

    private static bool SameNames(string a, string b)
    {
        static HashSet<string> Set(string s) => s.Split(", ").Select(TextMatch.Normalize).ToHashSet();
        return Set(a).SetEquals(Set(b));
    }

    private static string? Names(IEnumerable<Credit> credits, CreditRole role)
    {
        var names = credits.Where(c => c.Role == role).Select(c => c.Name).Distinct().ToList();
        return names.Count == 0 ? null : string.Join(", ", names);
    }

    public static string? DateText(PartialDate? date) => date switch
    {
        null => null,
        { Month: { } m, Day: { } d } => $"{date.Year:D4}-{m:D2}-{d:D2}",
        { Month: { } m } => $"{date.Year:D4}-{m:D2}",
        _ => date.Year.ToString(CultureInfo.InvariantCulture),
    };

    private static string KindText(BookKind kind) => kind.ToString().ToLowerInvariant();
}
