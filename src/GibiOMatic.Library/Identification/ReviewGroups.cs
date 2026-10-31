using System.Text;
using System.Text.Json;
using Dapper;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Ids;
using GibiOMatic.Core.Json;
using GibiOMatic.Core.Text;
using GibiOMatic.Library.Index;
using GibiOMatic.Metadata;
using GibiOMatic.Metadata.Identification;

namespace GibiOMatic.Library.Identification;

/// <summary>A book of the Review queue, as the Review by series sees it.</summary>
/// <param name="Number">The number read from its title ("Vol. 3" → "3"); null when it has none.</param>
/// <param name="SeriesName">The series read from its title, as written ("Deadly Class"); null when it has none.</param>
public sealed record ReviewGroupBook(CatalogBook Book, BookIdentification Identification, string? Number, string? SeriesName)
{
    public string BookId => Book.Metadata.Id;

    public string Title => Book.Metadata.Title;

    public MetadataCandidate? Best => Identification.Result?.Best;

    /// <summary>The ComicVine volume of its best candidate, if it is a ComicVine record.</summary>
    public int? VolumeId => Best?.Record.ComicVineVolumeId;
}

/// <summary>A group of the Review by series: a proposal, nothing is saved until it is approved.</summary>
/// <param name="Id">"key:deadlyclass" for an automatic group, "group:&lt;ulid&gt;" for one the user created.</param>
/// <param name="Diverging">Books whose best candidate is in another volume than most of the group's, or that have none.</param>
public sealed record ReviewGroup(string Id, string Name, IReadOnlyList<ReviewGroupBook> Books, int Diverging, bool NumberRepeated, bool NumberMissing)
{
    public bool HasWarnings => Diverging > 0 || NumberRepeated || NumberMissing;
}

/// <param name="Ungrouped">The books of the fixed "No group" entry, reviewed one by one.</param>
public sealed record ReviewGrouping(IReadOnlyList<ReviewGroup> Groups, IReadOnlyList<ReviewGroupBook> Ungrouped);

/// <summary>The series chosen for a group: a ComicVine volume, or typed by hand (no volume).</summary>
public sealed record GroupSeries(string Name, string? Publisher, int? StartYear, int? VolumeId = null, int IssueCount = 0);

/// <summary>
/// The automatic grouping of the Review queue by series (SPEC §7.1): books whose titles read as the
/// same series (case, punctuation, accents, a leading "The" and spaces ignored) go together, groups
/// whose books' best candidates share a ComicVine volume are merged, and a series of one book forms
/// no group. The user's corrections ("pins") win over it.
/// </summary>
public static class SeriesGrouping
{
    /// <summary>"Deadly Class, Vol. 3" and "DeadlyClass_v5" → "deadlyclass"; null when the title names no series.</summary>
    public static string? SeriesKey(string? seriesName)
    {
        if (string.IsNullOrWhiteSpace(seriesName))
        {
            return null;
        }

        var text = TitleParser.RemoveDiacritics(seriesName).ToLowerInvariant().Trim();
        if (text.StartsWith("the ", StringComparison.Ordinal))
        {
            text = text[4..];
        }

        var key = new StringBuilder(text.Length);
        foreach (var c in text.Where(char.IsLetterOrDigit))
        {
            key.Append(c);
        }

        return key.Length == 0 ? null : key.ToString();
    }

    public static ReviewGroupBook Describe(CatalogBook book, BookIdentification identification)
    {
        var parts = TitleParser.Parse(book.Metadata.Title);
        return new ReviewGroupBook(book, identification, BookQuery.From(book.Metadata).Number,
            string.IsNullOrWhiteSpace(parts.Name) ? null : parts.Name);
    }

    /// <param name="pins">The user's corrections: book → group id, or null for "No group".</param>
    public static ReviewGrouping Build(IReadOnlyList<ReviewGroupBook> books, IReadOnlyDictionary<string, string?> pins)
    {
        var free = books.Where(b => !pins.ContainsKey(b.BookId)).ToList();
        var node = free.ToDictionary(b => b.BookId, b => SeriesKey(b.SeriesName) is { } key ? "key:" + key : "book:" + b.BookId);

        // Union-find over the series keys, joined by the volume of the best candidates.
        var parent = node.Values.Distinct().ToDictionary(n => n, n => n);
        string Find(string n) => parent[n] == n ? n : parent[n] = Find(parent[n]);
        foreach (var byVolume in free.Where(b => b.VolumeId is not null).GroupBy(b => b.VolumeId))
        {
            var first = Find(node[byVolume.First().BookId]);
            foreach (var other in byVolume.Skip(1))
            {
                var root = Find(node[other.BookId]);
                if (root != first)
                {
                    parent[root] = first;
                }
            }
        }

        var members = new Dictionary<string, List<ReviewGroupBook>>(StringComparer.Ordinal);
        foreach (var component in free.GroupBy(b => Find(node[b.BookId])))
        {
            // The group is named after its biggest series key, so it keeps its id while books come and go.
            var id = component.GroupBy(b => node[b.BookId])
                .OrderBy(g => g.Key.StartsWith("book:", StringComparison.Ordinal) ? 1 : 0)
                .ThenByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .First().Key;
            members[id] = component.ToList();
        }

        var pinnedGroups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var book in books.Where(b => pins.GetValueOrDefault(b.BookId) is not null))
        {
            var id = pins[book.BookId]!;
            pinnedGroups.Add(id);
            if (!members.TryGetValue(id, out var list))
            {
                members[id] = list = [];
            }

            list.Add(book);
        }

