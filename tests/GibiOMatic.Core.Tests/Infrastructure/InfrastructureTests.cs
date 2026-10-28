using GibiOMatic.Core.Ids;
using GibiOMatic.Core.IO;
using GibiOMatic.Core.Secrets;

namespace GibiOMatic.Core.Tests.Infrastructure;

public sealed class UlidTests
{
    [Fact]
    public void New_ulids_are_valid_unique_and_sorted_by_time()
    {
        var earlier = Ulid.NewUlid(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var later = Ulid.NewUlid(new DateTimeOffset(2026, 1, 1, 0, 0, 1, TimeSpan.Zero));

        Assert.True(Ulid.IsValid(earlier));
        Assert.Equal(Ulid.Length, earlier.Length);
        Assert.True(string.CompareOrdinal(earlier, later) < 0);
        Assert.NotEqual(Ulid.NewUlid(), Ulid.NewUlid());
    }

    [Fact]
    public void Encodes_the_timestamp_like_the_reference_implementation()
    {
        // From the ULID spec: 1469918176385 ms -> "01ARYZ6S41".
        var ulid = Ulid.NewUlid(DateTimeOffset.FromUnixTimeMilliseconds(1469918176385));

        Assert.StartsWith("01ARYZ6S41", ulid);
    }

    [Fact]
    public void Short_id_is_the_last_six_characters_in_lowercase()
    {
        Assert.Equal("k3f9a2", Ulid.ShortId("01JABCDEFGHJKMNPQRSTK3F9A2"));
        Assert.Throws<ArgumentException>(() => Ulid.ShortId("nope"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("01JABCDEFGHJKMNPQRSTK3F9AU")] // 'U' is not in the alphabet
    [InlineData("81JABCDEFGHJKMNPQRSTK3F9A2")] // overflows 128 bits
    public void Rejects_invalid_values(string? value) => Assert.False(Ulid.IsValid(value));
}

public sealed class AtomicFileTests
{
    [Fact]
    public async Task Replaces_existing_content_and_leaves_no_temp_files()
    {
        using var temp = new TempDirectory();
        var file = temp.Combine("sub", "file.json");
        var ct = TestContext.Current.CancellationToken;

        await AtomicFile.WriteAllTextAsync(file, "first", ct);
        await AtomicFile.WriteAllTextAsync(file, "segundo é maior", ct);

        Assert.Equal("segundo é maior", await File.ReadAllTextAsync(file, ct));
        Assert.Single(Directory.GetFiles(temp.Combine("sub")));
    }
}

public sealed class SecretStoreTests
{
    [Fact]
    public async Task Dpapi_store_round_trips_without_writing_plain_text()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("DPAPI is only available on Windows.");
            return;
        }

        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Combine("config"), temp.Combine("data"));
        var store = new DpapiSecretStore(paths);
        var ct = TestContext.Current.CancellationToken;

        Assert.Null(await store.GetAsync(SecretNames.ComicVineApiKey, ct));
        await store.SetAsync(SecretNames.ComicVineApiKey, "not-a-real-key-123", ct);

        Assert.Equal("not-a-real-key-123", await store.GetAsync(SecretNames.ComicVineApiKey, ct));
        var stored = await File.ReadAllBytesAsync(Path.Combine(paths.SecretsDirectory, SecretNames.ComicVineApiKey + ".bin"), ct);
        Assert.DoesNotContain("not-a-real-key", System.Text.Encoding.UTF8.GetString(stored));

        await store.RemoveAsync(SecretNames.ComicVineApiKey, ct);
        Assert.Null(await store.GetAsync(SecretNames.ComicVineApiKey, ct));
    }

    [Fact]
    public async Task Unsupported_store_reports_it_and_refuses_to_store()
    {
        var store = new UnsupportedSecretStore();

        Assert.False(store.IsSupported);
        Assert.Null(await store.GetAsync("x", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => store.SetAsync("x", "y", TestContext.Current.CancellationToken));
    }
}

public sealed class FreedesktopTrashTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void A_file_goes_to_the_trash_with_its_info_and_names_do_not_collide()
    {
        var trash = new FreedesktopTrash(_temp.Combine("Trash"));
        Directory.CreateDirectory(_temp.Combine("Inbox"));
        var first = _temp.Combine("Inbox", "Saga 1.cbz");
        File.WriteAllText(first, "one");

        Assert.True(trash.TryRecycle(first));
        File.WriteAllText(first, "two");
        Assert.True(trash.TryRecycle(first));

        Assert.False(File.Exists(first));
        Assert.Equal("one", File.ReadAllText(_temp.Combine("Trash", "files", "Saga 1.cbz")));
        Assert.Equal("two", File.ReadAllText(_temp.Combine("Trash", "files", "Saga 1 (2).cbz")));
        var info = File.ReadAllText(_temp.Combine("Trash", "info", "Saga 1 (2).cbz.trashinfo"));
        Assert.StartsWith("[Trash Info]\nPath=", info);
        Assert.Contains("Saga%201.cbz\nDeletionDate=", info);
    }

    [Fact]
    public void A_missing_file_is_not_recycled()
    {
        Assert.False(new FreedesktopTrash(_temp.Combine("Trash")).TryRecycle(_temp.Combine("nada.cbz")));
    }
}
