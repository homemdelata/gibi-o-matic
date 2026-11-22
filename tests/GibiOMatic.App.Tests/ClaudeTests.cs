using System.Text.Json;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
using GibiOMatic.App.ViewModels;
using GibiOMatic.Core.Settings;
using GibiOMatic.Metadata;
using GibiOMatic.Metadata.Assisted;
using GibiOMatic.Metadata.Identification;

namespace GibiOMatic.App.Tests;

/// <summary>Claude Code in memory: never the real command line in tests.</summary>
public sealed class FakeClaudeCode : IClaudeCode
{
    public ClaudeStatus Status { get; set; } = new(true, "2.1.316", true, "claude.ai", "claude");

    public Func<ClaudeRun, string?> Answer { get; set; } = _ => null;

    public List<ClaudeRun> Runs { get; } = [];

    /// <summary>When set, a run waits for it: Claude is still looking.</summary>
    public TaskCompletionSource? Hold { get; set; }

    public Task<ClaudeStatus> GetStatusAsync(CancellationToken ct) => Task.FromResult(Status);

    public async Task<ClaudeRunResult> RunAsync(ClaudeRun run, CancellationToken ct)
    {
        Runs.Add(run);
        if (Hold is { } hold)
        {
            await hold.Task.WaitAsync(ct);
        }

        return Answer(run) is { } output
            ? new ClaudeRunResult(output, null, 9, TimeSpan.FromSeconds(15))
            : ClaudeRunResult.Failed("timeout", TimeSpan.FromMinutes(5));
    }
}

/// <summary>Claude in Settings and in the Review (SPEC §8.4).</summary>
public sealed class ClaudeTests : IDisposable
{
    private readonly AppTestContext _app = new();

    public void Dispose() => _app.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private LibrarySession Session => _app.Get<LibrarySession>();

    private async Task OpenAndImportAsync(params string[] fileNames)
    {
        await Session.CreateAsync(_app.Folder("Library"), Ct);
        await Session.Queue!.AddAsync(fileNames.Select((n, i) => SyntheticBooks.Pdf(_app.Folder(Path.Combine("Downloads", n)), new SyntheticBooks.PdfOptions { Pages = i + 2 })), Ct);
        await Session.Queue.WaitUntilIdleAsync(Ct);
        await Session.Queue.ConfirmAsync(Ct);
        await Session.Queue.WaitUntilIdleAsync(Ct);
        for (var waited = 0; waited < 10_000 && (await Session.Identification!.GetCountsAsync(Ct)) is { Queued: > 0 } or { ToReview: 0 }; waited += 25)
        {
            await Task.Delay(25, Ct);
        }
    }

    [Fact]
    public async Task Settings_show_the_state_and_save_the_choices_dropping_bad_sites()
    {
        await _app.EnterUiThreadAsync();
        await Session.CreateAsync(_app.Folder("Library"), Ct);
        _app.Claude.Status = _app.Claude.Status with { LoggedIn = false };
        var page = _app.Get<SettingsPageViewModel>();

        await page.OnNavigatedToAsync(Ct);
        Assert.False(page.ClaudeReady);
        Assert.Contains("claude auth login", page.ClaudeStatusText);
        Assert.False(page.ClaudeEnabled);

        page.ClaudeEnabled = true;
        page.QuickModel = "sonnet";
        page.ClaudeAutomatic = true;
        page.ClaudeAutoApprove = true;
        page.ClaudeAutoApproveMinimum = 90;
        page.ClaudeSites = "https://www.imagecomics.com/\nhumblebundle.com\namazon.com";
        await page.SaveCommand.ExecuteAsync(null);

        var saved = _app.Get<ISettingsService>().Current.Claude;
        Assert.True(saved.Enabled);
        Assert.Equal(new ClaudeLevelSettings("sonnet", "low"), saved.Quick);
        Assert.Equal(ClaudeRunMode.Automatic, saved.When);
        Assert.True(saved.AutoApprove);
        Assert.Equal(0.9, saved.AutoApproveMinimum, 3);
        Assert.Equal(["imagecomics.com", "amazon.com"], saved.Sites);
        Assert.Contains("humblebundle.com", page.StatusMessage);

        page.RestoreClaudeSitesCommand.Execute(null);
        Assert.Contains("leagueofcomicgeeks.com", page.ClaudeSites);
    }

