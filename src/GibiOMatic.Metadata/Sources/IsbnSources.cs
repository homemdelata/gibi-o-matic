using System.Text.Json;
using GibiOMatic.Core.Domain;
using GibiOMatic.Metadata.Http;

namespace GibiOMatic.Metadata.Sources;

/// <summary>A source that finds a book by ISBN (Open Library, Google Books).</summary>
public interface IIsbnSource
{
    string Id { get; }

    Task<MetadataRecord?> LookupIsbnAsync(string isbn, CancellationToken ct);
}

/// <summary>
/// Open Library books API (no key): <c>/api/books?bibkeys=ISBN:…&amp;format=json&amp;jscmd=data</c>.
/// Its authors have no role, so they are not used as credits (ComicVine has them by role).
/// </summary>
public sealed class OpenLibraryClient(SourceClient client) : IIsbnSource
{
    public string Id => SourceIds.OpenLibrary;

    public async Task<MetadataRecord?> LookupIsbnAsync(string isbn, CancellationToken ct)
    {
        var url = $"https://openlibrary.org/api/books?bibkeys=ISBN:{Uri.EscapeDataString(isbn)}&format=json&jscmd=data";
        using var doc = JsonDocument.Parse(await client.GetAsync("books", url, secretQuery: null, ct));
        if (doc.RootElement.Prop($"ISBN:{isbn}") is not { } book)
        {
            return null;
        }

        var title = book.Str("title");
        var subtitle = book.Str("subtitle");
        return new MetadataRecord
        {
            Source = Id,
            Title = subtitle is null || title is null ? title : $"{title}: {subtitle}",
            Date = Json.Date(book.Str("publish_date")),
            Publisher = book.Items("publishers").Select(p => p.Str("name")).FirstOrDefault(p => p is not null),
            Isbn = isbn,
            PageCount = book.Int("number_of_pages"),
            Summary = book.Str("notes"),
            SourceUrl = book.Str("url"),
            Credits = book.Items("authors").Select(a => a.Str("name")).OfType<string>().Select(n => new Credit(CreditRole.Other, n)).ToList(),
        };
    }
}

/// <summary>Google Books volumes API by ISBN. Off by default: without a key it answers 429 quickly.</summary>
public sealed class GoogleBooksClient(SourceClient client) : IIsbnSource
{
    public string Id => SourceIds.GoogleBooks;

    public async Task<MetadataRecord?> LookupIsbnAsync(string isbn, CancellationToken ct)
    {
        var url = $"https://www.googleapis.com/books/v1/volumes?q=isbn:{Uri.EscapeDataString(isbn)}";
        using var doc = JsonDocument.Parse(await client.GetAsync("volumes", url, secretQuery: null, ct));
        if (doc.RootElement.Items("items").FirstOrDefault() is not { ValueKind: JsonValueKind.Object } item || item.Prop("volumeInfo") is not { } info)
        {
            return null;
        }

        var title = info.Str("title");
        var subtitle = info.Str("subtitle");
        return new MetadataRecord
        {
            Source = Id,
            Title = subtitle is null || title is null ? title : $"{title}: {subtitle}",
            Date = Json.Date(info.Str("publishedDate")),
            Publisher = info.Str("publisher"),
            Isbn = isbn,
            PageCount = info.Int("pageCount"),
            Summary = info.Str("description"),
            Language = info.Str("language"),
            SourceUrl = info.Str("infoLink"),
            Credits = info.Items("authors").Select(a => a.ValueKind == JsonValueKind.String ? a.GetString() : null)
                .OfType<string>().Select(n => new Credit(CreditRole.Other, n)).ToList(),
        };
    }
}
