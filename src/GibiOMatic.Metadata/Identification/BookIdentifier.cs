using System.Text.RegularExpressions;
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

    /// <summary>
    /// The book the user found on comicvine.gamespot.com: an issue link (…/4000-937068/) gives that
    /// issue; a volume link (…/4050-144057/) gives its issues. Empty when the link is not recognised.
    /// </summary>
    Task<IReadOnlyList<MetadataCandidate>> FromComicVineLinkAsync(BookMetadata book, string link, CancellationToken ct);
}

/// <summary>
/// SPEC §8.2: ISBN first (Open Library, Google Books), then ComicVine volumes (collected editions
/// are volumes of their own), each candidate scored by title, number, publisher, year and kind.
/// </summary>
public sealed partial class BookIdentifier(
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
                CoverUrl = candidate.Record.CoverUrl ?? agreeing.CoverUrl,
                CoverFullUrl = candidate.Record.CoverFullUrl ?? agreeing.CoverFullUrl,
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
                CoverUrl = candidate.Record.CoverUrl ?? issue.CoverUrl,
                CoverFullUrl = candidate.Record.CoverFullUrl ?? issue.CoverFullUrl,
                IsDetailed = true,
            },
        };
    }

    public async Task<IReadOnlyList<MetadataCandidate>> FromComicVineLinkAsync(BookMetadata book, string link, CancellationToken ct)
    {
        if (ParseComicVineLink(link) is not { } target)
        {
            return [];
        }

        var query = BookQuery.From(book);
        var issues = new List<CvIssueSummary>();
        CvVolume? volume;
        if (target.IsIssue)
        {
            if (await comicVine.GetIssueAsync(target.Id, ct) is not { } issue)
            {
                return [];
            }

            volume = await comicVine.GetVolumeAsync(issue.VolumeId, ct);
            issues.Add(new CvIssueSummary(issue.Id, issue.VolumeId, issue.VolumeName, issue.IssueNumber, issue.Name, issue.Date, issue.CoverUrl, issue.CoverFullUrl));
        }
        else
        {
            volume = await comicVine.GetVolumeAsync(target.Id, ct);
            if (volume is null)
            {
                return [];
            }

            issues.AddRange(await comicVine.GetIssuesAsync([volume.Id], ct));
        }

        volume ??= new CvVolume(issues[0].VolumeId, issues[0].VolumeName, null, null, issues.Count);

        // The user chose it: full confidence; the ones closest to the book first (for a volume link).
        var candidates = issues
            .Select(i => Score(i, volume, query))
            .OrderByDescending(c => c.Confidence)
            .Take(12)
            .Select(c => c with { Confidence = 1.0, Reasons = [MatchReason.ChosenByUser] })
            .ToList();
        if (candidates.Count > 0)
        {
            candidates[0] = await CompleteAsync(candidates[0], ct);
        }

        return candidates;
    }

    /// <summary>"https://comicvine.gamespot.com/deadly-class-…/4000-937068/" → issue 937068.</summary>
    public static (bool IsIssue, int Id)? ParseComicVineLink(string? link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            return null;
        }

        var m = Regex.Match(link, @"\b(4000|4050)-(\d+)\b");
        return m.Success && int.TryParse(m.Groups[2].Value, out var id) ? (m.Groups[1].Value == "4000", id) : null;
    }

    private async Task<List<MetadataCandidate>> SearchComicVineAsync(BookQuery query, CancellationToken ct)
    {
        // Most specific search first ("The Boys Over the Hill…"), then broader ones ("The Boys"),
        // stopping as soon as the book's own volume shows up; the volumes found are pooled.
        var volumes = new List<CvVolume>();
        var texts = query.SearchTexts;
        for (var i = 0; i < texts.Count; i++)
        {
            var isFallback = i == texts.Count - 1 && texts.Count > 1 && query.SearchText.Contains(':');
            if (isFallback && volumes.Any(v => NameScore(v, query) >= 0.6))
            {
                break; // the part before a colon is only tried when nothing else came close
            }

            var found = await comicVine.SearchVolumesAsync(texts[i], ct);
            volumes.AddRange(found.Where(v => volumes.All(x => x.Id != v.Id)));
            if (found.Any(v => TextMatch.Similarity(v.Name, query.FullName) >= 0.9))
            {
                break;
            }
        }

        var relevant = volumes
            .Select(v => (Volume: v, Score: NameScore(v, query)))
            .Where(x => x.Score >= 0.6)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => TextMatch.Similarity(x.Volume.Name, query.FullName))
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

        // "Book One: The Flight of the Prince" on issues #1 to #5 of Wynd names a story arc, not a
        // collected edition: a collected-edition number repeated in a volume is ignored.
        var arcIssues = issues
            .GroupBy(i => (i.VolumeId, Number: TextMatch.CollectedNumber(i.Name)))
            .Where(g => g.Key.Number is not null && g.Count() > 1)
            .SelectMany(g => g.Select(i => i.Id))
            .ToHashSet();

        var byId = relevant.ToDictionary(v => v.Id);
        return issues
            .Where(i => byId.ContainsKey(i.VolumeId))
            .DistinctBy(i => i.Id)
            .Select(i => Score(i, byId[i.VolumeId], query, arcIssues.Contains(i.Id)))
            .Where(c => c.Confidence > 0.25)
            .OrderByDescending(c => c.Confidence)
            .Take(MaxCandidates)
            .ToList();
    }

    /// <summary>
    /// How well a volume name matches: "Deadly Class: Reagan Youth" matches "Deadly Class", and
    /// "Robocop: Last Stand" matches "RoboCop: Last Stand" and "Hellboy: Krampusnacht" its full name.
    /// </summary>
    private static double NameScore(CvVolume volume, BookQuery query)
    {
        var series = query.Series ?? query.SearchText;
        return new[] { volume.Name, WithoutCreator(volume.Name) }.Distinct().Max(name =>
            Math.Max(TextMatch.Similarity(PrefixOf(name), series),
                Math.Max(TextMatch.Similarity(name, query.SearchText), TextMatch.Similarity(name, query.FullName))));
    }

    /// <param name="nameIsArc">The issue's name is a story arc shared with other issues ("Book One: …"), not a collected edition.</param>
    internal static MetadataCandidate Score(CvIssueSummary issue, CvVolume volume, BookQuery query, bool nameIsArc = false)
    {
        var prefix = PrefixOf(volume.Name);
        var suffix = SuffixOf(volume.Name);
        var collected = nameIsArc ? null : TextMatch.CollectedNumber(issue.Name);
        var shortVolume = volume.IssueCount <= 12;
        var series = query.Series ?? query.SearchText;
        var exactSeriesVolume = SameName(volume.Name, series) || SameName(WithoutCreator(volume.Name), series);
        var wantsCollected = query.Kind is BookKind.Tpb or BookKind.Omnibus;

        // A deluxe/hardcover series named like the series numbers its volumes by issue number.
        collected ??= !nameIsArc && shortVolume && volume.IssueCount > 1 && exactSeriesVolume && query.Kind != BookKind.Issue ? issue.IssueNumber : null;

        // A series that only ever had one collected edition: "Scotch McTiernan… Vol. 1" is the single
        // issue of the volume of that name.
        collected ??= volume.IssueCount == 1 && exactSeriesVolume && wantsCollected
                      && TextMatch.SameNumber(query.Number, "1") && TextMatch.SameNumber(issue.IssueNumber, "1") ? "1" : null;
        var kind = KindOf(volume, collected, shortVolume);
        var number = query.Kind == BookKind.Issue ? issue.IssueNumber : collected ?? (shortVolume ? null : issue.IssueNumber);
        var oneShot = volume.IssueCount == 1 && collected is null && suffix is null;

        var reasons = new List<MatchReason>();
        var titleSim = NameScore(volume, query);
        var confidence = 0.30 * titleSim;
        if (titleSim >= 0.85)
        {
            reasons.Add(MatchReason.Title);
        }

        if (SameName(PrefixOf(volume.Name), series) || SameName(PrefixOf(WithoutCreator(volume.Name)), series) || exactSeriesVolume)
        {
            confidence += 0.05;
        }

        // The subtitle says which book of the series this is: ComicVine writes it in the volume's
        // name ("Hellboy: Krampusnacht") or in the issue's ("Over the Hill with the Swords…").
        if (query.Subtitle is { } subtitle)
        {
            var issueNameSim = TextMatch.Similarity(IssueTitleOf(issue.Name), subtitle);
            var subtitleSim = Math.Max(TextMatch.Similarity(suffix, subtitle), issueNameSim);
            if (subtitleSim >= 0.7 || TextMatch.Similarity(volume.Name, query.FullName) >= 0.9)
            {
                confidence += 0.1;
                reasons.Add(MatchReason.Subtitle);

                // Without a number, the issue's name is what tells the books of the series apart
                // ("Parker - The Hunter" is the issue "The Hunter" of Richard Stark's Parker).
                if (query.Number is null && issueNameSim >= 0.7 && volume.IssueCount > 1)
                {
                    confidence += 0.25;
                }
            }
            else if (suffix is not null && TextMatch.Similarity(suffix, subtitle) < 0.4)
            {
                confidence -= 0.1; // another mini-series of the same series ("RoboCop: Roulette")
            }
        }

        if (query.SeriesYear is { } seriesYear && volume.StartYear is { } startYear)
        {
            confidence += startYear == seriesYear ? 0.1 : Math.Abs(startYear - seriesYear) > 1 ? -0.1 : 0;
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
            CoverUrl = issue.CoverUrl,
            CoverFullUrl = issue.CoverFullUrl,
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

    /// <summary>An issue name without its collected-edition number: "Volume 11: Over the Hill…" → "Over the Hill…".</summary>
    private static string? IssueTitleOf(string? name) => name is null ? null : CollectedNumberPrefix().Replace(name, string.Empty);

    /// <summary>
    /// "Richard Stark's Parker" → "Parker", "Frank Miller's RoboCop" → "RoboCop": a creator's full
    /// name is often left out of the book's title. One word ("Crogan's March") is part of the name.
    /// </summary>
    private static string WithoutCreator(string volumeName)
    {
        var m = CreatorPrefix().Match(volumeName);
        return m.Success ? volumeName[m.Length..] : volumeName;
    }

    [GeneratedRegex(@"^\s*(?:vol(?:ume)?|book|tpb|part)\.?\s*(?:\d+|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)\b\s*[:,-]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex CollectedNumberPrefix();

    [GeneratedRegex(@"^(?:[\p{L}.\-]+\s+){1,2}[\p{L}.\-]+['’]s\s+(?=\S)")]
    private static partial Regex CreatorPrefix();

    /// <summary>Same name, ignoring case, punctuation and a leading "The" ("The Transformers" = "Transformers").</summary>
    private static bool SameName(string a, string b)
    {
        static string Key(string s) => TextMatch.Normalize(s) is var n && n.StartsWith("the ", StringComparison.Ordinal) ? n[4..] : TextMatch.Normalize(s);
        return Key(a).Length > 0 && Key(a) == Key(b);
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

    Task<CvVolume?> GetVolumeAsync(int id, CancellationToken ct);
}