    [Fact]
    public async Task Asking_claude_in_the_review_brings_its_candidate_with_what_it_saw()
    {
        await _app.EnterUiThreadAsync();
        await _app.Get<ISettingsService>().SaveAsync(new AppSettings { Claude = new ClaudeSettings { Enabled = true } }, Ct);
        _app.Claude.Answer = _ => JsonSerializer.Serialize(new BookAnswer
        {
            Identified = true,
            Title = "House of Slaughter, Vol. 1",
            ComicVineUrl = "https://comicvine.gamespot.com/house-of-slaughter-1/4000-1/",
            Confidence = 0.93,
            Evidence = ["The credits page reads House of Slaughter Volume One."],
            Doubts = "The cover could be a variant.",
        }, Core.Json.GibiJson.Options);
        await OpenAndImportAsync("SKYBOUND LOGO white.pdf", "Monstress_Template.pdf");
        var review = _app.Get<ReviewPageViewModel>();
        await review.OnNavigatedToAsync(Ct);

        Assert.True(review.ClaudeEnabled);
        Assert.Contains("2", review.AskClaudeAllText);
        Assert.Empty(review.Candidates);
        Assert.False(review.Selected!.HasBest);
        Assert.Null(review.Selected.BestText);

        await review.AskClaudeCommand.ExecuteAsync(null);

        // The list shows Claude's confidence beside the book's current score, each named on hover.
        var item = review.Selected!;
        Assert.True(item.IsBestFromClaude);
        Assert.Equal(0.93.ToString("0%", System.Globalization.CultureInfo.CurrentCulture), item.BestText);
        Assert.Contains("Claude", item.BestTip);
        Assert.Contains(item.Score.ToString(System.Globalization.CultureInfo.CurrentCulture), item.ScoreTip);

        var run = Assert.Single(_app.Claude.Runs);
        Assert.Equal("haiku", run.Model);
        var candidate = review.Candidates[0];
        Assert.True(candidate.IsFromClaude);
        Assert.True(candidate.IsSelected);
        Assert.Contains("haiku", candidate.ClaudeLine);
        Assert.Equal(["The credits page reads House of Slaughter Volume One."], candidate.Evidence);
        Assert.Contains("variant", candidate.DoubtsText);
        Assert.Null(review.ClaudeMessage);

        // The batch counts only the books without an answer yet, and asks first.
        Assert.Contains("1", review.AskClaudeAllText);
        review.AskClaudeAllCommand.Execute(null);
        Assert.NotNull(review.AskClaudeAllConfirmText);
    }

