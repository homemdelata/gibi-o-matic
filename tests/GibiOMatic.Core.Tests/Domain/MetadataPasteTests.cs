using System.Text.Json;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Json;
using GibiOMatic.Core.Settings;

namespace GibiOMatic.Core.Tests.Domain;

public sealed class MetadataPasteTests
{
    private static readonly BookMetadata Source = new()
    {
        Id = "SOURCE",
        Kind = BookKind.Tpb,
        Title = "Saga, Vol. 1",
        Series = new SeriesInfo("Saga", "1", "2012"),
        Date = new PartialDate(2012, 10),
        Publisher = "Image Comics",
        Credits = [new Credit(CreditRole.Writer, "Brian K. Vaughan"), new Credit(CreditRole.Artist, "Fiona Staples")],
        Summary = "The first volume.",
        Language = "en",
        Identifiers = new BookIdentifiers { Isbn = "9781607066019", ComicVine = "4000-1" },
        Tags = ["sci-fi"],
        Universes = ["Saga-Verse"],
        OneShot = false,
        Review = new ReviewInfo { Status = ReviewStatus.Approved, MatchedBy = MatchedBy.Isbn },
    };

    private static BookMetadata Target(string title = "saga vol2", SeriesInfo? series = null, string downloadedAs = "Saga_Vol2_Image.pdf") => new()
    {
        Id = "TARGET",
        Kind = BookKind.Book,
        Title = title,
        Series = series,
        Imprint = "Its own imprint",
        Credits = [new Credit(CreditRole.Other, "Someone")],
        Identifiers = new BookIdentifiers { Isbn = "9781607066927" },
        Files = [new BookFile { Name = "saga vol2.pdf", DownloadedAs = downloadedAs, Sha256 = "x" }],
    };

    [Fact]
    public void The_default_fields_copy_what_a_series_shares_and_keep_the_book_its_own()
    {
        var pasted = MetadataPaste.Apply(Source, Target(), MetadataPaste.DefaultFields);

        Assert.Equal("saga vol2", pasted.Title);
        Assert.Null(pasted.Date);
        Assert.Null(pasted.Summary);
        Assert.Equal(new SeriesInfo("Saga", "2", "2012"), pasted.Series);
        Assert.Equal((BookKind.Tpb, "Image Comics", "en", false), (pasted.Kind, pasted.Publisher, pasted.Language, pasted.OneShot));
        Assert.Equal(Source.Credits, pasted.Credits);
        Assert.Equal(["Saga-Verse"], pasted.Universes);

        // Never copied: they belong to the book.
        Assert.Equal(("TARGET", "9781607066927", null), (pasted.Id, pasted.Identifiers.Isbn, pasted.Identifiers.ComicVine));
        Assert.Empty(pasted.Tags);
        Assert.Equal(ReviewStatus.Pending, pasted.Review.Status);
        Assert.Single(pasted.Files);

        // The source has no imprint: the target's stays.
        Assert.Equal("Its own imprint", pasted.Imprint);
    }

    [Fact]
    public void Title_date_and_summary_are_copied_only_when_chosen()
    {
        var pasted = MetadataPaste.Apply(Source, Target(), [PasteField.Title, PasteField.Date, PasteField.Summary]);

        Assert.Equal(("Saga, Vol. 1", new PartialDate(2012, 10), "The first volume."), (pasted.Title, pasted.Date, pasted.Summary));
        Assert.Null(pasted.Series);
        Assert.Equal(BookKind.Book, pasted.Kind);
        Assert.Equal("Someone", Assert.Single(pasted.Credits).Name);
    }

    [Theory]
    [InlineData("saga vol2", "Saga_Vol2_Image.pdf", "2")]
    [InlineData("Saga", "Saga_Vol3_Image.pdf", "3")]
    [InlineData("Saga", "Saga_Image.pdf", null)]
    public void The_number_comes_from_the_title_then_the_file_name(string title, string downloadedAs, string? number)
    {
        var pasted = MetadataPaste.Apply(Source, Target(title, downloadedAs: downloadedAs), [PasteField.Series]);

        Assert.Equal(new SeriesInfo("Saga", number, "2012"), pasted.Series);
    }

    [Fact]
    public void A_number_the_book_already_has_wins()
    {
        var pasted = MetadataPaste.Apply(Source, Target("saga vol2", new SeriesInfo("Old name", "2.5")), [PasteField.Series]);

        Assert.Equal(new SeriesInfo("Saga", "2.5", "2012"), pasted.Series);
    }

    [Fact]
    public void Empty_values_in_the_source_clear_nothing()
    {
        var empty = new BookMetadata { Id = "EMPTY", Title = "Untitled", Kind = BookKind.Tpb };
        var target = Target("saga vol2", new SeriesInfo("Saga", "2")) with { Language = "pt", Universes = ["Mine"], OneShot = true };

        var pasted = MetadataPaste.Apply(empty, target, Enum.GetValues<PasteField>().Where(f => f != PasteField.Title).ToList());

        Assert.Equal(target with { Kind = BookKind.Tpb }, pasted);
    }

    [Fact]
    public void The_chosen_fields_are_remembered_in_the_settings_by_name()
    {
        var settings = new AppSettings { Editor = new EditorSettings { PasteFields = [PasteField.Series, PasteField.OneShot] } };

        var json = JsonSerializer.Serialize(settings, GibiJson.Options);
        Assert.Contains("\"pasteFields\":[\"series\",\"oneShot\"]", json.Replace(" ", string.Empty).Replace("\n", string.Empty).Replace("\r", string.Empty));
        Assert.Equal([PasteField.Series, PasteField.OneShot], JsonSerializer.Deserialize<AppSettings>(json, GibiJson.Options)!.Editor.PasteFields);
        Assert.Equal(MetadataPaste.DefaultFields, JsonSerializer.Deserialize<AppSettings>("{}", GibiJson.Options)!.Editor.PasteFields);
    }
}
