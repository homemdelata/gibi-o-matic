using System.Text.Json;
using GibiOMatic.Core;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Ids;
using GibiOMatic.Core.Libraries;
using GibiOMatic.Library.Index;
using Microsoft.Extensions.Logging.Abstractions;

namespace GibiOMatic.Library.Tests;

public sealed class LibraryServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppPaths _paths;
    private readonly LibraryService _service;
    private readonly string _root;

    public LibraryServiceTests()
    {
        _paths = new AppPaths(_temp.Combine("config"), _temp.Combine("data"));
        _service = new LibraryService(_paths, NullLogger<LibraryService>.Instance);
        _root = _temp.Combine("My Library");
    }

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string IndexPath(LibraryInfo library) => Path.Combine(_paths.IndexDirectory, library.Id + ".db");

    [Fact]
    public async Task Create_lays_out_the_library_and_its_index()
    {
        var library = await _service.CreateAsync(_root, Ct);

        Assert.True(Ulid.IsValid(library.Id));
        Assert.Equal(1, library.FormatVersion);
        Assert.True(Directory.Exists(Path.Combine(_root, "Books")));
        Assert.True(Directory.Exists(Path.Combine(_root, ".gibiomatic", "history")));

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "gibiomatic-library.json"), Ct));
        Assert.Equal(library.Id, manifest.RootElement.GetProperty("id").GetString());
        Assert.Equal(1, manifest.RootElement.GetProperty("formatVersion").GetInt32());

        Assert.True(File.Exists(IndexPath(library)));
        Assert.False(File.Exists(Path.Combine(_root, library.Id + ".db")), "the index must never live inside the library");
        Assert.Equal(0, await _service.CountBooksAsync(library, Ct));
    }

    [Fact]
    public async Task Create_refuses_a_folder_that_already_holds_a_library()
    {
        await _service.CreateAsync(_root, Ct);

        var ex = await Assert.ThrowsAsync<LibraryException>(() => _service.CreateAsync(_root, Ct));
        Assert.Equal(LibraryErrorKind.AlreadyALibrary, ex.Kind);
    }

    [Fact]
    public async Task Open_returns_the_same_library_that_was_created()
    {
        var created = await _service.CreateAsync(_root, Ct);

        var opened = await _service.OpenAsync(_root + Path.DirectorySeparatorChar, Ct);

        Assert.Equal(created, opened);
    }

    [Fact]
    public async Task Open_rejects_a_folder_without_a_manifest()
    {
        Directory.CreateDirectory(_root);

        var ex = await Assert.ThrowsAsync<LibraryException>(() => _service.OpenAsync(_root, Ct));
        Assert.Equal(LibraryErrorKind.NotALibrary, ex.Kind);
    }

    [Theory]
    [InlineData("{ broken", LibraryErrorKind.InvalidManifest)]
    [InlineData("""{ "formatVersion": 1, "id": "not-a-ulid" }""", LibraryErrorKind.InvalidManifest)]
    [InlineData("""{ "formatVersion": 99, "id": "01JABCDEFGHJKMNPQRSTK3F9A2" }""", LibraryErrorKind.UnsupportedFormatVersion)]
    public async Task Open_rejects_bad_manifests(string manifest, LibraryErrorKind expected)
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "gibiomatic-library.json"), manifest, Ct);

        var ex = await Assert.ThrowsAsync<LibraryException>(() => _service.OpenAsync(_root, Ct));
        Assert.Equal(expected, ex.Kind);
    }

    [Fact]
    public async Task Rebuild_indexes_every_metadata_json_and_reports_broken_ones()
    {
        var library = await _service.CreateAsync(_root, Ct);
        var saga = await WriteBookAsync("Image/Saga/Saga, Vol. 1 [aaaaaa]", "Saga, Vol. 1", "Saga");
        var plutona = await WriteBookAsync("Image/Plutona [bbbbbb]", "Plutona", series: null);
        await WriteRawAsync("_No publisher/Broken [cccccc]", "{ this is not json");
        await WriteRawAsync("_No publisher/Duplicate [dddddd]", MetadataFile.Serialize(saga with { Title = "Copy" }));

        var result = await _service.RebuildIndexAsync(library, Ct);

        Assert.Equal(2, result.IndexedBooks);
        Assert.Equal(2, result.Issues.Count);
        Assert.Contains(result.Issues, i => i.RelativePath == "Books/_No publisher/Broken [cccccc]/metadata.json");
        Assert.Contains(result.Issues, i => i.Message.Contains("Duplicate", StringComparison.Ordinal));

        var index = new LibraryIndex(IndexPath(library));
        Assert.Equal(new[] { saga.Id, plutona.Id }.Order(StringComparer.Ordinal), await index.GetBookIdsAsync(Ct));
    }

    [Fact]
    public async Task Rebuild_replaces_previous_content()
    {
        var library = await _service.CreateAsync(_root, Ct);
        await WriteBookAsync("Image/Saga/Saga, Vol. 1 [aaaaaa]", "Saga, Vol. 1", "Saga");
        Assert.Equal(1, (await _service.RebuildIndexAsync(library, Ct)).IndexedBooks);

        Directory.Delete(Path.Combine(_root, "Books", "Image"), recursive: true);
        await _service.RebuildIndexAsync(library, Ct);

        Assert.Equal(0, await _service.CountBooksAsync(library, Ct));
    }

    [Fact]
    public async Task Open_rebuilds_a_missing_index_from_disk()
    {
        var library = await _service.CreateAsync(_root, Ct);
        await WriteBookAsync("Image/Saga/Saga, Vol. 1 [aaaaaa]", "Saga, Vol. 1", "Saga");
        File.Delete(IndexPath(library));

        var reopened = await _service.OpenAsync(_root, Ct);

        Assert.Equal(1, await _service.CountBooksAsync(reopened, Ct));
    }

    [Fact]
    public async Task Index_written_by_a_newer_app_version_is_discarded_and_rebuilt()
    {
        var library = await _service.CreateAsync(_root, Ct);
        await WriteBookAsync("Image/Saga/Saga, Vol. 1 [aaaaaa]", "Saga, Vol. 1", "Saga");
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={IndexPath(library)};Pooling=False"))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 999;";
            await command.ExecuteNonQueryAsync(Ct);
        }

        await _service.OpenAsync(_root, Ct);

        var index = new LibraryIndex(IndexPath(library));
        Assert.Equal(IndexMigrations.LatestVersion, await index.GetVersionAsync(Ct));
        Assert.Equal(1, await index.CountBooksAsync(Ct));
    }

    private async Task<BookMetadata> WriteBookAsync(string folder, string title, string? series)
    {
        var metadata = new BookMetadata
        {
            Id = Ulid.NewUlid(),
            Kind = series is null ? BookKind.Book : BookKind.Tpb,
            Title = title,
            Series = series is null ? null : new SeriesInfo(series, "1"),
            Publisher = "Image Comics",
            Date = new PartialDate(2012),
        };
        await WriteRawAsync(folder, MetadataFile.Serialize(metadata));
        return metadata;
    }

    private async Task WriteRawAsync(string folder, string content)
    {
        var dir = Path.Combine(_root, "Books", folder);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "metadata.json"), content, Ct);
    }
}

public sealed class FolderContentTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Only_visible_entries_count_as_content()
    {
        var folder = _temp.Combine("folder");
        Directory.CreateDirectory(folder);
        Assert.False(LibraryService.HasVisibleContent(folder));

        File.WriteAllText(Path.Combine(folder, ".DS_Store"), "");
        if (OperatingSystem.IsWindows())
        {
            var ini = Path.Combine(folder, "desktop.ini");
            File.WriteAllText(ini, "");
            File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
        }

        Assert.False(LibraryService.HasVisibleContent(folder));

        File.WriteAllText(Path.Combine(folder, "foto.jpg"), "");
        Assert.True(LibraryService.HasVisibleContent(folder));
        Assert.False(LibraryService.HasVisibleContent(_temp.Combine("nao-existe")));
    }
}
