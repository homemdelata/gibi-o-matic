using GibiOMatic.App.ViewModels;

namespace GibiOMatic.App.Tests;

/// <summary>The covers of a series or universe card: the first volume in front, up to two behind (F7).</summary>
public sealed class CoverStackTests
{
    private sealed record Volume(string Title, string? Number);

    private static (string Front, string[] Behind) Pick(params Volume[] books)
    {
        var (front, behind) = CoverStack.Pick(books, v => v.Number);
        return (front.Title, behind.Select(v => v.Title).ToArray());
    }

    [Fact]
    public void One_book_has_nothing_behind()
    {
        Assert.Equal(("Saga 1", []), Pick(new Volume("Saga 1", "1")));
    }

    [Fact]
    public void Two_books_have_one_cover_behind()
    {
        var (front, behind) = Pick(new Volume("Saga 1", "1"), new Volume("Saga 2", "2"));

        Assert.Equal("Saga 1", front);
        Assert.Equal(["Saga 2"], behind);
    }

    [Fact]
    public void Three_or_more_books_show_the_next_two_in_reading_order()
    {
        var (front, behind) = Pick(
            new Volume("Saga 1", "1"), new Volume("Saga 2", "2"), new Volume("Saga 3", "3"), new Volume("Saga 4", "4"));

        Assert.Equal("Saga 1", front);
        Assert.Equal(["Saga 2", "Saga 3"], behind);
    }

    [Fact]
    public void Volumes_without_a_number_go_after_the_numbered_ones()
    {
        // The library sorts an empty number first; the stack keeps volume 1 in front.
        var (front, behind) = Pick(new Volume("Saga: Especial", null), new Volume("Saga 1", "1"), new Volume("Saga 2", "2"));

        Assert.Equal("Saga 1", front);
        Assert.Equal(["Saga 2", "Saga: Especial"], behind);
    }

    [Fact]
    public void Without_any_number_the_order_is_kept()
    {
        var (front, behind) = Pick(new Volume("A", null), new Volume("B", " "), new Volume("C", null));

        Assert.Equal("A", front);
        Assert.Equal(["B", "C"], behind);
    }
}
