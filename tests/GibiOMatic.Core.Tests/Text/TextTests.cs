using GibiOMatic.Core.Domain;
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
    [InlineData("Hellboy - Krampusnacht")]
    public void No_series_without_a_number_marker(string title) => Assert.Null(TitleParser.GuessSeries(title));

    /// <summary>Title shapes found in Humble Bundle downloads: nothing that names the book is dropped.</summary>
    [Theory]
    // name | volume | issue | subtitle | kind | series year
    [InlineData("RoboCop Vol. 3 - Last Stand #5", "RoboCop: Last Stand", "3", "5", null, BookKind.Issue, null)]
    [InlineData("The Boys Vol. 11 - Over the Hill with the Swords of a Thousand Men", "The Boys", "11", null, "Over the Hill with the Swords of a Thousand Men", BookKind.Tpb, null)]
    [InlineData("Something Epic Vol. 1 (v2)", "Something Epic", "1", null, null, BookKind.Tpb, null)]
    [InlineData("Deadly Class, Vol. 11: A Fond Farewell", "Deadly Class", "11", null, "A Fond Farewell", BookKind.Tpb, null)]
    [InlineData("Wynd - Book One - Flight of the Prince", "Wynd", "1", null, "Flight of the Prince", BookKind.Tpb, null)]
    [InlineData("Pogo - The Complete Syndicated Comic Strips Vol. 2 - Bona Fide Balderdash", "Pogo: The Complete Syndicated Comic Strips", "2", null, "Bona Fide Balderdash", BookKind.Tpb, null)]
    [InlineData("Hellboy - Krampusnacht", "Hellboy", null, null, "Krampusnacht", null, null)]
    [InlineData("Hellboy and the B.P.R.D. - Her Fatal Hour One-Shot", "Hellboy and the B.P.R.D.", null, null, "Her Fatal Hour", null, null)]
    [InlineData("Lovesick Vol. 1 TP", "Lovesick", "1", null, null, BookKind.Tpb, null)]
    [InlineData("Plutona TP", "Plutona", null, null, null, BookKind.Tpb, null)]
    [InlineData("Fathom - Kiani - Omnibus 1", "Fathom: Kiani Omnibus", "1", null, null, BookKind.Omnibus, null)]
    [InlineData("Pathfinder #1 - Map", "Pathfinder", null, "1", "Map", BookKind.Issue, null)]
    [InlineData("The Complete Dracula #1 (Free Issue)", "The Complete Dracula", null, "1", null, BookKind.Issue, null)]
    [InlineData("Do Androids Dream of Electric Sheep Omnibus - Issues 1-24", "Do Androids Dream of Electric Sheep Omnibus", null, null, "Issues 1-24", BookKind.Omnibus, null)]
    [InlineData("RoboCop (2003) Vol. 1", "RoboCop", "1", null, null, BookKind.Tpb, 2003)]
    [InlineData("Lumberjanes_Issue_18.cbz", "Lumberjanes", null, "18", null, BookKind.Issue, null)]
    [InlineData("deadlyclass vol11", "deadlyclass", "11", null, null, BookKind.Tpb, null)]
    [InlineData("Saga, Vol. 01 [Digital]", "Saga", "1", null, null, BookKind.Tpb, null)]
    [InlineData("Prince Valiant Vol. 1 - 1937-1938", "Prince Valiant", "1", null, "1937-1938", BookKind.Tpb, null)]
    public void Parses_titles_into_their_parts(string title, string name, string? volume, string? issue, string? subtitle, BookKind? kind, int? year)
    {
        var parts = TitleParser.Parse(title);
        Assert.Equal((name, volume, issue, subtitle, kind, year),
            (parts.Name, parts.VolumeNumber, parts.IssueNumber, parts.Subtitle, parts.Kind, parts.SeriesYear));
    }

    [Theory]
    [InlineData("RoboCop Vol. 3 - Last Stand #5", "RoboCop: Last Stand", "5")]
    [InlineData("Transformers - More Than Meets the Eye Vol. 4", "Transformers: More Than Meets the Eye", "4")]
    [InlineData("March - Book Two", "March", "2")]
    public void The_series_includes_the_named_part_before_the_number(string title, string name, string number) =>
        Assert.Equal((name, number), TitleParser.GuessSeries(title));

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