        var groups = new List<ReviewGroup>();
        var ungrouped = books.Where(b => pins.TryGetValue(b.BookId, out var pin) && pin is null).ToList();
        foreach (var (id, list) in members)
        {
            if (list.Count >= 2 || pinnedGroups.Contains(id))
            {
                groups.Add(Group(id, list));
            }
            else
            {
                ungrouped.AddRange(list);
            }
        }

        return new ReviewGrouping(
            groups.OrderByDescending(g => g.HasWarnings).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            ungrouped.OrderBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase).ToList());
    }

    private static ReviewGroup Group(string id, List<ReviewGroupBook> books)
    {
        var ordered = books.OrderBy(b => decimal.TryParse(b.Number, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : decimal.MaxValue)
            .ThenBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // "Deadly Class" rather than "DeadlyClass": the spelling most books use, with spaces if any has them.
        var name = books.Where(b => b.SeriesName is not null)
            .GroupBy(b => b.SeriesName!)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Key.Contains(' ', StringComparison.Ordinal))
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key)
            .FirstOrDefault() ?? books[0].Title;

        var mainVolume = books.Where(b => b.VolumeId is not null).GroupBy(b => b.VolumeId).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        var diverging = books.Count(b => b.VolumeId is null || b.VolumeId != mainVolume);
        var numbers = books.Where(b => b.Number is not null).Select(b => b.Number!).ToList();
        var repeated = numbers.Count != numbers.Distinct(NumberComparer.Instance).Count();
        return new ReviewGroup(id, name, ordered, diverging, repeated, numbers.Count < books.Count);
    }

    private sealed class NumberComparer : IEqualityComparer<string>
    {
        public static NumberComparer Instance { get; } = new();

        public bool Equals(string? x, string? y) => TextMatch.SameNumber(x, y);

        public int GetHashCode(string obj) =>
            decimal.TryParse(obj, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var n)
                ? n.GetHashCode()
                : StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Trim());
    }
}

