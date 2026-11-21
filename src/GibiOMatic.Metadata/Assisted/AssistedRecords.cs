using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Settings;
using GibiOMatic.Metadata.Identification;
using GibiOMatic.Metadata.Sources;

namespace GibiOMatic.Metadata.Assisted;

/// <summary>How the app starts itself for Claude Code: the MCP server and the site guard hook.</summary>
/// <param name="Executable">The app's executable (<see cref="Environment.ProcessPath"/>).</param>
/// <param name="Args">Arguments before the mode (empty for the installed app).</param>
public sealed record ClaudeBridge(string Executable, IReadOnlyList<string> Args)
{
    public const string McpMode = "--claude-mcp";
    public const string HookMode = "--claude-hook";

    public IReadOnlyList<string> McpArgs(string folder) => [.. Args, McpMode, folder];

    public IReadOnlyList<string> HookArgs(string folder) => [.. Args, HookMode, folder];
}

/// <summary>Turns Claude's answers into candidates the Review knows (SPEC §8.4, step 5).</summary>
public static class AssistedRecords
{
    private static readonly ProposalField[] CreditFields =
        [ProposalField.Writers, ProposalField.Artists, ProposalField.Inkers, ProposalField.Colorists, ProposalField.Letterers, ProposalField.CoverArtists];

    public static BookKind? KindOf(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "issue" => BookKind.Issue,
        "tpb" or "hc" => BookKind.Tpb,
        "omnibus" => BookKind.Omnibus,
        "artbook" => BookKind.Artbook,
        "book" => BookKind.Book,
        _ => null,
    };

    public static PartialDate? DateOf(string? text) => Json.Date(text);

