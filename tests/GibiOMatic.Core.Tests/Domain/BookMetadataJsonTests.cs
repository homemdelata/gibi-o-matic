using System.Text.Json;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Json;

namespace GibiOMatic.Core.Tests.Domain;

public sealed class BookMetadataJsonTests
{
    private static BookMetadata Sample() => new()
    {
        Id = "01JABCK3F9A2QWERTYZXCVBNMK",
        Kind = BookKind.Tpb,
        Title = "Saga, Vol. 1",
        Series = new SeriesInfo("Saga", "1"),
        Date = new PartialDate(2012, 10, 10),
        Publisher = "Image Comics",
        Credits = [new Credit(CreditRole.Writer, "Brian K. Vaughan"), new Credit(CreditRole.Artist, "Fiona Staples")],
        Language = "en",
        Identifiers = new BookIdentifiers { Isbn = "9781607066019", ComicVine = "4000-123456" },
        Files =
        [
            new BookFile
            {
                Name = "Saga, Vol. 1.pdf",
                DownloadedAs = "Saga_Vol1_Image.pdf",
                Format = BookFormat.Pdf,
                Sha256 = "abc",
                Bytes = 412345678,
                Source = "Humble Bundle",
                Quality = FileQuality.Original,
            },
        ],
        Review = new ReviewInfo { Status = ReviewStatus.Approved, MatchedBy = MatchedBy.ComicVineVolume, Confidence = 0.99 },
        CreatedAt = new DateTimeOffset(2026, 10, 17, 14, 0, 0, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 10, 17, 14, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void Serializes_with_the_names_and_values_of_the_spec()
    {
        var json = JsonSerializer.Serialize(Sample(), GibiJson.Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("schema").GetInt32());
        Assert.Equal("tpb", root.GetProperty("kind").GetString());
        Assert.Equal("Saga", root.GetProperty("series").GetProperty("name").GetString());
        Assert.Equal("writer", root.GetProperty("credits")[0].GetProperty("role").GetString());
        Assert.Equal("4000-123456", root.GetProperty("identifiers").GetProperty("comicvine").GetString());
        Assert.Equal("pdf", root.GetProperty("files")[0].GetProperty("format").GetString());
        Assert.Equal("original", root.GetProperty("files")[0].GetProperty("role").GetString());
        Assert.Equal("Saga_Vol1_Image.pdf", root.GetProperty("files")[0].GetProperty("downloadedAs").GetString());
        Assert.Equal("approved", root.GetProperty("review").GetProperty("status").GetString());
        Assert.Equal("comicvine-volume", root.GetProperty("review").GetProperty("matchedBy").GetString());
    }

    [Fact]
    public void Round_trips()
    {
        var original = Sample();

        var copy = JsonSerializer.Deserialize<BookMetadata>(JsonSerializer.Serialize(original, GibiJson.Options), GibiJson.Options)!;

        Assert.Equal(original.Title, copy.Title);
        Assert.Equal(original.Series, copy.Series);
        Assert.Equal(original.Date, copy.Date);
        Assert.Equal(original.Credits, copy.Credits);
        Assert.Equal(original.Identifiers, copy.Identifiers);
        Assert.Equal(original.Files, copy.Files);
        Assert.Equal(original.Review, copy.Review);
        Assert.Equal(original.CreatedAt, copy.CreatedAt);
    }

    [Fact]
    public void Missing_required_fields_are_rejected()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<BookMetadata>("""{ "title": "No id" }""", GibiJson.Options));
    }
}
