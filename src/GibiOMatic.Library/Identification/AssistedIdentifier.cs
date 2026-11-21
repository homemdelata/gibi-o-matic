using System.Text.Json;
using GibiOMatic.Conversion.Images;
using GibiOMatic.Core;
using GibiOMatic.Core.Json;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Core.Settings;
using GibiOMatic.Library.Index;
using GibiOMatic.Metadata;
using GibiOMatic.Metadata.Assisted;
using GibiOMatic.Metadata.Http;
using GibiOMatic.Metadata.Identification;
using GibiOMatic.Metadata.Sources;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Library.Identification;

/// <summary>
/// One run of the user's Claude Code and the app's checks of its answer (SPEC §8.4): the folder is
/// filled, <c>claude -p</c> runs in it, and the answer becomes candidates only after the app read
/// the ComicVine record again by its id and compared the covers. Nothing is saved to the book here.
/// </summary>
public sealed class AssistedIdentifier(
    IClaudeCode claude,
    IBookIdentifier identifier,
    IComicVineApi comicVine,
    CoverImageCache covers,
    ISettingsService settings,
    AppPaths paths,
    ClaudeBridge bridge,
    ILogger<AssistedIdentifier> logger)
{
    /// <summary>Run folders kept for checking what Claude saw and did (the oldest are deleted).</summary>
    private const int KeptRuns = 30;

    private static readonly TimeSpan BookTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan GroupTimeout = TimeSpan.FromMinutes(15);

    /// <summary>A safety stop only: on a subscription the runs use the plan, not dollars.</summary>
    private const decimal BookBudget = 3m;
    private const decimal GroupBudget = 5m;

    public string RunsDirectory => Path.Combine(paths.DataDirectory, "claude", "runs");

    private (ClaudeStatus Status, DateTimeOffset At)? _ready;

    /// <summary>Installed and logged in; asked again when it was not, or after 10 minutes.</summary>
    public async Task<ClaudeStatus> GetStatusAsync(CancellationToken ct = default)
    {
        if (_ready is { } known && known.Status.IsReady && DateTimeOffset.UtcNow - known.At < TimeSpan.FromMinutes(10))
        {
            return known.Status;
        }

        var status = await claude.GetStatusAsync(ct);
        _ready = (status, DateTimeOffset.UtcNow);
        return status;
    }

    /// <param name="previous">The quick search's answer, given to a deep search.</param>
    public async Task<AssistedBookResult> IdentifyBookAsync(LibraryInfo library, CatalogBook book, IdentificationResult? current, AssistedLevel level,
        BookAnswer? previous, CancellationToken ct)
    {
        var config = settings.Current.Claude;
        var levelSettings = level == AssistedLevel.Deep ? config.Deep : config.Quick;
        var folder = NewRunFolder(book.Metadata.Id, level);
        try
        {
            await Task.Run(() => AssistedFolders.WriteBook(library, book, current, folder, previous, ct), ct);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not prepare the Claude folder of book {BookId}", book.Metadata.Id);
            return new AssistedBookResult(book.Metadata.Id, level, AssistedOutcome.Failed, null, null, "book-unreadable", TimeSpan.Zero, 0);
        }

        var run = await RunAsync(folder, level, levelSettings, config, ct);
        if (!run.Succeeded)
        {
            return new AssistedBookResult(book.Metadata.Id, level, AssistedOutcome.Failed, null, null, run.Error, run.Duration, run.Turns);
        }

        var answer = Deserialize<BookAnswer>(run.Output!);
        if (answer is null)
        {
            return new AssistedBookResult(book.Metadata.Id, level, AssistedOutcome.Failed, null, null, "unreadable-answer", run.Duration, run.Turns);
        }

        var (outcome, candidate, message) = await CheckAsync(book, Path.Combine(folder, AssistedFolders.CoverFile), answer, level, levelSettings.Model, ct);
        logger.LogInformation("Claude ({Level}) for book {BookId}: {Outcome} in {Seconds:0}s, {Turns} turns", level, book.Metadata.Id, outcome,
            run.Duration.TotalSeconds, run.Turns);
        return new AssistedBookResult(book.Metadata.Id, level, outcome, candidate, answer, message, run.Duration, run.Turns);
    }

    /// <summary>One run for a whole group of the Review by series; every row is checked like a single answer.</summary>
    public async Task<AssistedGroupResult> IdentifyGroupAsync(LibraryInfo library, ReviewGroup group, CancellationToken ct)
    {
        var config = settings.Current.Claude;
        var folder = NewRunFolder(group.Id.Replace(':', '-'), AssistedLevel.Group);
        try
        {
            await Task.Run(() => AssistedFolders.WriteGroup(library, group, folder, ct), ct);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not prepare the Claude folder of group {GroupId}", group.Id);
            return Failed(group.Id, "book-unreadable", TimeSpan.Zero, 0);
        }

        var run = await RunAsync(folder, AssistedLevel.Group, config.Group, config, ct);
        if (!run.Succeeded)
        {
            return Failed(group.Id, run.Error, run.Duration, run.Turns);
        }

        if (Deserialize<GroupAnswer>(run.Output!) is not { } answer)
        {
            return Failed(group.Id, "unreadable-answer", run.Duration, run.Turns);
        }

        var rows = new List<AssistedGroupRow>();
        var byId = group.Books.ToDictionary(b => b.BookId, StringComparer.Ordinal);
        foreach (var book in answer.Books.Where(b => byId.ContainsKey(b.Id)).DistinctBy(b => b.Id))
        {
            rows.Add(await CheckRowAsync(library, byId[book.Id], book, config.Group.Model, ct));
        }

        GroupSeries? series = null;
        if (answer.Series.Name is { Length: > 0 } name)
        {
            CvVolume? volume = null;
            if (BookIdentifier.ParseComicVineLink(answer.Series.ComicVineVolumeUrl) is { IsIssue: false } link)
            {
                try
                {
                    volume = await comicVine.GetVolumeAsync(link.Id, ct);
                }
                catch (Exception ex) when (ex is MetadataSourceException or HttpRequestException)
                {
                    logger.LogWarning(ex, "Could not read the volume Claude proposed for group {GroupId}", group.Id);
                }
            }

            series = new GroupSeries(name, answer.Series.Publisher ?? volume?.Publisher, answer.Series.StartYear ?? volume?.StartYear, volume?.Id,
                volume?.IssueCount ?? 0);
        }

        logger.LogInformation("Claude (group) for {GroupId}: {Matched} of {Count} books in {Seconds:0}s, {Turns} turns", group.Id,
            rows.Count(r => r.Candidate is not null), group.Books.Count, run.Duration.TotalSeconds, run.Turns);
        return new AssistedGroupResult(group.Id, series, answer.Pattern, rows,
            answer.NotInGroup.Where(o => byId.ContainsKey(o.Id)).Select(o => new AssistedOutsider(o.Id, o.Reason, o.SuggestedSeries)).ToList(),
            answer.MissingNumbers, answer.RepeatedNumbers ?? [], answer.Notes, null, run.Duration, run.Turns);
    }

    private static AssistedGroupResult Failed(string groupId, string? message, TimeSpan duration, int turns) =>
        new(groupId, null, null, [], [], [], [], null, message ?? "error", duration, turns);

    private async Task<ClaudeRunResult> RunAsync(string folder, AssistedLevel level, ClaudeLevelSettings levelSettings, ClaudeSettings config,
        CancellationToken ct)
    {
        var status = await GetStatusAsync(ct);
        if (!status.IsReady)
        {
            return ClaudeRunResult.Failed(status.Installed ? "not-logged-in" : "not-installed", TimeSpan.Zero);
        }

        var sites = SiteGuard.Clean(config.Sites, out _);
        var web = config.UseWeb && sites.Count > 0;
        await File.WriteAllLinesAsync(Path.Combine(folder, SiteGuard.SitesFile), sites, ct);
        var mcp = Path.Combine(folder, "mcp.json");
        var hookSettings = Path.Combine(folder, "settings.json");
        await File.WriteAllTextAsync(mcp, ComicVineMcpServer.Config(bridge.Executable, bridge.McpArgs(folder)), ct);
        await File.WriteAllTextAsync(hookSettings, SiteGuard.Settings(SiteGuard.Command(bridge.Executable, bridge.HookArgs(folder)), web), ct);
        IReadOnlyList<string> tools = web ? ["Read", "WebSearch", "WebFetch"] : ["Read"];
        var group = level == AssistedLevel.Group;
        var run = new ClaudeRun(folder, levelSettings.Model, levelSettings.Effort, AssistedPrompts.Request(level), AssistedPrompts.SystemPrompt(level),
            AssistedPrompts.Schema(level), tools, mcp, hookSettings, group ? GroupTimeout : BookTimeout, group ? GroupBudget : BookBudget);
        var result = await claude.RunAsync(run, ct);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "answer.json"), result.Output ?? result.Error ?? string.Empty, ct);
        }
        catch (IOException)
        {
        }

        return result;
    }

    /// <summary>Checks one answer: the ComicVine record read again by its id, the covers compared.</summary>
    internal async Task<(AssistedOutcome Outcome, MetadataCandidate? Candidate, string? Message)> CheckAsync(CatalogBook book, string coverPath, BookAnswer answer,
        AssistedLevel level, string model, CancellationToken ct)
    {
        if (!answer.Identified && answer.Title is null && answer.ComicVineUrl is null)
        {
            return (AssistedOutcome.NotFound, null, null);
        }

        string? message = null;
        var link = BookIdentifier.ParseComicVineLink(answer.ComicVineUrl);
        if (link is { IsIssue: true } issue)
        {
            try
            {
                var reread = await identifier.FromComicVineLinkAsync(book.Metadata, $"4000-{issue.Id}", ct);
                if (reread.FirstOrDefault() is { } found)
                {
                    var same = await SameCoverAsync(coverPath, found.Record.CoverUrl ?? found.Record.CoverFullUrl, ct);
                    var evidence = AssistedRecords.Evidence(answer, level, model, same, editionMissing: false);
                    return (AssistedOutcome.Found, AssistedRecords.FromComicVine(found, answer, evidence), null);
                }

                message = "comicvine:not-found";
            }
            catch (Exception ex) when (ex is MetadataSourceException or HttpRequestException)
            {
                logger.LogWarning(ex, "Could not read again the record Claude proposed for book {BookId}", book.Metadata.Id);
                message = ex is MetadataSourceException mse ? $"comicvine:{mse.Error}" : "comicvine:Unavailable";
            }
        }

        // No ComicVine record of the edition: the series from its volume, the rest from the answer.
        if (answer.Title is null && answer.Series is null)
        {
            return (AssistedOutcome.NotFound, null, message);
        }

        CvVolume? series = null;
        var volumeLink = BookIdentifier.ParseComicVineLink(answer.ComicVineSeriesUrl) ?? (link is { IsIssue: false } v ? v : null);
        if (volumeLink is { IsIssue: false } volume)
        {
            try
            {
                series = await comicVine.GetVolumeAsync(volume.Id, ct);
            }
            catch (Exception ex) when (ex is MetadataSourceException or HttpRequestException)
            {
                logger.LogInformation(ex, "Could not read the series Claude proposed for book {BookId}", book.Metadata.Id);
            }
        }

        var built = AssistedRecords.FromAnswer(answer, AssistedRecords.Evidence(answer, level, model, null, editionMissing: true), series);
        return (AssistedOutcome.EditionMissing, built, message);
    }

    private async Task<AssistedGroupRow> CheckRowAsync(LibraryInfo library, ReviewGroupBook book, GroupAnswerBook row, string model, CancellationToken ct)
    {
        if (!row.Belongs || BookIdentifier.ParseComicVineLink(row.ComicVineIssueUrl) is not { IsIssue: true } issue)
        {
            return new AssistedGroupRow(book.BookId, row.Belongs, row.Number, null, null, row.Evidence, row.Doubts, null);
        }

        try
        {
            var reread = await identifier.FromComicVineLinkAsync(book.Book.Metadata, $"4000-{issue.Id}", ct);
            if (reread.FirstOrDefault() is not { } found)
            {
                return new AssistedGroupRow(book.BookId, true, row.Number, null, null, row.Evidence, row.Doubts, "comicvine:not-found");
            }

            var cover = Path.Combine(library.RootPath, book.Book.Folder, LibraryLayout.CoverFileName);
            var same = await SameCoverAsync(cover, found.Record.CoverUrl ?? found.Record.CoverFullUrl, ct);
            var evidence = new AssistedEvidence
            {
                Level = AssistedLevel.Group,
                Model = model,
                Evidence = row.Evidence,
                Doubts = row.Doubts,
                CoverMatches = same,
                ModelConfidence = row.Confidence,
                PromptVersion = AssistedPrompts.Version,
                At = DateTimeOffset.UtcNow,
            };
            var candidate = new MetadataCandidate(found.Record, Math.Round(Math.Clamp(row.Confidence, 0, 1), 2), [MatchReason.Claude]) { Assisted = evidence };

            // A record whose cover differs from the book's is not used: the row stays unmatched.
            return same == false
                ? new AssistedGroupRow(book.BookId, true, row.Number, null, false, row.Evidence, row.Doubts, "cover")
                : new AssistedGroupRow(book.BookId, true, row.Number, candidate, same, row.Evidence, row.Doubts, null);
        }
        catch (Exception ex) when (ex is MetadataSourceException or HttpRequestException)
        {
            logger.LogWarning(ex, "Could not read again the record Claude proposed for book {BookId}", book.BookId);
            return new AssistedGroupRow(book.BookId, true, row.Number, null, null, row.Evidence, row.Doubts,
                ex is MetadataSourceException mse ? $"comicvine:{mse.Error}" : "comicvine:Unavailable");
        }
    }

    /// <summary>The book's cover and the record's, compared by a perceptual hash; null when either is missing.</summary>
    private async Task<bool?> SameCoverAsync(string bookCover, string? recordCoverUrl, CancellationToken ct)
    {
        if (!File.Exists(bookCover) || await covers.GetAsync(recordCoverUrl, ct) is not { } recordCover)
        {
            return null;
        }

        return await Task.Run(() => PerceptualHash.Same(PerceptualHash.Compute(bookCover), PerceptualHash.Compute(recordCover)), ct);
    }

    private string NewRunFolder(string id, AssistedLevel level)
    {
        Directory.CreateDirectory(RunsDirectory);
        try
        {
            foreach (var old in new DirectoryInfo(RunsDirectory).GetDirectories().OrderByDescending(d => d.CreationTimeUtc).Skip(KeptRuns - 1))
            {
                old.Delete(recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not delete an old Claude run folder");
        }

        var name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{level.ToString().ToLowerInvariant()}-{(id.Length > 12 ? id[^12..] : id)}";
        var folder = Path.Combine(RunsDirectory, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private T? Deserialize<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, GibiJson.Options);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Claude's answer could not be read");
            return null;
        }
    }
}
