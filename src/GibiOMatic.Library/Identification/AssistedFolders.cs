using System.Text.Json;
using GibiOMatic.Conversion.Images;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Json;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Library.Index;
using GibiOMatic.Metadata.Assisted;
using GibiOMatic.Metadata.Identification;

namespace GibiOMatic.Library.Identification;

/// <summary>
/// The temporary folder of a run (SPEC §8.4, step 1): what Claude may read, and nothing else from
/// the library. The originals are only read.
/// </summary>
public static class AssistedFolders
{
    public const string BookFile = "book.json";
    public const string GroupFile = "group.json";
    public const string CoverFile = "cover.jpg";

    /// <summary>Cover size given to Claude in a group run: enough to recognise it, small enough for 15 of them.</summary>
    public const int GroupCoverSize = 500;

    /// <summary>Books of a group whose inner pages are given: one credits page is usually enough to understand the series.</summary>
    public const int GroupBooksWithPages = 2;

    /// <summary>
    /// <c>book.json</c>, <c>cover.jpg</c> and the credits pages (the 3 after the cover and the last 2).
    /// <paramref name="previous"/> is the quick search's answer, for a deep search.
    /// </summary>
    public static void WriteBook(LibraryInfo library, CatalogBook book, IdentificationResult? result, string folder, BookAnswer? previous,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var bookFolder = Path.Combine(library.RootPath, book.Folder);
        WriteCover(Path.Combine(bookFolder, LibraryLayout.CoverFileName), Path.Combine(folder, CoverFile), PageSnapshots.PageSize);
        var (pages, count) = book.MainFile is { } main && File.Exists(Path.Combine(bookFolder, main.Name))
            ? PageSnapshots.Write(Path.Combine(bookFolder, main.Name), main.Format, folder, "page-", PageSnapshots.CreditPages, ct)
            : ([], 0);
        var info = new
        {
            title = book.Metadata.Title,
            fileName = book.MainFile?.DownloadedAs ?? book.MainFile?.Name,
            format = book.MainFile?.Format.ToString().ToLowerInvariant(),
            pages = count > 0 ? count : book.MainFile?.Pages,
            embedded = Embedded(book.Metadata),
            algorithmCandidates = Candidates(result),
            images = new[] { CoverFile }.Concat(pages),
            quickSearchAnswer = previous,
        };
        File.WriteAllText(Path.Combine(folder, BookFile), JsonSerializer.Serialize(info, GibiJson.Options));
    }

    /// <summary>
    /// <c>group.json</c>, <c>covers/&lt;id&gt;.jpg</c> for every book and <c>pages/&lt;id&gt;-pNNN.jpg</c>
    /// for the first <see cref="GroupBooksWithPages"/> books whose file is on disk.
    /// </summary>
    public static void WriteGroup(LibraryInfo library, ReviewGroup group, string folder, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.Combine(folder, "covers"));
        Directory.CreateDirectory(Path.Combine(folder, "pages"));
        var withPages = 0;
        var books = new List<object>();
        foreach (var member in group.Books)
        {
            ct.ThrowIfCancellationRequested();
            var book = member.Book;
            var bookFolder = Path.Combine(library.RootPath, book.Folder);
            var cover = $"covers/{member.BookId}.jpg";
            var hasCover = WriteCover(Path.Combine(bookFolder, LibraryLayout.CoverFileName), Path.Combine(folder, cover), GroupCoverSize);
            IReadOnlyList<string> pages = [];
            var count = book.MainFile?.Pages;
            if (withPages < GroupBooksWithPages && book.MainFile is { } main && File.Exists(Path.Combine(bookFolder, main.Name)))
            {
                var written = PageSnapshots.Write(Path.Combine(bookFolder, main.Name), main.Format, Path.Combine(folder, "pages"), member.BookId + "-p",
                    PageSnapshots.CreditPages, ct);
                pages = written.Files.Select(f => "pages/" + f).ToList();
                count = written.PageCount > 0 ? written.PageCount : count;
                withPages += pages.Count > 0 ? 1 : 0;
            }

            books.Add(new
            {
                id = member.BookId,
                title = book.Metadata.Title,
                fileName = book.MainFile?.DownloadedAs ?? book.MainFile?.Name,
                format = book.MainFile?.Format.ToString().ToLowerInvariant(),
                pages = count,
                numberReadFromTitle = member.Number,
                embeddedIsbn = book.Metadata.Identifiers.Isbn,
                publisher = book.Metadata.Publisher,
                algorithmCandidates = Candidates(member.Identification.Result, 3),
                cover = hasCover ? cover : null,
                innerPages = pages,
            });
        }

        File.WriteAllText(Path.Combine(folder, GroupFile), JsonSerializer.Serialize(new { groupName = group.Name, books }, GibiJson.Options));
    }

    private static object Embedded(BookMetadata book) => new
    {
        publisher = book.Publisher,
        date = Proposal.DateText(book.Date),
        isbn = book.Identifiers.Isbn,
        series = book.Series?.Name,
        number = book.Series?.Number,
        credits = book.Credits.Select(c => $"{c.Name} ({c.Role.ToString().ToLowerInvariant()})"),
    };

    /// <summary>The algorithm's own candidates (never Claude's earlier ones), as hints.</summary>
    private static IEnumerable<object> Candidates(IdentificationResult? result, int count = 5) =>
        (result?.Candidates ?? []).Where(c => c.Assisted is null).Take(count).Select(c => new
        {
            title = c.Record.SourceName ?? c.Record.Title,
            comicVineId = c.Record.ComicVineId,
            year = c.Record.Date?.Year,
            confidence = c.Confidence,
        });

    private static bool WriteCover(string source, string target, int size)
    {
        if (!File.Exists(source) || PageSnapshots.Scale(File.ReadAllBytes(source), size) is not { } jpeg)
        {
            return false;
        }

        File.WriteAllBytes(target, jpeg);
        return true;
    }
}
