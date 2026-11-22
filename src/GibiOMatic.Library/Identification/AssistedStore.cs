using System.Text.Json;
using Dapper;
using GibiOMatic.Core.Json;
using GibiOMatic.Library.Index;
using GibiOMatic.Metadata;
using GibiOMatic.Metadata.Assisted;

namespace GibiOMatic.Library.Identification;

/// <summary>What a run for one book came to, after the app's checks.</summary>
public enum AssistedOutcome
{
    /// <summary>A ComicVine record of the edition, read again by the app.</summary>
    Found,

    /// <summary>No ComicVine record of the edition: a record built from the answer.</summary>
    EditionMissing,

    /// <summary>Claude could not tell which book it is.</summary>
    NotFound,

    /// <summary>The run failed (not logged in, timeout, no answer).</summary>
    Failed,
}

/// <summary>A run for one book, as stored and shown.</summary>
/// <param name="Message">Why it failed or what to know ("not-installed", "timeout", "comicvine:Unavailable").</param>
public sealed record AssistedBookResult(
    string BookId,
    AssistedLevel Level,
    AssistedOutcome Outcome,
    MetadataCandidate? Candidate,
    BookAnswer? Answer,
    string? Message,
    TimeSpan Duration,
    int Turns)
{
    public int PromptVersion { get; init; } = AssistedPrompts.Version;

    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>One book of a group run, after the app's checks.</summary>
/// <param name="Candidate">The edition's record when it passed the checks; null leaves the row unmatched.</param>
/// <param name="Problem">Why a proposed record was not used: "cover", "unreadable", "comicvine:…"; null when used or none was proposed.</param>
public sealed record AssistedGroupRow(
    string BookId,
    bool Belongs,
    string? Number,
    MetadataCandidate? Candidate,
    bool? CoverMatches,
    IReadOnlyList<string> Evidence,
    string? Doubts,
    string? Problem);

/// <summary>A book Claude says belongs to another series.</summary>
public sealed record AssistedOutsider(string BookId, string? Reason, string? SuggestedSeries);

/// <summary>A run for a group of the Review by series, after the app's checks (SPEC §8.4, "A run for a group").</summary>
public sealed record AssistedGroupResult(
    string GroupId,
    GroupSeries? Series,
    string? Pattern,
    IReadOnlyList<AssistedGroupRow> Rows,
    IReadOnlyList<AssistedOutsider> Outsiders,
    IReadOnlyList<string> MissingNumbers,
    IReadOnlyList<string> RepeatedNumbers,
    string? Notes,
    string? Message,
    TimeSpan Duration,
    int Turns)
{
    public bool Failed => Message is not null && Rows.Count == 0;

    public int PromptVersion { get; init; } = AssistedPrompts.Version;

    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Claude's checked answers, kept in the index so the Review shows them without asking again.</summary>
public sealed class AssistedStore(LibraryIndex index)
{
    public async Task SaveAsync(AssistedBookResult result, CancellationToken ct = default)
    {
        await using var connection = await index.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO assisted_answers (book_id, level, prompt_version, outcome, result_json, at)
            VALUES (@BookId, @level, @PromptVersion, @outcome, @json, @at)
            ON CONFLICT(book_id, level) DO UPDATE SET prompt_version = @PromptVersion, outcome = @outcome, result_json = @json, at = @at;
            """,
            new
            {
                result.BookId,
                level = Name(result.Level),
                result.PromptVersion,
                outcome = Name(result.Outcome),
                json = JsonSerializer.Serialize(result, GibiJson.Options),
                at = result.At.ToString("O"),
            }, cancellationToken: ct));
    }

    /// <summary>Every stored run of a book (quick, deep, group).</summary>
    public async Task<IReadOnlyList<AssistedBookResult>> GetAsync(string bookId, CancellationToken ct = default)
    {
        await using var connection = await index.OpenAsync(ct);
        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT result_json FROM assisted_answers WHERE book_id = @bookId;", new { bookId }, cancellationToken: ct));
        return rows.Select(Read<AssistedBookResult>).OfType<AssistedBookResult>().ToList();
    }

    /// <summary>The candidates of a book's runs that passed the checks.</summary>
    public async Task<IReadOnlyList<MetadataCandidate>> GetCandidatesAsync(string bookId, CancellationToken ct = default) =>
        (await GetAsync(bookId, ct)).Where(r => r.Candidate is not null).Select(r => r.Candidate!).ToList();

    /// <summary>Books already asked with the current prompts (so the automatic mode doesn't ask twice).</summary>
    public async Task<IReadOnlySet<string>> GetAnsweredAsync(CancellationToken ct = default)
    {
        await using var connection = await index.OpenAsync(ct);
        return (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT book_id FROM assisted_answers WHERE prompt_version >= @version;", new { version = AssistedPrompts.Version },
            cancellationToken: ct))).ToHashSet(StringComparer.Ordinal);
    }

    public async Task SaveGroupAsync(AssistedGroupResult result, CancellationToken ct = default)
    {
        await using var connection = await index.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO assisted_groups (group_id, result_json, at) VALUES (@GroupId, @json, @at)
            ON CONFLICT(group_id) DO UPDATE SET result_json = @json, at = @at;
            """,
            new { result.GroupId, json = JsonSerializer.Serialize(result, GibiJson.Options), at = result.At.ToString("O") }, cancellationToken: ct));
    }

    /// <summary>Groups of the Review by series that Claude already answered for.</summary>
    public async Task<IReadOnlySet<string>> GetAnsweredGroupsAsync(CancellationToken ct = default)
    {
        await using var connection = await index.OpenAsync(ct);
        return (await connection.QueryAsync<string>(new CommandDefinition("SELECT group_id FROM assisted_groups;", cancellationToken: ct)))
            .ToHashSet(StringComparer.Ordinal);
    }

    public async Task<AssistedGroupResult?> GetGroupAsync(string groupId, CancellationToken ct = default)
    {
        await using var connection = await index.OpenAsync(ct);
        var json = await connection.QueryFirstOrDefaultAsync<string>(new CommandDefinition(
            "SELECT result_json FROM assisted_groups WHERE group_id = @groupId;", new { groupId }, cancellationToken: ct));
        return json is null ? null : Read<AssistedGroupResult>(json);
    }

    /// <summary>A book left the library: its answers go too.</summary>
    public async Task DeleteAsync(string bookId, CancellationToken ct = default)
    {
        await using var connection = await index.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM assisted_answers WHERE book_id = @bookId;", new { bookId }, cancellationToken: ct));
    }

    private static T? Read<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, GibiJson.Options);
        }
        catch (JsonException)
        {
            return null; // written by a newer version
        }
    }

    private static string Name<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}
