using GibiOMatic.App.Services;
using GibiOMatic.App.ViewModels;
using GibiOMatic.Core.Settings;

namespace GibiOMatic.App.Tests;

/// <summary>The order and the text filter of the Review's lists.</summary>
public sealed class ReviewListTests : IDisposable
{
    private readonly AppTestContext _app = new();

    public void Dispose() => _app.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private LibrarySession Session => _app.Get<LibrarySession>();

    private async Task ImportAsync(params string[] fileNames)
    {
        await Session.CreateAsync(_app.Folder("Library"), Ct);
        await Session.Queue!.AddAsync(fileNames.Select((n, i) => SyntheticBooks.Pdf(_app.Folder(Path.Combine("Downloads", n)), new SyntheticBooks.PdfOptions { Pages = i + 2 })), Ct);
        await Session.Queue.WaitUntilIdleAsync(Ct);
        await Session.Queue.ConfirmAsync(Ct);
        await Session.Queue.WaitUntilIdleAsync(Ct);
        for (var waited = 0; waited < 10_000 && (await Session.Identification!.GetCountsAsync(Ct)) is var c && (c.Queued > 0 || c.ToReview < fileNames.Length); waited += 25)
        {
            await Task.Delay(25, Ct);
        }
    }

    [Fact]
    public async Task Books_are_listed_by_title_and_the_order_and_a_filter_can_be_chosen()
    {
        await _app.EnterUiThreadAsync();
        await ImportAsync("Wynd Vol. 1.pdf", "Ascender Vol. 2.pdf", "Lá Fora Vol. 1.pdf", "Saga Vol. 3.pdf");
        var review = _app.Get<ReviewPageViewModel>();
        await review.OnNavigatedToAsync(Ct);

        Assert.Equal(["Ascender Vol. 2", "Lá Fora Vol. 1", "Saga Vol. 3", "Wynd Vol. 1"], review.Items.Select(i => i.Title));

        review.SelectedSort = review.SortOptions.Single(o => o.Sort == ReviewSort.TitleDescending);
        await Task.Delay(50, Ct);
        Assert.Equal(["Wynd Vol. 1", "Saga Vol. 3", "Lá Fora Vol. 1", "Ascender Vol. 2"], review.Items.Select(i => i.Title));
        Assert.Equal(ReviewSort.TitleDescending, _app.Get<ISettingsService>().Current.Review.Sort);

        // Case and accents are ignored; the selected book follows the filter.
        review.FilterText = "la fora";
        await Task.Delay(50, Ct);
        Assert.Equal("Lá Fora Vol. 1", Assert.Single(review.Items).Title);
        Assert.Equal("Lá Fora Vol. 1", review.Selected?.Title);

        review.FilterText = "nothing like it";
        await Task.Delay(50, Ct);
        Assert.Empty(review.Items);
        Assert.Contains("nothing like it", review.FilterEmptyText);

        review.FilterText = null;
        await Task.Delay(50, Ct);
        Assert.Equal(4, review.Items.Count);
        Assert.Null(review.FilterEmptyText);
    }

    [Fact]
    public async Task Groups_are_filtered_by_their_name_or_any_of_their_books()
    {
        await _app.EnterUiThreadAsync();
        await ImportAsync("Saga Vol. 1.pdf", "Saga Vol. 2.pdf", "Wynd Vol. 1.pdf", "Wynd Vol. 2 - The Throne.pdf");
        var review = _app.Get<ReviewPageViewModel>();
        await review.ShowBySeriesCommand.ExecuteAsync(null);

        Assert.Equal(["Saga", "Wynd", null], review.Series.GroupItems.Select(g => g.Group?.Name));

        review.FilterText = "throne";
        await Task.Delay(50, Ct);
        Assert.Equal(["Wynd", null], review.Series.GroupItems.Select(g => g.Group?.Name));
        Assert.Equal("Wynd", review.Series.Selected?.Group?.Name);

        review.SelectedSort = review.SortOptions.Single(o => o.Sort == ReviewSort.TitleDescending);
        review.FilterText = null;
        await Task.Delay(50, Ct);
        Assert.Equal(["Wynd", "Saga", null], review.Series.GroupItems.Select(g => g.Group?.Name));
    }
}
