using GibiOMatic.Core.Text;

namespace GibiOMatic.Core.Tests.Text;

public sealed class IsbnTests
{
    [Theory]
    [InlineData("9781607066019", true)]
    [InlineData("978-1-60706-601-9", true)]
    [InlineData("1607066017", true)]
    [InlineData("080442957X", true)]
    [InlineData("080442957x", true)]
    [InlineData("9781607066018", false)]
    [InlineData("hum-irredeemable_vol3", false)]
    [InlineData("MAY160548", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Validates_check_digits(string? isbn, bool valid) => Assert.Equal(valid, Isbn.IsValid(isbn));

    [Theory]
    [InlineData("urn:isbn:978-1-60706-601-9", "9781607066019")]
    [InlineData("ISBN 9781534321236 (paperback)", "9781534321236")]
    [InlineData("uuid:1234-5678", null)]
    [InlineData("hum-saga_vol1", null)]
    public void Finds_an_isbn_inside_text(string text, string? expected) => Assert.Equal(expected, Isbn.Find(text));

    [Theory]
    [InlineData("1607066017", "9781607066019")]
    [InlineData("0-8044-2957-X", "9780804429573")]
    [InlineData("978-1-60706-601-9", "9781607066019")]
    public void Converts_isbn10_to_isbn13(string isbn, string expected) => Assert.Equal(expected, Isbn.ToIsbn13(isbn));
}

public sealed class TitleParserTests
{
    [Theory]
    [InlineData("Deadly Class, Vol. 11: A Fond Farewell", "Deadly Class", "11")]
    [InlineData("Saga, Vol. 01", "Saga", "1")]
    [InlineData("Lumberjanes: Issue 18", "Lumberjanes", "18")]
    [InlineData("House of Slaughter #1", "House of Slaughter", "1")]
    [InlineData("houseofslaughter vol1", "houseofslaughter", "1")]
    public void Guesses_series_from_the_title(string title, string name, string number) =>
        Assert.Equal((name, number), TitleParser.GuessSeries(title));

    [Theory]
    [InlineData("Plutona")]
    [InlineData("Revolution 3")]
    public void No_series_without_a_number_marker(string title) => Assert.Null(TitleParser.GuessSeries(title));

    [Theory]
    [InlineData("Saga_Vol1_Image.pdf", "Saga Vol1 Image")]
    [InlineData("saga-vol-01.epub", "saga-vol-01")]
    [InlineData("Deadly.Class.v12.cbz", "Deadly Class v12")]
    [InlineData("The Department of Truth v1.pdf", "The Department of Truth v1")]
    [InlineData("  spaced   name  .cbz", "spaced name")]
    public void Builds_a_title_from_the_file_name(string file, string title) =>
        Assert.Equal(title, TitleParser.TitleFromFileName(file));

    [Theory]
    [InlineData("Saga, Vol. 01", "saga_vol1")]
    [InlineData("Saga, Vol. 1", "SAGA VOLUME 1")]
    [InlineData("Lumberjanes #18", "Lumberjanes: Issue 18")]
    [InlineData("Pokémon Adventures", "Pokemon Adventures")]
    public void Book_keys_match_across_spellings(string a, string b) =>
        Assert.Equal(TitleParser.BookKey(a), TitleParser.BookKey(b));

    [Fact]
    public void Book_keys_keep_different_volumes_apart() =>
        Assert.NotEqual(TitleParser.BookKey("Saga, Vol. 1"), TitleParser.BookKey("Saga, Vol. 11"));
}
