using GibiOMatic.Core.Domain;

namespace GibiOMatic.Core.Tests.Domain;

public sealed class PartialDateTests
{
    [Theory]
    [InlineData("2022", 2022, null, null)]
    [InlineData(" 2022-06 ", 2022, 6, null)]
    [InlineData("2024-02-29", 2024, 2, 29)]
    public void Year_month_and_day_are_read(string text, int year, int? month, int? day) =>
        Assert.Equal(new PartialDate(year, month, day), PartialDate.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("June 2022")]
    [InlineData("2022-13")]
    [InlineData("2023-02-29")]
    [InlineData("1700")]
    [InlineData("2022--01")]
    public void Anything_else_is_not_a_date(string? text) => Assert.Null(PartialDate.Parse(text));
}