    [Fact]
    public async Task While_claude_looks_at_a_book_the_others_can_be_reviewed()
    {
        await _app.EnterUiThreadAsync();
        await _app.Get<ISettingsService>().SaveAsync(new AppSettings { Claude = new ClaudeSettings { Enabled = true } }, Ct);
        _app.Identifier.Respond = (_, _) => [FakeIdentifier.Candidate("Saga, Vol. 1", "Saga", "1", 0.6, MatchReason.Title)];
        _app.Claude.Answer = _ => JsonSerializer.Serialize(new BookAnswer
        {
            Identified = true,
            Title = "House of Slaughter, Vol. 1",
            ComicVineUrl = "https://comicvine.gamespot.com/house-of-slaughter-1/4000-1/",
            Confidence = 0.93,
        }, Core.Json.GibiJson.Options);
        await OpenAndImportAsync("alpha.pdf", "beta.pdf");
        var review = _app.Get<ReviewPageViewModel>();
        await review.OnNavigatedToAsync(Ct);
        var first = review.Items[0].BookId;
        var second = review.Items[1].BookId;

        _app.Claude.Hold = new TaskCompletionSource();
        var asking = review.AskClaudeCommand.ExecuteAsync(null);
        Assert.True(review.IsClaudeRunning);
        Assert.False(review.ShowCandidates);
        Assert.True(review.Items[0].IsClaudeRunning);

        // Another book shows as it is, with its own candidates, and can be worked on.
        review.Items[1].SelectCommand.Execute(null);
        await Task.Delay(50, Ct);
        Assert.Equal(second, review.Selected?.BookId);
        Assert.False(review.IsBusy);
        Assert.False(review.IsClaudeRunning);
        Assert.True(review.ShowCandidates);
        Assert.Equal("Saga, Vol. 1", review.SelectedCandidate?.Title);
        Assert.True(review.ApproveCommand.CanExecute(null));

        // Back on the first book, the wait shows again.
        review.Items[0].SelectCommand.Execute(null);
        await Task.Delay(50, Ct);
        Assert.True(review.IsClaudeRunning);
        Assert.False(review.ShowCandidates);
        Assert.False(review.ShowClaudeEvidence);

        // Claude answers while the second book is on screen: it stays there; the first book's line shows the answer.
        review.Items[1].SelectCommand.Execute(null);
        await Task.Delay(50, Ct);
        _app.Claude.Hold.SetResult();
        await asking;
        Assert.Equal(second, review.Selected?.BookId);
        Assert.Equal("Saga, Vol. 1", review.SelectedCandidate?.Title);
        Assert.False(review.IsBusy);
        var answered = review.Items.Single(i => i.BookId == first);
        Assert.False(answered.IsClaudeRunning);
        Assert.True(answered.IsBestFromClaude);

        answered.SelectCommand.Execute(null);
        await Task.Delay(50, Ct);
        Assert.True(review.ShowClaudeEvidence);
        Assert.Equal("House of Slaughter, Vol. 1", review.SelectedCandidate?.Title);
    }

    /// <summary>A group run answers with the series and no record (its books stay without a candidate); a book run gets nothing.</summary>
    private static string? GroupOnly(ClaudeRun run) => File.Exists(Path.Combine(run.Folder, "group.json"))
        ? JsonSerializer.Serialize(new GroupAnswer
        {
            Series = new GroupAnswerSeries { Name = "Saga", Publisher = "Image", StartYear = 2012 },
            Pattern = "Each trade is an issue of the series.",
            Books = [],
            MissingNumbers = [],
        }, Core.Json.GibiJson.Options)
        : null;

    [Fact]
    public async Task By_series_the_batch_asks_claude_once_per_series()
    {
        await _app.EnterUiThreadAsync();
        await _app.Get<ISettingsService>().SaveAsync(new AppSettings { Claude = new ClaudeSettings { Enabled = true } }, Ct);
        _app.Claude.Answer = GroupOnly;
        await OpenAndImportAsync("Saga Vol. 1.pdf", "Saga Vol. 2.pdf", "Wynd Vol. 1.pdf", "Wynd Vol. 2.pdf", "Plutona.pdf");
        var review = _app.Get<ReviewPageViewModel>();
        await review.OnNavigatedToAsync(Ct);
        Assert.Equal(string.Format(Strings.Review_AskClaudeAll, 5), review.AskClaudeAllText);

        // By series, the button counts the groups (not the books, nor "No group").
        await review.ShowBySeriesCommand.ExecuteAsync(null);
        Assert.Equal(string.Format(Strings.Review_AskClaudeAllSeries, 2), review.AskClaudeAllText);
        Assert.Equal(Strings.Series_AskClaudeHelp, review.AskClaudeAllHelp);
        review.AskClaudeAllCommand.Execute(null);
        Assert.NotNull(review.AskClaudeAllConfirmText);
        await review.ConfirmAskClaudeAllCommand.ExecuteAsync(null);

        var assisted = Session.Assisted!;
        for (var waited = 0; waited < 10_000 && (assisted.HasBatch || assisted.IsBusy); waited += 25)
        {
            await Task.Delay(25, Ct);
        }

        Assert.Equal(2, _app.Claude.Runs.Count);
        Assert.All(_app.Claude.Runs, r => Assert.True(File.Exists(Path.Combine(r.Folder, "group.json"))));

        // Both groups have their answer; the open one shows Claude's suggestion.
        await review.Series.RefreshAsync(ct: Ct);
        await Task.Delay(100, Ct);
        Assert.Null(review.AskClaudeAllText);
        Assert.True(review.Series.HasClaudeResult);

        // Back by book, the button counts books again (the group runs found no record for them).
        await review.ShowByBookCommand.ExecuteAsync(null);
        Assert.Equal(string.Format(Strings.Review_AskClaudeAll, 5), review.AskClaudeAllText);
    }

