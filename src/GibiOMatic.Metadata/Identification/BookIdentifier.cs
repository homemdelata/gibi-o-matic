using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Settings;
using GibiOMatic.Core.Text;
using GibiOMatic.Metadata.Http;
using GibiOMatic.Metadata.Sources;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Metadata.Identification;

/// <summary>Candidates found for a book, best first, and what went wrong with the sources.</summary>
/// <param name="Notes">Problems worth showing ("comicvine:no-key", "openlibrary:Unavailable").</param>
public sealed record IdentificationResult(BookQuery Query, IReadOnlyList<MetadataCandidate> Candidates, IReadOnlyList<string> Notes)
{
    public MetadataCandidate? Best => Candidates.Count > 0 ? Candidates[0] : null;
}

public interface IBookIdentifier
{
    /// <param name="searchText">Text typed by the user instead of the book's title.</param>
    Task<IdentificationResult> IdentifyAsync(BookMetadata book, MetadataSettings settings, string? searchText, CancellationToken ct);

    /// <summary>Fetches what the search summary lacks (credits, synopsis) for a ComicVine candidate.</summary>
    Task<MetadataCandidate> CompleteAsync(MetadataCandidate candidate, CancellationToken ct);
}

/// <summary>
/// SPEC §8.2: ISBN first (Open Library, Google Books), then ComicVine volumes (collected editions
/// are volumes of their own), each candidate scored by title, number, publisher, year and kind.
/// </summary>
public sealed class BookIdentifier(
    IEnumerable<IIsbnSource> isbnSources,
    IComicVineApi comicVine,
    ILogger<BookIdentifier> logger) : IBookIdentifier
{
    /// <summary>Confidence given to a record found by the file's ISBN.</summary>
    public const double IsbnConfidence = 0.96;

    private const int MaxCandidates = 6;

    public async Task<IdentificationResult> IdentifyAsync(BookMetadata book, MetadataSettings settings, string? searchText, CancellationToken ct)
    {
        var query = BookQuery.From(book, searchText);
        var notes = new List<string>();

        // 1. ISBN: Open Library, then Google Books, in the configured order.
        var isbnRecords = new List<MetadataRecord>();
        if (query.Isbn is { } isbn)
        {
            foreach (var source in settings.Sources.Where(s => s.Enabled))
            {
                if (isbnSources.FirstOrDefault(s => s.Id == source.Id) is not { } isbnSource)
                {
                    continue;
                }

                try
                {
                    if (await isbnSource.LookupIsbnAsync(isbn, ct) is { } record)
                    {
                        isbnRecords.Add(Shape(record));
                    }
                }
                catch (MetadataSourceException ex)
                {
                    notes.Add($"{isbnSource.Id}:{ex.Error}");
                    logger.LogWarning("{Source} failed for ISBN {Isbn}: {Message}", isbnSource.Id, isbn, ex.Message);
                }
            }
        }

        // The ISBN record's title is better than a file-name title for searching ComicVine.
        var cvQuery = query;
        if (searchText is null && isbnRecords.FirstOrDefault() is { Title: { } knownTitle } known)
        {
            var refined = BookQuery.From(new BookMetadata { Id = book.Id, Title = knownTitle });
            cvQuery = refined with
            {
                Publisher = query.Publisher ?? known.Publisher,
                Year = query.Year ?? known.Date?.Year,
                Isbn = query.Isbn,
                Language = query.Language,
            };
        }

        // 2. ComicVine.
        var cvCandidates = new List<MetadataCandidate>();
        if (settings.IsEnabled(SourceIds.ComicVine))
        {
            if (!await comicVine.HasKeyAsync(ct))
            {
                notes.Add("comicvine:no-key");
            }
            else
            {
                try
                {
                    cvCandidates = await SearchComicVineAsync(cvQuery, ct);
                }
                catch (MetadataSourceException ex)
                {
                    notes.Add($"comicvine:{ex.Error}");
                    logger.LogWarning("ComicVine failed for '{Query}': {Message}", cvQuery.SearchText, ex.Message);
                }
            }
        }

        // 3. Agreement between the ISBN record and ComicVine.
        var candidates = new List<MetadataCandidate>();
        var confirmedIsbnRecords = new HashSet<MetadataRecord>();
        foreach (var candidate in cvCandidates)
        {
            var agreeing = isbnRecords.FirstOrDefault(r => SameBook(r, candidate.Record));
            if (agreeing is null)
            {
                candidates.Add(candidate);
                continue;
            }

            confirmedIsbnRecords.Add(agreeing);
            var reasons = candidate.Reasons.Append(MatchReason.Isbn).ToList();
            if (TextMatch.SamePublisher(agreeing.Publisher, candidate.Record.Publisher))
            {
                reasons.Add(MatchReason.SecondSource);
            }

            var merged = candidate.Record with
            {
                Isbn = agreeing.Isbn,
                PageCount = candidate.Record.PageCount ?? agreeing.PageCount,
                Date = candidate.Record.Date ?? agreeing.Date,
            };
            candidates.Add(new MetadataCandidate(merged, Math.Max(candidate.Confidence, 0.97), reasons.Distinct().ToList()));
        }

        foreach (var record in isbnRecords)
        {
            // Still offered, below the ComicVine record that it confirmed.
            var confidence = confirmedIsbnRecords.Contains(record) ? 0.6 : IsbnConfidence;
            candidates.Add(new MetadataCandidate(record, confidence, [MatchReason.Isbn]));
        }

        var ordered = candidates
            .OrderByDescending(c => c.Confidence)
            .ThenBy(c => c.Record.Source == SourceIds.ComicVine ? 0 : 1)
            .Take(MaxCandidates)
            .ToList();

        // Details (credits, synopsis) only for the best ComicVine match; the others on demand.
        if (ordered.Count > 0 && !ordered[0].Record.IsDetailed)
        {
            try
            {
                ordered[0] = await CompleteAsync(ordered[0], ct);
            }
            catch (MetadataSourceException ex)
            {
                notes.Add($"comicvine:{ex.Error}");
            }
        }

        return new IdentificationResult(cvQuery, ordered, notes);
    }

    public async Task<MetadataCandidate> CompleteAsync(MetadataCandidate candidate, CancellationToken ct)
    {
        if (candidate.Record.IsDetailed || candidate.Record.ComicVineId is not { } cvId
            || !int.TryParse(cvId.Replace("4000-", string.Empty, StringComparison.Ordinal), out var issueId))
        {
            return candidate;
        }

        var issue = await comicVine.GetIssueAsync(issueId, ct);
        if (issue is null)
        {
            return candidate;
        }

        return candidate with
        {
            Record = candidate.Record with
            {
                Credits = issue.Credits,
                Summary = issue.Description ?? candidate.Record.Summary,
                Date = issue.Date ?? candidate.Record.Date,
                SourceUrl = issue.SiteUrl,
                IsDetailed = true,
            },
        };
    }

    private async Task<List<MetadataCandidate>> SearchComicVineAsync(BookQuery query, CancellationToken ct)
    {
        var volumes = await comicVine.SearchVolumesAsync(query.SearchText, ct);
        var target = query.Series ?? query.SearchText;
        var relevant = volumes
            .Select(v => (Volume: v, Score: NameScore(v.Name, target, query.SearchText)))
            .Where(x => x.Score >= 0.6)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => TextMatch.SamePublisher(x.Volume.Publisher, query.Publisher))
            .Take(15)
            .Select(x => x.Volume)
            .ToList();
        if (relevant.Count == 0)
        {
            return [];
        }

        // Collected editions and short series: every issue in one request. Long series: only the
        // issue with the wanted number, in a second request.
        var issues = new List<CvIssueSummary>();
        var small = relevant.Where(v => v.IssueCount <= 12).Select(v => v.Id).ToList();
        if (small.Count > 0)
        {
            issues.AddRange(await comicVine.GetIssuesAsync(small, ct));
        }

        var big = relevant.Where(v => v.IssueCount > 12).Take(3).Select(v => v.Id).ToList();
        if (big.Count > 0 && query.Number is not null)
        {
            issues.AddRange(await comicVine.GetIssuesAsync(big, ct, query.Number));
        }

        var byId = relevant.ToDictionary(v => v.Id);
        return issues
            .Where(i => byId.ContainsKey(i.VolumeId))
            .DistinctBy(i => i.Id)
            .Select(i => Score(i, byId[i.VolumeId], query))
            .Where(c => c.Confidence > 0.25)
            .OrderByDescending(c => c.Confidence)
            .Take(MaxCandidates)
            .ToList();
    }

    /// <summary>How well a volume name matches: "Deadly Class: Reagan Youth" matches "Deadly Class".</summary>
    private static double NameScore(string volumeName, string series, string searchText)
    {
        var prefix = PrefixOf(volumeName);
        return Math.Max(TextMatch.Similarity(prefix, series), TextMatch.Similarity(volumeName, searchText));
    }

    internal static MetadataCandidate Score(CvIssueSummary issue, CvVolume volume, BookQuery query)
    {
        var prefix = PrefixOf(volume.Name);
        var suffix = SuffixOf(volume.Name);
        var collected = TextMatch.CollectedNumber(issue.Name);
        var shortVolume = volume.IssueCount <= 12;
        var series = query.Series ?? query.SearchText;
        var exactSeriesVolume = TextMatch.Normalize(volume.Name) == TextMatch.Normalize(series);

        // A deluxe/hardcover series named like the series numbers its volumes by issue number.
        collected ??= shortVolume && volume.IssueCount > 1 && exactSeriesVolume && query.Kind != BookKind.Issue ? issue.IssueNumber : null;
        var kind = KindOf(volume, collected, shortVolume);
        var number = query.Kind == BookKind.Issue ? issue.IssueNumber : collected ?? (shortVolume ? null : issue.IssueNumber);
        var oneShot = volume.IssueCount == 1 && collected is null && suffix is null;

        var reasons = new List<MatchReason>();
        var titleSim = Math.Max(TextMatch.Similarity(prefix, series), TextMatch.Similarity(volume.Name, query.SearchText));
        var confidence = 0.30 * titleSim;
        if (titleSim >= 0.85)
        {
            reasons.Add(MatchReason.Title);
        }

        if (TextMatch.Normalize(prefix) == TextMatch.Normalize(series))
        {
            confidence += 0.05;
        }

        if (query.Subtitle is not null && suffix is not null && TextMatch.Similarity(suffix, query.Subtitle) >= 0.7)
        {
            confidence += 0.05;
        }

        if (query.Number is not null)
        {
            if (TextMatch.SameNumber(number, query.Number))
            {
                confidence += 0.35;
                reasons.Add(MatchReason.Number);
            }
            else
            {
                confidence -= number is null ? 0.1 : 0.25;
            }
        }
        else if (volume.IssueCount == 1)
        {
            confidence += 0.25; // a stand-alone book searched by its title
        }

        if (query.Publisher is not null)
        {
            if (TextMatch.SamePublisher(volume.Publisher, query.Publisher))
            {
                confidence += 0.15;
                reasons.Add(MatchReason.Publisher);
            }
            else
            {
                confidence -= 0.15;
            }
        }

        // Translations (German Splitter, French Urban Comics…) are other books for an English collection.
        if (TextMatch.IsForeignPublisher(volume.Publisher) && !TextMatch.SamePublisher(volume.Publisher, query.Publisher)
            && query.Language is null or "en" or "eng")
        {
            confidence -= 0.3;
        }

        if (query.Year is { } year && issue.Date is { } date)
        {
            if (date.Year == year)
            {
                confidence += 0.1;
                reasons.Add(MatchReason.Year);
            }
            else if (Math.Abs(date.Year - year) > 1)
            {
                confidence -= 0.1;
            }
        }

        if (query.Kind is { } wanted)
        {
            if (wanted == kind || (wanted == BookKind.Tpb && kind == BookKind.Omnibus))
            {
                confidence += 0.05;
                reasons.Add(MatchReason.Kind);
            }
            else
            {
                confidence -= 0.1;
            }
        }

        var record = new MetadataRecord
        {
            Source = SourceIds.ComicVine,
            Title = TitleOf(volume, issue, prefix, suffix, collected, kind, oneShot),
            SeriesName = oneShot ? null : kind == BookKind.Issue || (collected is not null && exactSeriesVolume) ? volume.Name : prefix,
            Number = oneShot ? null : number,
            Kind = kind,
            Date = issue.Date,
            Publisher = volume.Publisher,
            ComicVineId = $"4000-{issue.Id}",
            ComicVineVolumeId = volume.Id,
            VolumeIssueCount = volume.IssueCount,
            IsOneShot = oneShot,
            IsDetailed = false,
        };

        return new MetadataCandidate(record, Math.Round(Math.Clamp(confidence, 0, 1), 2), reasons);
    }

    private static BookKind KindOf(CvVolume volume, string? collected, bool shortVolume)
    {
        var name = TextMatch.Normalize(volume.Name);
        if (name.Contains("omnibus", StringComparison.Ordinal) || name.Contains("compendium", StringComparison.Ordinal))
        {
            return BookKind.Omnibus;
        }

        return collected is not null || (shortVolume && volume.IssueCount == 1) ? BookKind.Tpb : BookKind.Issue;
    }

    private static string TitleOf(CvVolume volume, CvIssueSummary issue, string prefix, string? suffix, string? collected, BookKind kind, bool oneShot)
    {
        if (oneShot)
        {
            return volume.Name;
        }

        if (kind == BookKind.Issue)
        {
            return issue.Name is { } name && TextMatch.CollectedNumber(name) is null
                ? $"{volume.Name} #{issue.IssueNumber}: {name}"
                : $"{volume.Name} #{issue.IssueNumber}";
        }

        var series = suffix is not null ? prefix : volume.Name;
        var title = collected is not null ? $"{series}, Vol. {collected}" : series;
        return suffix is not null ? $"{title}: {suffix}" : title;
    }

    /// <summary>Gives an ISBN record a series and number taken from its title.</summary>
    private static MetadataRecord Shape(MetadataRecord record)
    {
        if (record.Title is not { } title || TitleParser.GuessSeries(title) is not { Name.Length: > 0 } guess)
        {
            return record with { Credits = [] };
        }

        return record with
        {
            SeriesName = guess.Name,
            Number = guess.Number,
            Kind = title.Contains('#') ? BookKind.Issue : BookKind.Tpb,
            Credits = [], // Open Library and Google Books do not say who drew and who wrote
        };
    }

    /// <summary>Title and number agree (publisher is checked separately for the "two sources" rule).</summary>
    private static bool SameBook(MetadataRecord isbnRecord, MetadataRecord cv)
    {
        var sameSeries = TextMatch.Similarity(isbnRecord.SeriesName ?? isbnRecord.Title, cv.SeriesName ?? cv.Title) >= 0.85
                         || TextMatch.Similarity(isbnRecord.Title, cv.Title) >= 0.85;
        var sameNumber = isbnRecord.Number is null || cv.Number is null
            ? isbnRecord.Number is null && cv.Number is null
            : TextMatch.SameNumber(isbnRecord.Number, cv.Number);
        return sameSeries && sameNumber;
    }

    private static string PrefixOf(string volumeName)
    {
        var colon = volumeName.IndexOf(':');
        return colon > 0 ? volumeName[..colon].Trim() : volumeName.Trim();
    }

    private static string? SuffixOf(string volumeName)
    {
        var colon = volumeName.IndexOf(':');
        return colon > 0 && colon < volumeName.Length - 1 ? volumeName[(colon + 1)..].Trim() : null;
    }
}

/// <summary>The ComicVine calls the identifier needs (a fake replaces it in tests).</summary>
public interface IComicVineApi
{
    Task<bool> HasKeyAsync(CancellationToken ct);

    Task<IReadOnlyList<CvVolume>> SearchVolumesAsync(string query, CancellationToken ct, int limit = 30);

    Task<IReadOnlyList<CvIssueSummary>> GetIssuesAsync(IReadOnlyCollection<int> volumeIds, CancellationToken ct, string? issueNumber = null);

    Task<CvIssue?> GetIssueAsync(int id, CancellationToken ct);
}