/// <summary>
/// The groups of the Review by series of one library, with the user's corrections (kept in the
/// index until the books leave the queue) and the series chosen for each group.
/// </summary>
public sealed class ReviewGroups(LibraryIndex index, IdentificationQueue queue)
{
    /// <summary>The books of the queue (not approved yet), grouped; corrections of books that left it are dropped.</summary>
    public async Task<ReviewGrouping> GetAsync(CancellationToken ct = default)
    {
        var books = (await index.GetBooksAsync(ct)).ToDictionary(b => b.Metadata.Id);
        var queued = (await queue.GetReviewItemsAsync(ct))
            .Where(i => books.TryGetValue(i.BookId, out var b) && b.Metadata.Review.Status == ReviewStatus.Pending)
            .Select(i => SeriesGrouping.Describe(books[i.BookId], i))
            .ToList();

        await using var connection = await index.OpenAsync(ct);
        var pins = (await connection.QueryAsync<(string BookId, string? GroupId)>(new CommandDefinition(
            "SELECT book_id, group_id FROM review_group_books;", cancellationToken: ct))).ToList();
        var inQueue = queued.Select(b => b.BookId).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in pins.Where(p => !inQueue.Contains(p.BookId)))
        {
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM review_group_books WHERE book_id = @BookId;", new { gone.BookId },
                cancellationToken: ct));
        }

        var grouping = SeriesGrouping.Build(queued, pins.Where(p => inQueue.Contains(p.BookId)).ToDictionary(p => p.BookId, p => p.GroupId));
        var ids = grouping.Groups.Select(g => g.Id).ToList();
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM review_group_series WHERE group_id NOT IN @ids;", new { ids }, cancellationToken: ct));
        return grouping;
    }

    /// <summary>"Remove from group": the books go to No group.</summary>
    public Task RemoveAsync(IEnumerable<string> bookIds, CancellationToken ct = default) => PinAsync(bookIds, null, ct);

    /// <summary>"Ungroup": every book of the group goes to No group.</summary>
    public Task UngroupAsync(ReviewGroup group, CancellationToken ct = default) => PinAsync(group.Books.Select(b => b.BookId), null, ct);

    /// <summary>"Add to group": books from No group or other groups join it; its own books stay with it from now on.</summary>
    public async Task AddAsync(ReviewGroup group, IEnumerable<string> bookIds, CancellationToken ct = default)
    {
        // Books of the group the user already moved elsewhere stay where they went.
        HashSet<string> moved;
        await using (var connection = await index.OpenAsync(ct))
        {
            moved = (await connection.QueryAsync<string>(new CommandDefinition(
                "SELECT book_id FROM review_group_books WHERE group_id IS NULL OR group_id <> @id;", new { id = group.Id }, cancellationToken: ct))).ToHashSet();
        }

        await PinAsync(group.Books.Select(b => b.BookId).Where(id => !moved.Contains(id)).Concat(bookIds), group.Id, ct);
    }

    /// <summary>"Merge with…": the books of <paramref name="from"/> join <paramref name="into"/>.</summary>
    public Task MergeAsync(ReviewGroup from, ReviewGroup into, CancellationToken ct = default) =>
        AddAsync(into, from.Books.Select(b => b.BookId), ct);

    /// <summary>"Create group" from books chosen in No group; returns its id.</summary>
    public async Task<string> CreateAsync(IEnumerable<string> bookIds, CancellationToken ct = default)
    {
        var id = "group:" + Ulid.NewUlid();
        await PinAsync(bookIds, id, ct);
        return id;
    }

    /// <summary>Keeps books in a group whatever the automatic grouping says.</summary>
    public async Task PinAsync(IEnumerable<string> bookIds, string? groupId, CancellationToken ct = default)
    {
        await using var connection = await index.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        foreach (var bookId in bookIds.Distinct())
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO review_group_books (book_id, group_id) VALUES (@bookId, @groupId) ON CONFLICT(book_id) DO UPDATE SET group_id = @groupId;",
                new { bookId, groupId }, transaction: tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
    }

    public async Task<GroupSeries?> GetSeriesAsync(string groupId, CancellationToken ct = default)
    {
        await using var connection = await index.OpenAsync(ct);
        var json = await connection.QueryFirstOrDefaultAsync<string>(new CommandDefinition(
            "SELECT series_json FROM review_group_series WHERE group_id = @groupId;", new { groupId }, cancellationToken: ct));
        return json is null ? null : JsonSerializer.Deserialize<GroupSeries>(json, GibiJson.Options);
    }

    public async Task SetSeriesAsync(string groupId, GroupSeries series, CancellationToken ct = default)
    {
        await using var connection = await index.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO review_group_series (group_id, series_json) VALUES (@groupId, @json)
            ON CONFLICT(group_id) DO UPDATE SET series_json = @json;
            """,
            new { groupId, json = JsonSerializer.Serialize(series, GibiJson.Options) }, cancellationToken: ct));
    }
}

/// <summary>A row of the open group's table: what one book will get when the group is approved.</summary>
/// <param name="Candidate">The book's record: an issue of the group's volume, its own best candidate, or one the user chose; null for "No match".</param>
/// <param name="NewTitle">The title from the template; null when each book keeps its title.</param>
public sealed record SeriesPlanRow(ReviewGroupBook Book, VolumeMatchKind Kind, MetadataCandidate? Candidate, string? NewTitle)
{
    public string BookId => Book.BookId;

    /// <summary>The record is in another volume than the one the book matched before.</summary>
    public bool ChangesVolume => Candidate?.Record.ComicVineVolumeId is { } volume && volume != Book.VolumeId;

    public bool ChangesTitle => NewTitle is not null && !string.Equals(NewTitle, Book.Title, StringComparison.Ordinal);

    /// <summary>The row differs from what the book would get in the Review by book (highlighted).</summary>
    public bool Changes => Candidate is not null
                           && (ChangesVolume || ChangesTitle || Candidate.Record.ComicVineId != Book.Best?.Record.ComicVineId);
}

/// <summary>How the Review by series turns a group's choices into each book's record (SPEC §7.1).</summary>
public static class SeriesPlan
{
    public static SeriesPlanRow Row(ReviewGroupBook book, VolumeBookMatch match, GroupSeries series, string? template) =>
        new(book, match.Kind, match.Candidate, match.Candidate is { } c && template is not null ? TitleFor(template, series, c.Record) : null);

    public static string TitleFor(string template, GroupSeries series, MetadataRecord record) =>
        TitleTemplate.Render(template, new TitleFields(series.Name, record.Number, VolumeMatcher.NameOf(record), record.Date?.Year));

    /// <summary>
    /// The record applied to a book: the group's series (and its publisher) whatever volume the record
    /// came from, and the title from the template, or none to keep the book's.
    /// </summary>
    public static MetadataRecord Shape(MetadataRecord record, GroupSeries series, string? title) => record with
    {
        SeriesName = series.Name,
        Publisher = series.Publisher ?? record.Publisher,
        Title = title,
        IsOneShot = false,
    };
}