    [Fact]
    public async Task While_claude_looks_at_a_series_the_other_groups_can_be_reviewed()
    {
        await _app.EnterUiThreadAsync();
        await _app.Get<ISettingsService>().SaveAsync(new AppSettings { Claude = new ClaudeSettings { Enabled = true } }, Ct);
        _app.Claude.Answer = GroupOnly;
        await OpenAndImportAsync("Saga Vol. 1.pdf", "Saga Vol. 2.pdf", "Wynd Vol. 1.pdf", "Wynd Vol. 2.pdf");
        var review = _app.Get<ReviewPageViewModel>();
        await review.ShowBySeriesCommand.ExecuteAsync(null);
        var series = review.Series;
        Assert.Equal("Saga", series.Selected?.Name);

        _app.Claude.Hold = new TaskCompletionSource();
        var asking = series.AskClaudeCommand.ExecuteAsync(null);
        Assert.True(series.IsClaudeRunning);
        Assert.False(series.IsIdle);
        Assert.True(series.GroupItems[0].IsClaudeRunning);

        // Another group opens as it is and can be worked on.
        await series.GroupItems[1].SelectCommand.ExecuteAsync(null);
        Assert.Equal("Wynd", series.Selected?.Name);
        Assert.True(series.IsIdle);
        Assert.False(series.IsClaudeRunning);
        Assert.False(series.HasClaudeResult);

        // Back on the first group, the wait shows again, with Cancel.
        await series.GroupItems[0].SelectCommand.ExecuteAsync(null);
        Assert.True(series.IsClaudeRunning);

        // Claude answers while the other group is open: it stays open; the first one shows the answer when opened.
        await series.GroupItems[1].SelectCommand.ExecuteAsync(null);
        _app.Claude.Hold.SetResult();
        await asking;
        Assert.Equal("Wynd", series.Selected?.Name);
        Assert.False(series.HasClaudeResult);
        Assert.False(series.GroupItems[0].IsClaudeRunning);

        await series.GroupItems[0].SelectCommand.ExecuteAsync(null);
        Assert.True(series.HasClaudeResult);
        Assert.Equal("Saga", series.SeriesName);
    }

    [Fact]
    public async Task A_run_that_fails_says_why_and_the_book_keeps_its_candidates()
    {
        await _app.EnterUiThreadAsync();
        await _app.Get<ISettingsService>().SaveAsync(new AppSettings { Claude = new ClaudeSettings { Enabled = true } }, Ct);
        _app.Identifier.Respond = (_, _) => [FakeIdentifier.Candidate("Saga, Vol. 1", "Saga", "1", 0.6, MatchReason.Title)];
        await OpenAndImportAsync("saga.pdf");
        var review = _app.Get<ReviewPageViewModel>();
        await review.OnNavigatedToAsync(Ct);

        Assert.True(review.Selected!.IsBestFromSearch);
        Assert.Equal(0.6.ToString("0%", System.Globalization.CultureInfo.CurrentCulture), review.Selected.BestText);
        Assert.Equal(CheckStatus.Warn, review.Selected.BestLevel);

        await review.SearchDeeperCommand.ExecuteAsync(null);

        Assert.Equal("sonnet", Assert.Single(_app.Claude.Runs).Model);
        Assert.NotNull(review.ClaudeMessage);
        Assert.False(Assert.Single(review.Candidates).IsFromClaude);
    }

    [Fact]
    public async Task With_claude_off_the_review_offers_nothing()
    {
        await _app.EnterUiThreadAsync();
        await OpenAndImportAsync("saga.pdf");
        var review = _app.Get<ReviewPageViewModel>();
        await review.OnNavigatedToAsync(Ct);

        Assert.False(review.ClaudeEnabled);
        Assert.Null(review.AskClaudeAllText);
        await review.AskClaudeCommand.ExecuteAsync(null);
        Assert.Empty(_app.Claude.Runs);
    }
}
