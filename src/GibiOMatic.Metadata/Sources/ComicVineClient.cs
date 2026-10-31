using System.Globalization;
using System.Text.Json;
using GibiOMatic.Core.Domain;
using GibiOMatic.Metadata.Http;
using GibiOMatic.Metadata.Identification;

namespace GibiOMatic.Metadata.Sources;

public sealed record CvVolume(int Id, string Name, int? StartYear, string? Publisher, int IssueCount);

public sealed record CvIssueSummary(int Id, int VolumeId, string VolumeName, string? IssueNumber, string? Name, PartialDate? Date,
    string? CoverUrl = null, string? CoverFullUrl = null);

public sealed record CvIssue(
    int Id,
    int VolumeId,
    string VolumeName,
    string? IssueNumber,
    string? Name,
    PartialDate? Date,
    IReadOnlyList<Credit> Credits,
    string? Description,
    string? SiteUrl,
    string? CoverUrl = null,
    string? CoverFullUrl = null);

/// <summary>
/// ComicVine API (personal key per user). Collected editions are volumes of their own
/// ("Deadly Class: A Fond Farewell Part One", 1 issue named "Volume 11"), so identification
/// searches volumes, lists their issues in one request and fetches details only for the best match.
/// Limits: 200 requests per resource per hour, plus a velocity check; see docs/RESEARCH.md.
/// </summary>
public sealed class ComicVineClient(SourceClient client, IComicVineKeyProvider keys) : IComicVineApi
{
    public async Task<bool> HasKeyAsync(CancellationToken ct) => !string.IsNullOrWhiteSpace(await keys.GetKeyAsync(ct));

    private const string Base = "https://comicvine.gamespot.com/api";

    /// <summary>Volumes whose name matches <paramref name="query"/> (ComicVine's own ranking).</summary>
    public async Task<IReadOnlyList<CvVolume>> SearchVolumesAsync(string query, CancellationToken ct, int limit = 30)
    {
        var url = $"{Base}/search/?format=json&resources=volume&limit={limit}&field_list=id,name,start_year,publisher,count_of_issues&query={Uri.EscapeDataString(query)}";
        using var doc = await GetAsync("search", url, ct);
        return doc.RootElement.Items("results")
            .Select(v => new CvVolume(v.Int("id") ?? 0, v.Str("name") ?? string.Empty, v.Int("start_year"),
                v.Prop("publisher")?.Str("name"), v.Int("count_of_issues") ?? 0))
            .Where(v => v.Id > 0)
            .ToList();
    }

    /// <summary>
    /// Issues of the given volumes in one request (up to 100), optionally only those with
    /// <paramref name="issueNumber"/> (both filters combine, checked against the live API).
    /// </summary>
    public async Task<IReadOnlyList<CvIssueSummary>> GetIssuesAsync(IReadOnlyCollection<int> volumeIds, CancellationToken ct, string? issueNumber = null)
    {
        if (volumeIds.Count == 0)
        {
            return [];
        }

        var filter = "volume:" + string.Join('|', volumeIds.Order());
        if (issueNumber is not null)
        {
            filter += ",issue_number:" + Uri.EscapeDataString(issueNumber);
        }

        var url = $"{Base}/issues/?format=json&limit=100&sort=cover_date:asc&field_list=id,name,issue_number,cover_date,store_date,volume,image&filter={filter}";
        using var doc = await GetAsync("issues", url, ct);
        return doc.RootElement.Items("results")
            .Select(i => new CvIssueSummary(i.Int("id") ?? 0, i.Prop("volume")?.Int("id") ?? 0, i.Prop("volume")?.Str("name") ?? string.Empty,
                i.Str("issue_number"), i.Str("name"), DateOf(i), CoverOf(i), FullCoverOf(i)))
            .Where(i => i.Id > 0)
            .ToList();
    }

