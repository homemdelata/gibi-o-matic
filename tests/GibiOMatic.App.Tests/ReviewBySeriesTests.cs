using System.Globalization;
using GibiOMatic.App.Resources;
using GibiOMatic.App.Services;
using GibiOMatic.App.ViewModels;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Settings;
using GibiOMatic.Library.Index;
using GibiOMatic.Metadata;
using GibiOMatic.Metadata.Identification;
using GibiOMatic.Metadata.Sources;

namespace GibiOMatic.App.Tests;

/// <summary>The Review by series (SPEC §7.1), driven through its view models.</summary>
public sealed class ReviewBySeriesTests : IDisposable
{
    private readonly AppTestContext _app = new();

    public void Dispose() => _app.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private LibrarySession Session => _app.Get<LibrarySession>();

    private static MetadataCandidate Candidate(int volumeId, string volumeName, string number) => new(new MetadataRecord
    {
        Source = SourceIds.ComicVine,
        Title = $"{volumeName}, Vol. {number}",
        SeriesName = volumeName,
        Number = number,
        Kind = BookKind.Tpb,
        Publisher = "Image",
        ComicVineId = $"4000-{volumeId}{number}",
        ComicVineVolumeId = volumeId,
        VolumeName = volumeName,
        VolumeIssueCount = 2,
        SourceName = $"{volumeName} #{number}",
    }, 0.7, [MatchReason.Title]);

    private async Task ImportAsync(params string[] fileNames)
    {
        await Session.CreateAsync(_app.Folder("Library"), Ct);
        await Session.Queue!.AddAsync(fileNames.Select((n, i) => SyntheticBooks.Pdf(_app.Folder(Path.Combine("Downloads", n)),
            new SyntheticBooks.PdfOptions { Pages = i + 1 })), Ct);
        await Session.Queue.WaitUntilIdleAsync(Ct);
        await Session.Queue.ConfirmAsync(Ct);
        await Session.Queue.WaitUntilIdleAsync(Ct);
        var books = await Session.Index!.GetBooksAsync(Ct);
        for (var waited = 0; waited < 10_000; waited += 25)
        {
            var counts = await Session.Identification!.GetCountsAsync(Ct);
            if (counts.Queued == 0 && counts.ToReview >= books.Count)
            {
                break;
            }

            await Task.Delay(25, Ct);
        }
    }

