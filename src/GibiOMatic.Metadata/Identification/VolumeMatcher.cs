using GibiOMatic.Core.Domain;
using GibiOMatic.Metadata.Sources;

namespace GibiOMatic.Metadata.Identification;

/// <summary>Where the record of a book in the Review by series came from (SPEC §7.1).</summary>
public enum VolumeMatchKind
{
    /// <summary>The issue with the book's number in the group's volume.</summary>
    VolumeIssue,

    /// <summary>The volume has no such issue; the book's own best candidate has its number.</summary>
    OwnCandidate,

    /// <summary>Picked by the user from the volume's issues.</summary>
    Chosen,

    /// <summary>No record: the book stays in the Review.</summary>
    None,
}

/// <param name="Number">The book's number as read from its title; null when it has none.</param>
/// <param name="Best">The book's own best candidate, from its identification.</param>
public sealed record VolumeBook(BookMetadata Book, string? Number, MetadataCandidate? Best);

public sealed record VolumeBookMatch(string BookId, VolumeMatchKind Kind, MetadataCandidate? Candidate);

/// <summary>A ComicVine volume with every issue it has.</summary>
public sealed record VolumeIssues(CvVolume Volume, IReadOnlyList<CvIssueSummary> Issues);

/// <summary>
/// Matching inside one ComicVine volume for the Review by series (SPEC §7.1): one request lists the
/// volume's issues (cached and spaced like every ComicVine call), then each book gets the issue with
/// its number, else its own best candidate when that one has the number, else nothing.
/// </summary>
public sealed class VolumeMatcher(IComicVineApi comicVine)
{
    public Task<bool> HasKeyAsync(CancellationToken ct) => comicVine.HasKeyAsync(ct);

    public Task<CvVolume?> GetVolumeAsync(int id, CancellationToken ct) => comicVine.GetVolumeAsync(id, ct);

    /// <summary>The volume of a pasted ComicVine link: a volume link, or the volume of an issue link.</summary>
    public async Task<CvVolume?> FromLinkAsync(string? link, CancellationToken ct)
    {
        if (BookIdentifier.ParseComicVineLink(link) is not { } target)
        {
            return null;
        }

        if (!target.IsIssue)
        {
            return await comicVine.GetVolumeAsync(target.Id, ct);
        }

        return await comicVine.GetIssueAsync(target.Id, ct) is { } issue ? await comicVine.GetVolumeAsync(issue.VolumeId, ct) : null;
    }

    public async Task<VolumeIssues> GetIssuesAsync(CvVolume volume, CancellationToken ct) =>
        new(volume, await comicVine.GetVolumeIssuesAsync(volume.Id, volume.IssueCount, ct));

    public async Task<IReadOnlyList<VolumeBookMatch>> MatchAsync(CvVolume volume, IReadOnlyList<VolumeBook> books, CancellationToken ct)
    {
        var issues = await GetIssuesAsync(volume, ct);
        return books.Select(b => Match(issues, b)).ToList();
    }

    public static VolumeBookMatch Match(VolumeIssues volume, VolumeBook book)
    {
        if (book.Number is { } number)
        {
            if (volume.Issues.FirstOrDefault(i => TextMatch.SameNumber(i.IssueNumber, number)) is { } issue)
            {
                return new VolumeBookMatch(book.Book.Id, VolumeMatchKind.VolumeIssue, CandidateFor(issue, volume.Volume, book.Book));
            }

            // Collected editions are often volumes of their own on ComicVine ("Deadly Class: A Fond Farewell").
            if (book.Best is { Record.Number: { } own } best && TextMatch.SameNumber(own, number))
            {
                return new VolumeBookMatch(book.Book.Id, VolumeMatchKind.OwnCandidate, best);
            }
        }

        return new VolumeBookMatch(book.Book.Id, VolumeMatchKind.None, null);
    }

    /// <summary>The record of an issue of the volume for a book: read as identification reads it, with the issue's own number.</summary>
    public static MetadataCandidate CandidateFor(CvIssueSummary issue, CvVolume volume, BookMetadata book)
    {
        var scored = BookIdentifier.Score(issue, volume, BookQuery.From(book));
        return new MetadataCandidate(scored.Record with { Number = issue.IssueNumber ?? scored.Record.Number, IsOneShot = false }, 1.0,
            [MatchReason.ChosenByUser]);
    }

    /// <summary>
    /// The issue's own name for <c>{Name}</c>, without a collected-edition number ("Volume 11: Over
    /// the Hill…" → "Over the Hill…"); for a collected edition that is a volume of its own, the
    /// volume's subtitle ("Deadly Class: A Fond Farewell" → "A Fond Farewell").
    /// </summary>
    public static string? NameOf(MetadataRecord record)
    {
        var (volumeName, issueName) = (record.VolumeName, record.IssueName);
        if (volumeName is null && record.SourceName is { } source)
        {
            // Results stored before the names were kept: "Volume #3 · Issue name".
            var dot = source.IndexOf(" · ", StringComparison.Ordinal);
            var head = dot < 0 ? source : source[..dot];
            var hash = head.LastIndexOf(" #", StringComparison.Ordinal);
            volumeName = hash < 0 ? head : head[..hash];
            issueName = dot < 0 ? null : source[(dot + 3)..];
        }

        if (BookIdentifier.IssueTitleOf(issueName) is { Length: > 0 } name && TextMatch.CollectedNumber(name) is null)
        {
            return name.Trim();
        }

        return volumeName is null ? null : BookIdentifier.SuffixOf(volumeName);
    }
}