    public async Task<CvIssue?> GetIssueAsync(int id, CancellationToken ct)
    {
        var url = $"{Base}/issue/4000-{id.ToString(CultureInfo.InvariantCulture)}/?format=json&field_list=id,name,issue_number,cover_date,store_date,volume,person_credits,description,deck,site_detail_url,image";
        using var doc = await GetAsync("issue", url, ct);
        if (doc.RootElement.Prop("results") is not { ValueKind: JsonValueKind.Object } i)
        {
            return null;
        }

        var credits = new List<Credit>();
        foreach (var person in i.Items("person_credits"))
        {
            if (person.Str("name") is not { } name)
            {
                continue;
            }

            // Roles come comma separated ("letterer, designer"); creative roles only.
            foreach (var role in (person.Str("role") ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (RoleOf(role) is { } mapped && !credits.Contains(new Credit(mapped, name)))
                {
                    credits.Add(new Credit(mapped, name));
                }
            }
        }

        return new CvIssue(
            i.Int("id") ?? id,
            i.Prop("volume")?.Int("id") ?? 0,
            i.Prop("volume")?.Str("name") ?? string.Empty,
            i.Str("issue_number"),
            i.Str("name"),
            DateOf(i),
            credits.OrderBy(c => c.Role).ToList(),
            Json.HtmlToText(i.Str("description")) ?? Json.Clean(i.Str("deck")),
            i.Str("site_detail_url"),
            CoverOf(i),
            FullCoverOf(i));
    }

    public async Task<CvVolume?> GetVolumeAsync(int id, CancellationToken ct)
    {
        var url = $"{Base}/volume/4050-{id.ToString(CultureInfo.InvariantCulture)}/?format=json&field_list=id,name,start_year,publisher,count_of_issues";
        using var doc = await GetAsync("volume", url, ct);
        return doc.RootElement.Prop("results") is { ValueKind: JsonValueKind.Object } v
            ? new CvVolume(v.Int("id") ?? id, v.Str("name") ?? string.Empty, v.Int("start_year"), v.Prop("publisher")?.Str("name"), v.Int("count_of_issues") ?? 0)
            : null;
    }

    /// <summary>Checks the key with the smallest possible request.</summary>
    public async Task<bool> TestKeyAsync(CancellationToken ct)
    {
        try
        {
            using var doc = await GetAsync("types", $"{Base}/types/?format=json&field_list=id", ct);
            return true;
        }
        catch (MetadataSourceException ex) when (ex.Error == MetadataSourceError.InvalidKey)
        {
            return false;
        }
    }

    /// <summary>
    /// ComicVine roles → credit roles (SPEC §8.2 item 4): only creative roles; editors and
    /// designers stay out, the old Calibre plugin mixed them with the authors.
    /// </summary>
    internal static CreditRole? RoleOf(string role) => role.Trim().ToLowerInvariant() switch
    {
        "writer" or "plotter" or "scripter" => CreditRole.Writer,
        "artist" or "penciler" or "penciller" or "breakdowns" => CreditRole.Artist,
        "inker" => CreditRole.Inker,
        "colorist" or "colourist" => CreditRole.Colorist,
        "letterer" => CreditRole.Letterer,
        "cover" => CreditRole.Cover,
        "translator" => CreditRole.Translator,
        _ => null,
    };

    /// <summary>A small rendition of the cover (about 160 px wide), enough to recognise the book.</summary>
    private static string? CoverOf(JsonElement issue) =>
        issue.Prop("image") is { } image ? image.Str("small_url") ?? image.Str("medium_url") ?? image.Str("thumb_url") : null;

    /// <summary>The original upload (or the largest scaled one), for replacing the book's cover.</summary>
    private static string? FullCoverOf(JsonElement issue) =>
        issue.Prop("image") is { } image ? image.Str("original_url") ?? image.Str("super_url") ?? image.Str("screen_large_url") : null;

    /// <summary>The on-sale date is the real publication date; the cover date runs months ahead.</summary>
    private static PartialDate? DateOf(JsonElement issue) => Json.Date(issue.Str("store_date")) ?? Json.Date(issue.Str("cover_date"));

    private async Task<JsonDocument> GetAsync(string resource, string url, CancellationToken ct)
    {
        var key = await keys.GetKeyAsync(ct)
                  ?? throw new MetadataSourceException(SourceIds.ComicVine, MetadataSourceError.InvalidKey, "No ComicVine key.");

        // ComicVine answers HTTP 200 with status_code 107 when the quota is exceeded,
        // and 100 for an invalid key; neither is cached, nor are incomplete search results.
        var body = await client.GetAsync(resource, url, "api_key=" + Uri.EscapeDataString(key), ct,
            isRateLimited: b => StatusOf(b) == 107,
            isCacheable: b => StatusOf(b) == 1 && !(resource == "search" && IsIncompleteSearch(b)));
        var doc = JsonDocument.Parse(body);
        if (resource == "search" && IsIncompleteSearch(doc.RootElement))
        {
            doc.Dispose();
            throw new MetadataSourceException(SourceIds.ComicVine, MetadataSourceError.BadResponse,
                "ComicVine returned search results without publishers or issue counts; try again later.");
        }

        var status = doc.RootElement.Int("status_code");
        if (status == 100)
        {
            doc.Dispose();
            throw new MetadataSourceException(SourceIds.ComicVine, MetadataSourceError.InvalidKey, "Invalid ComicVine key.");
        }

        if (status is not (1 or 101))
        {
            var error = doc.RootElement.Str("error");
            doc.Dispose();
            throw new MetadataSourceException(SourceIds.ComicVine, MetadataSourceError.BadResponse, $"ComicVine status {status}: {error}");
        }

        return doc;
    }

    /// <summary>
    /// For a few days in October 2026 ComicVine's search answered without the publisher and with
    /// zero issues for every volume; read as true, that hides the right volume (a 72-issue series
    /// looks like a one-shot). Several results that all lack both are such an answer.
    /// </summary>
    internal static bool IsIncompleteSearch(JsonElement root)
    {
        var results = root.Items("results").ToList();
        return results.Count >= 3
               && results.All(v => v.Prop("publisher") is not { ValueKind: JsonValueKind.Object } && (v.Int("count_of_issues") ?? 0) == 0);
    }

    private static bool IsIncompleteSearch(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return IsIncompleteSearch(doc.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int? StatusOf(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Int("status_code");
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Where the ComicVine key comes from (protected settings, or the environment in development).</summary>
public interface IComicVineKeyProvider
{
    Task<string?> GetKeyAsync(CancellationToken ct);
}
