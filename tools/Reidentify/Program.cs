using System.Text;
using System.Text.Json;
using GibiOMatic.App.Services;
using GibiOMatic.Core;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Json;
using GibiOMatic.Core.Settings;
using GibiOMatic.Library;
using GibiOMatic.Library.Editing;
using GibiOMatic.Library.Identification;
using GibiOMatic.Metadata;
using GibiOMatic.Metadata.Http;
using GibiOMatic.Metadata.Identification;
using Microsoft.Extensions.DependencyInjection;

namespace GibiOMatic.Reidentify;

/// <summary>
/// Checks the approved books of the library against the current <see cref="BookIdentifier"/>: each
/// book, as it was before its approval, is identified again, and what the current version would have
/// proposed is compared with what was approved. Usage, from the repository root, with the app closed:
/// <list type="bullet">
/// <item><c>dotnet run --project tools/Reidentify -- report &lt;folder&gt;</c>: only reads; writes
/// <c>report.md</c> and <c>plan.json</c> to the folder.</item>
/// <item><c>dotnet run --project tools/Reidentify -- apply &lt;folder&gt;/plan.json</c>: saves the
/// entries of the plan marked <c>"apply": true</c> (the previous metadata goes to the history).</item>
/// </list>
/// </summary>
internal static class Program
{
    /// <summary>Fields that only depend on how the ComicVine record is read, not on which record it is.</summary>
    private static readonly ProposalField[] ReadingFields = [ProposalField.Title, ProposalField.Series, ProposalField.Number, ProposalField.Kind, ProposalField.OneShot];

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length != 2 || args[0] is not ("report" or "apply"))
        {
            Console.Error.WriteLine("Usage: report <folder> | apply <plan.json>");
            return 2;
        }

        using var services = ServiceRegistration.Build(AppPaths.ForCurrentUser());
        var settings = await services.GetRequiredService<ISettingsService>().LoadAsync();
        if (settings.LibraryPath is not { } root)
        {
            Console.Error.WriteLine("No library in the settings.");
            return 1;
        }

        var libraries = services.GetRequiredService<LibraryService>();
        var library = await libraries.OpenAsync(root);
        var editor = services.GetRequiredService<BookEditor>();
        return args[0] == "report"
            ? await ReportAsync(services, settings, library, editor, Path.GetFullPath(args[1]))
            : await ApplyAsync(library, editor, Path.GetFullPath(args[1]));
    }

    private static async Task<int> ReportAsync(IServiceProvider services, AppSettings settings, Core.Libraries.LibraryInfo library, BookEditor editor, string folder)
    {
        Directory.CreateDirectory(folder);
        var identifier = services.GetRequiredService<IBookIdentifier>();
        var queue = services.GetRequiredService<IdentificationQueueFactory>().Create(library);
        var books = (await services.GetRequiredService<LibraryService>().GetIndex(library).GetBooksAsync())
            .Where(b => b.Metadata.Review.Status == ReviewStatus.Approved)
            .OrderBy(b => b.Metadata.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var plan = new List<PlanEntry>();
        var skipped = new List<(string Title, string Why)>();
        var unchanged = 0;
        for (var i = 0; i < books.Count; i++)
        {
            var current = books[i].Metadata;
            Console.WriteLine($"[{i + 1}/{books.Count}] {current.Title}");
            try
            {
                var finding = await CheckAsync(current, settings, identifier, queue, editor, library);
                switch (finding)
                {
                    case PlanEntry entry:
                        plan.Add(entry);
                        break;
                    case string why:
                        skipped.Add((current.Title, why));
                        break;
                    default:
                        unchanged++;
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One book that fails does not stop the others; it is listed as not checked.
                skipped.Add((current.Title, $"error: {ex.GetType().Name}: {ex.Message}"));
            }
        }

        await File.WriteAllTextAsync(Path.Combine(folder, "plan.json"), JsonSerializer.Serialize(plan, GibiJson.Options));
        await File.WriteAllTextAsync(Path.Combine(folder, "report.md"), Report(books.Count, unchanged, plan, skipped));
        Console.WriteLine($"{books.Count} approved: {unchanged} unchanged, {plan.Count} to fix, {skipped.Count} not checked. See {folder}.");
        return 0;
    }

    /// <returns>A <see cref="PlanEntry"/>, the reason the book was not checked, or null when nothing changes.</returns>
    private static async Task<object?> CheckAsync(BookMetadata current, AppSettings settings, IBookIdentifier identifier,
        IdentificationQueue queue, BookEditor editor, Core.Libraries.LibraryInfo library)
    {
        if (current.Identifiers.ComicVine is not { } cvId)
        {
            return "not from ComicVine";
        }

        // The book as it was before it was approved: the reading of the file, not of ComicVine.
        var history = await editor.GetHistoryAsync(library, current.Id);
        if (history.FirstOrDefault(h => h.Metadata.Review.Status == ReviewStatus.Pending)?.Metadata is not { } before)
        {
            return "no version from before the approval";
        }

        var stored = (await queue.GetAsync(current.Id))?.Result;
        var approved = stored?.Candidates.FirstOrDefault(c => c.Record.ComicVineId == cvId);
        var chosenByUser = approved?.Reasons.Contains(MatchReason.ChosenByUser) == true;

        var search = await identifier.IdentifyAsync(before, settings.Metadata, null, default);
        var same = search.Candidates.FirstOrDefault(c => c.Record.ComicVineId == cvId)
                   ?? (await identifier.FromComicVineLinkAsync(before, cvId, default)).FirstOrDefault();
        if (same is null)
        {
            return $"ComicVine issue {cvId} not found";
        }

        // 1. Another book: the search now prefers another ComicVine record, clearly.
        if (!chosenByUser && search.Best is { } best && best.Record.ComicVineId != cvId
            && best.Confidence >= 0.75 && best.Confidence - ScoreIn(search, cvId) >= 0.15)
        {
            var detailed = await identifier.CompleteAsync(best, default);
            var rows = Proposal.Compare(current, detailed.Record);
            return new PlanEntry(current.Id, current.Title, Pages(current), "other-book", Apply: false, before.Title,
                $"now: {same.Record.SourceName} [{cvId}, {same.Record.Publisher}] ({ScoreIn(search, cvId):P0}) → " +
                $"{detailed.Record.SourceName} [{detailed.Record.ComicVineId}, {detailed.Record.Publisher}] ({best.Confidence:P0})",
                detailed.Record, Proposal.DefaultSelection(rows).ToList(), Changes(rows));
        }

        // 2. Same record, read differently: only the fields that came from ComicVine are changed
        //    (those the user kept from the file at approval stay as they are).
        var record = same.Record;
        var fields = Proposal.Compare(current, record)
            .Where(r => r.Changed && ReadingFields.Contains(r.Field))
            .Where(r => approved is null
                ? ValueOf(before, r.Field) != ValueOf(current, r.Field)
                : Proposal.Compare(current, approved.Record).FirstOrDefault(x => x.Field == r.Field)?.Changed != true)
            .Select(r => r.Field)
            .ToList();
        if (fields.Count == 0)
        {
            return null;
        }

        var changes = Proposal.Compare(current, record).Where(r => fields.Contains(r.Field)).ToList();
        return new PlanEntry(current.Id, current.Title, Pages(current), "reading", Apply: true, before.Title,
            $"{record.SourceName} [{cvId}]", record, fields, Changes(changes));
    }

    private static string? ValueOf(BookMetadata book, ProposalField field) => field switch
    {
        ProposalField.Title => book.Title,
        ProposalField.Series => book.Series?.Name,
        ProposalField.Number => book.Series?.Number,
        ProposalField.Kind => book.Kind.ToString(),
        ProposalField.OneShot => book.OneShot.ToString(),
        _ => null,
    };

    private static double ScoreIn(IdentificationResult result, string cvId) =>
        result.Candidates.FirstOrDefault(c => c.Record.ComicVineId == cvId)?.Confidence ?? 0;

    private static int? Pages(BookMetadata book) => book.Files.FirstOrDefault()?.Pages;

    private static List<string> Changes(IEnumerable<FieldChange> rows) =>
        rows.Where(r => r.Changed && r.Field != ProposalField.Cover).Select(r => $"{r.Field}: {r.Current ?? "—"} → {r.Proposed ?? "—"}").ToList();

    private static string Report(int total, int unchanged, List<PlanEntry> plan, List<(string Title, string Why)> skipped)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Approved books checked again").AppendLine();
        sb.AppendLine($"{total} approved · {unchanged} unchanged · {plan.Count(p => p.Finding == "reading")} read differently · " +
                      $"{plan.Count(p => p.Finding == "other-book")} maybe another book · {skipped.Count} not checked").AppendLine();
        foreach (var group in plan.GroupBy(p => p.Finding))
        {
            sb.AppendLine(group.Key == "reading" ? "## Same ComicVine record, read differently" : "## The search now prefers another record").AppendLine();
            foreach (var p in group)
            {
                sb.AppendLine($"### {p.Title}");
                sb.AppendLine($"- file before approval: `{p.Before}` · {p.Pages?.ToString() ?? "?"} pages · {p.Detail}");
                foreach (var change in p.Changes)
                {
                    sb.AppendLine($"- {change}");
                }

                sb.AppendLine();
            }
        }

        if (skipped.Count > 0)
        {
            sb.AppendLine("## Not checked").AppendLine();
            foreach (var group in skipped.GroupBy(s => s.Why))
            {
                sb.AppendLine($"- **{group.Key}** ({group.Count()}): {string.Join("; ", group.Select(g => g.Title))}");
            }
        }

        return sb.ToString();
    }

    private static async Task<int> ApplyAsync(Core.Libraries.LibraryInfo library, BookEditor editor, string planPath)
    {
        var plan = JsonSerializer.Deserialize<List<PlanEntry>>(await File.ReadAllTextAsync(planPath), GibiJson.Options) ?? [];
        var log = new StringBuilder();
        foreach (var entry in plan.Where(p => p.Apply))
        {
            var saved = await editor.UpdateAsync(library, entry.BookId, book => Proposal.Apply(book, entry.Record, entry.Fields.ToHashSet()));
            var line = $"{DateTimeOffset.Now:u} {entry.BookId} {entry.Title} → {saved.Metadata.Title} ({string.Join(", ", entry.Fields)})";
            Console.WriteLine(line);
            log.AppendLine(line);
        }

        await File.AppendAllTextAsync(Path.Combine(Path.GetDirectoryName(planPath)!, "applied.log"), log.ToString());
        Console.WriteLine($"{plan.Count(p => p.Apply)} books saved.");
        return 0;
    }
}

/// <param name="Apply">Whether <c>apply</c> saves it; edit the plan to change it.</param>
/// <param name="Before">Title of the book before its approval (the file's reading).</param>
internal sealed record PlanEntry(
    string BookId, string Title, int? Pages, string Finding, bool Apply, string Before, string Detail,
    MetadataRecord Record, List<ProposalField> Fields, List<string> Changes);