    [Fact]
    public async Task A_series_is_approved_with_one_decision_and_undone_at_once()
    {
        await _app.EnterUiThreadAsync();
        _app.Identifier.Respond = (book, _) => book.Title switch
        {
            "Deadly Class Vol1" => [Candidate(100, "Deadly Class", "1")],
            "DeadlyClass v2" => [Candidate(144, "Deadly Class: Kids of the Black Hole", "2")],
            "Deadly Class Vol3" => [Candidate(300, "Deadly Class: The Snake Pit", "3")],
            _ => [],
        };
        _app.ComicVine.Volumes.Add(new CvVolume(100, "Deadly Class", 2014, "Image", 2));
        _app.ComicVine.Issues.AddRange(
        [
            new CvIssueSummary(1001, 100, "Deadly Class", "1", "Reagan Youth", new PartialDate(2015)),
            new CvIssueSummary(1002, 100, "Deadly Class", "2", "Kids of the Black Hole", new PartialDate(2015)),
        ]);
        await ImportAsync("Deadly Class Vol1.pdf", "DeadlyClass_v2.pdf", "Deadly Class Vol3.pdf", "Plutona.pdf");
        var review = _app.Get<ReviewPageViewModel>();
        await review.OnNavigatedToAsync(Ct);

        await review.ShowBySeriesCommand.ExecuteAsync(null);

        Assert.True(_app.Get<ISettingsService>().Current.Review.BySeries);
        var series = review.Series;
        Assert.Equal(["Deadly Class", null], series.GroupItems.Select(g => g.Group?.Name));
        Assert.Equal(Strings.Series_OneBook, series.GroupItems[1].CountText);
        Assert.True(series.IsGroupOpen);
        Assert.NotNull(series.GroupItems[0].WarningText);

        // Every book found another volume; the user picks the series' own.
        series.Volumes.Single(v => v.Volume.Id == 100).PickCommand.Execute(null);
        await WaitAsync(() => series.IsIdle && series.Rows.All(r => r.HasMatch));
        Assert.Equal(["Deadly Class Vol. 1: Reagan Youth", "Deadly Class Vol. 2: Kids of the Black Hole", "Deadly Class Vol. 3: The Snake Pit"],
            series.Rows.Select(r => r.ResultTitle));
        Assert.True(series.Rows[1].Plan.ChangesVolume);
        Assert.False(series.Rows[2].Plan.ChangesVolume);

        series.KeepTitles = true;
        Assert.Equal("Deadly Class Vol1", series.Rows[0].ResultTitle);
        series.KeepTitles = false;
        series.Template = "{Series} #{Number:00}";
        Assert.Equal("Deadly Class #01", series.Rows[0].ResultTitle);

        series.AskApproveCommand.Execute(null);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Series_Confirm, 3, 1, 0), series.ConfirmText);
        await series.ApproveCommand.ExecuteAsync(null);

        var books = await Session.Index!.GetBooksAsync(Ct);
        var approved = books.Where(b => b.Metadata.Review.Status == ReviewStatus.Approved).ToList();
        Assert.Equal(3, approved.Count);
        Assert.All(approved, b => Assert.Equal("Deadly Class", b.Metadata.Series?.Name));
        Assert.Contains(approved, b => b.Metadata.Title == "Deadly Class #02");
        Assert.Equal("{Series} #{Number:00}", _app.Get<ISettingsService>().Current.Review.TitleTemplate);
        Assert.NotNull(series.ApprovedText);
        Assert.True(series.IsNoGroupOpen);

        await series.UndoCommand.ExecuteAsync(null);

        books = await Session.Index!.GetBooksAsync(Ct);
        Assert.All(books, b => Assert.Equal(ReviewStatus.Pending, b.Metadata.Review.Status));
        Assert.Contains(books, b => b.Metadata.Title == "DeadlyClass v2");
        Assert.Equal(3, series.GroupItems[0].Group?.Books.Count);
    }

    [Fact]
    public async Task Books_are_moved_between_groups_and_no_group_is_reviewed_by_book()
    {
        await _app.EnterUiThreadAsync();
        await ImportAsync("Saga Vol1.pdf", "Saga Vol2.pdf", "Saga Vol3.pdf", "Plutona.pdf");
        var review = _app.Get<ReviewPageViewModel>();
        await review.OnNavigatedToAsync(Ct);
        await review.ShowBySeriesCommand.ExecuteAsync(null);
        var series = review.Series;
        Assert.Equal("Saga", series.GroupItems[0].Name);
        Assert.Equal(3, series.Rows.Count);

        // A row dragged onto "No group".
        await series.GroupItems[^1].DropCommand.ExecuteAsync(series.Rows[2].BookId);
        Assert.Equal(2, series.Rows.Count);
        await series.GroupItems[^1].SelectCommand.ExecuteAsync(null);
        Assert.Equal(2, series.UngroupedBooks.Count);
        var plutona = series.UngroupedBooks.Single(b => b.Title == "Plutona").Book.BookId;

        // A group created from No group, then undone.
        foreach (var book in series.UngroupedBooks)
        {
            book.IsChecked = true;
        }

        await series.CreateGroupCommand.ExecuteAsync(null);
        Assert.True(series.IsGroupOpen);
        Assert.Equal(2, series.Rows.Count);
        await series.UngroupCommand.ExecuteAsync(null);
        Assert.True(series.IsNoGroupOpen);
        Assert.Equal(2, series.UngroupedBooks.Count);

        // A book of No group is reviewed on its own, By book.
        series.UngroupedBooks.Single(b => b.Book.BookId == plutona).ReviewCommand.Execute(null);
        await WaitAsync(() => review.IsByBook && review.Selected?.BookId == plutona);
        Assert.False(_app.Get<ISettingsService>().Current.Review.BySeries);
    }

    private static async Task WaitAsync(Func<bool> done)
    {
        for (var waited = 0; waited < 5_000 && !done(); waited += 20)
        {
            await Task.Delay(20, Ct);
        }

        Assert.True(done());
    }
}
