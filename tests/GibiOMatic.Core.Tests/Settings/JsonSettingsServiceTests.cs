using System.Text.Json;
using GibiOMatic.Core;
using GibiOMatic.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace GibiOMatic.Core.Tests.Settings;

public sealed class JsonSettingsServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppPaths _paths;
    private readonly JsonSettingsService _service;

    public JsonSettingsServiceTests()
    {
        _paths = new AppPaths(_temp.Combine("config"), _temp.Combine("data"));
        _service = new JsonSettingsService(_paths, NullLogger<JsonSettingsService>.Instance);
    }

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Load_without_file_returns_defaults()
    {
        var settings = await _service.LoadAsync(Ct);

        Assert.Equal("pt-BR", settings.Language);
        Assert.Equal(AppTheme.Dark, settings.Theme);
        Assert.Null(settings.LibraryPath);
        Assert.False(File.Exists(_paths.SettingsFile));
    }

    [Fact]
    public async Task Save_then_load_round_trips()
    {
        var saved = new AppSettings { Language = "en", LibraryPath = @"D:\gibiomatic-test\Library", InboxPath = "/tmp/inbox" };

        await _service.SaveAsync(saved, Ct);
        var reloaded = await new JsonSettingsService(_paths, NullLogger<JsonSettingsService>.Instance).LoadAsync(Ct);

        Assert.Equal(saved.Language, reloaded.Language);
        Assert.Equal(saved.LibraryPath, reloaded.LibraryPath);
        Assert.Equal(saved.InboxPath, reloaded.InboxPath);
        Assert.Equal(saved.Theme, reloaded.Theme);
    }

    [Fact]
    public async Task Save_writes_readable_camel_case_json_and_leaves_no_temp_files()
    {
        await _service.SaveAsync(new AppSettings { LibraryPath = @"G:\My Drive\Comics" }, Ct);

        var json = await File.ReadAllTextAsync(_paths.SettingsFile, Ct);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal("dark", doc.RootElement.GetProperty("theme").GetString());
        Assert.Contains(@"G:\\My Drive\\Comics", json);
        Assert.Equal([_paths.SettingsFile], Directory.GetFiles(_paths.ConfigDirectory));
    }

    [Fact]
    public async Task Unknown_properties_from_newer_versions_survive_a_save()
    {
        Directory.CreateDirectory(_paths.ConfigDirectory);
        await File.WriteAllTextAsync(_paths.SettingsFile, """{ "language": "en", "futureOption": { "a": 1 } }""", Ct);

        var loaded = await _service.LoadAsync(Ct);
        await _service.SaveAsync(loaded with { InboxPath = "x" }, Ct);

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(_paths.SettingsFile, Ct));
        Assert.Equal(1, doc.RootElement.GetProperty("futureOption").GetProperty("a").GetInt32());
        Assert.Equal("x", doc.RootElement.GetProperty("inboxPath").GetString());
    }

    [Fact]
    public async Task Invalid_file_falls_back_to_defaults_and_keeps_a_backup()
    {
        Directory.CreateDirectory(_paths.ConfigDirectory);
        await File.WriteAllTextAsync(_paths.SettingsFile, "{ not json", Ct);

        var settings = await _service.LoadAsync(Ct);

        Assert.Equal(new AppSettings().Language, settings.Language);
        Assert.Equal("{ not json", await File.ReadAllTextAsync(_paths.SettingsFile + ".bad", Ct));
    }

    [Fact]
    public async Task Unsupported_language_is_replaced_by_the_default()
    {
        Directory.CreateDirectory(_paths.ConfigDirectory);
        await File.WriteAllTextAsync(_paths.SettingsFile, """{ "language": "klingon" }""", Ct);

        var settings = await _service.LoadAsync(Ct);

        Assert.Equal("pt-BR", settings.Language);
    }
}