    /// <summary>What Claude said, kept with the candidate.</summary>
    public static AssistedEvidence Evidence(BookAnswer answer, AssistedLevel level, string model, bool? coverMatches, bool editionMissing) => new()
    {
        Level = level,
        Model = model,
        Evidence = answer.Evidence,
        Doubts = answer.Doubts,
        FieldSources = answer.Sources,
        CoverMatches = coverMatches,
        CoverExplanation = answer.CoverExplanation,
        EditionMissing = editionMissing,
        ModelConfidence = answer.Confidence,
        // Credits read from the credits page mix in the publisher's staff ("logo designed by…").
        Unticked = editionMissing ? CreditFields : [],
        PromptVersion = AssistedPrompts.Version,
        At = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// The ComicVine record the app read again by its id, with what the answer adds that ComicVine
    /// lacks (ISBN, page count). The confidence is the model's.
    /// </summary>
    public static MetadataCandidate FromComicVine(MetadataCandidate reread, BookAnswer answer, AssistedEvidence evidence)
    {
        var isbn = Core.Text.Isbn.Find(answer.Isbn ?? string.Empty);
        var record = reread.Record with
        {
            Isbn = reread.Record.Isbn ?? (isbn is null ? null : Core.Text.Isbn.ToIsbn13(isbn)),
            PageCount = reread.Record.PageCount ?? answer.PageCount,
        };
        return new MetadataCandidate(record, Math.Round(Math.Clamp(answer.Confidence, 0, 1), 2), [MatchReason.Claude]) { Assisted = evidence };
    }

    /// <summary>
    /// An edition ComicVine doesn't have (SPEC §8.4, "Edition missing from ComicVine"): the series from
    /// its ComicVine volume when there is one, the other fields from the answer.
    /// </summary>
    public static MetadataCandidate FromAnswer(BookAnswer answer, AssistedEvidence evidence, CvVolume? series)
    {
        var isbn = Core.Text.Isbn.Find(answer.Isbn ?? string.Empty);
        var credits = answer.Credits
            .SelectMany(c => c.Role.Split(',', '/', '&').Select(r => (Role: ComicVineClient.RoleOf(r), c.Name)))
            .Where(c => c.Role is not null && !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => new Credit(c.Role!.Value, c.Name.Trim()))
            .Distinct()
            .OrderBy(c => c.Role)
            .ToList();
        var seriesName = answer.Series ?? (series is null ? null : BookIdentifier.PrefixOf(series.Name));
        var record = new MetadataRecord
        {
            Source = SourceIds.Claude,
            Title = answer.Title ?? (seriesName is null ? null : answer.Number is null ? seriesName : $"{seriesName}, Vol. {answer.Number}"),
            SeriesName = seriesName,
            Number = answer.Number,
            Kind = KindOf(answer.Kind),
            Date = DateOf(answer.ReleaseDate),
            Publisher = answer.Publisher ?? series?.Publisher,
            Imprint = answer.Imprint,
            Credits = credits,
            Summary = answer.Summary,
            Isbn = isbn is null ? null : Core.Text.Isbn.ToIsbn13(isbn),
            PageCount = answer.PageCount,
            ComicVineVolumeId = series?.Id,
            VolumeName = series?.Name,
            VolumeStartYear = series?.StartYear,
            VolumeIssueCount = series?.IssueCount,
            SourceName = answer.Title ?? seriesName,
            IsDetailed = true,
        };
        return new MetadataCandidate(record, Math.Round(Math.Clamp(answer.Confidence, 0, 1), 2), [MatchReason.Claude]) { Assisted = evidence };
    }

    /// <summary>
    /// The result stored for the book: Claude's candidates first (those with a matching cover before
    /// the others, one per record), then all the algorithm's. The algorithm's are kept even when they
    /// repeat Claude's record, since that agreement counts for the automatic approval; the Review
    /// shows each record once. Older answers of Claude are replaced by <paramref name="assisted"/>.
    /// </summary>
    public static IdentificationResult Merge(IdentificationResult result, IEnumerable<MetadataCandidate> assisted)
    {
        var mine = assisted
            .OrderBy(c => c.Assisted?.CoverMatches switch { true => 0, null => 1, false => 2 })
            .ThenBy(c => c.Assisted?.EditionMissing == true ? 1 : 0)
            .ThenByDescending(c => c.Assisted?.Level == AssistedLevel.Deep)
            .ThenByDescending(c => c.Confidence)
            .ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<MetadataCandidate>();
        foreach (var candidate in mine)
        {
            if (candidate.Record.ComicVineId is { } id && !seen.Add(id))
            {
                continue;
            }

            merged.Add(candidate);
        }

        merged.AddRange(result.Candidates.Where(c => c.Assisted is null));
        return result with { Candidates = merged };
    }

    /// <summary>
    /// A quick answer that should be asked again more deeply (SPEC §8.4, "Two levels"): it found only
    /// the series, the covers differ, or its confidence is below <paramref name="minimum"/>.
    /// </summary>
    public static bool NeedsDeep(MetadataCandidate? candidate, double minimum) =>
        candidate?.Assisted is not { } evidence
        || evidence.EditionMissing
        || evidence.CoverMatches == false
        || candidate.Confidence < minimum;
}

/// <summary>
/// Approving Claude's answer without the user (SPEC §8.4, "Approval"): off by default, and then only
/// when every condition the app checks itself holds. The model's confidence alone never approves.
/// </summary>
public static class AssistedApproval
{
    /// <param name="algorithm">The algorithm's result, without Claude's candidates.</param>
    /// <param name="otherRuns">Claude's candidates from the other runs (quick, deep, group) of the same book.</param>
    public static bool CanApprove(BookMetadata book, MetadataCandidate candidate, IdentificationResult? algorithm, IEnumerable<MetadataCandidate> otherRuns,
        ClaudeSettings claude, MetadataSettings metadata, out string reason)
    {
        reason = Check(book, candidate, algorithm, otherRuns, claude, metadata);
        return reason == "ok";
    }

    private static string Check(BookMetadata book, MetadataCandidate candidate, IdentificationResult? algorithm, IEnumerable<MetadataCandidate> otherRuns,
        ClaudeSettings claude, MetadataSettings metadata)
    {
        if (!claude.Enabled || !claude.AutoApprove || metadata.AlwaysReview)
        {
            return "off";
        }

        if (book.Review.Status != ReviewStatus.Pending)
        {
            return "approved";
        }

        if (candidate.Assisted is not { } evidence)
        {
            return "not-claude";
        }

        if (candidate.Confidence < claude.AutoApproveMinimum)
        {
            return "low-confidence";
        }

        if (candidate.Record.ComicVineId is not { } id || !id.StartsWith("4000-", StringComparison.Ordinal) || evidence.EditionMissing)
        {
            return "no-edition-record";
        }

        if (evidence.CoverMatches != true)
        {
            return "cover";
        }

        var algorithmAgrees = algorithm?.Candidates.FirstOrDefault(c => c.Assisted is null)?.Record.ComicVineId == id;
        var runsAgree = otherRuns.Any(c => !ReferenceEquals(c, candidate) && c.Assisted?.Level != evidence.Level && c.Record.ComicVineId == id);
        var isbnAgrees = algorithm?.Candidates.Any(c => c.Assisted is null && c.IsbnMatch && c.Record.ComicVineId == id) == true;
        if (!algorithmAgrees && !runsAgree && !isbnAgrees)
        {
            return "no-agreement";
        }

        if (Proposal.Compare(book, candidate.Record).Any(r => r.Conflict))
        {
            return "conflict";
        }

        return "ok";
    }
}
