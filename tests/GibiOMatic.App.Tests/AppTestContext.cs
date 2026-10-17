using GibiOMatic.App.Services;
using GibiOMatic.Core;
using Microsoft.Extensions.Configuration;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Settings;
using GibiOMatic.Metadata;
using GibiOMatic.Metadata.Identification;
using Microsoft.Extensions.DependencyInjection;

namespace GibiOMatic.App.Tests;

/// <summary>The real composition root over a temp folder, with a scripted folder picker.</summary>
public sealed class AppTestContext : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ServiceProvider _services;

    public AppTestContext()
    {
        Paths = new AppPaths(_temp.Combine("config"), _temp.Combine("data"));
        _services = CreateServices();
    }

    public AppPaths Paths { get; }

    public FakeFolderPicker Picker { get; } = new();

    public FakeFilePicker FilePicker { get; } = new();

    public FakeUrlOpener Urls { get; } = new();

    /// <summary>Never calls the real APIs; answers what each test scripts.</summary>
    public FakeIdentifier Identifier { get; } = new();

    public string Folder(string name) => _temp.Combine(name);

    public T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    /// <summary>A second "run" of the app on the same machine (same settings and index folders).</summary>
    public ServiceProvider CreateServices() =>
        new ServiceCollection()
            .AddGibiOMatic(Paths, new ConfigurationBuilder().Build())
            .AddSingleton<IFolderPicker>(Picker)
            .AddSingleton<IFilePicker>(FilePicker)
            .AddSingleton<IUiThread>(new InlineUiThread())
            .AddSingleton<IBookIdentifier>(Identifier)
            .AddSingleton<IUrlOpener>(Urls)
            .BuildServiceProvider();

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }
}

public sealed class FakeFolderPicker : IFolderPicker
{
    private readonly Queue<string?> _answers = new();

    public void WillReturn(string? folder) => _answers.Enqueue(folder);

    public Task<string?> PickFolderAsync(string title, CancellationToken ct = default) =>
        Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : null);
}

public sealed class FakeFilePicker : IFilePicker
{
    private readonly Queue<IReadOnlyList<string>> _answers = new();

    public void WillReturn(params string[] files) => _answers.Enqueue(files);

    public Task<IReadOnlyList<string>> PickBooksAsync(string title, CancellationToken ct = default) =>
        Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : (IReadOnlyList<string>)[]);

    public string? NextImage { get; set; }

    public Task<string?> PickImageAsync(string title, CancellationToken ct = default) => Task.FromResult(NextImage);
}

/// <summary>No UI thread in tests: updates run right away.</summary>
public sealed class InlineUiThread : IUiThread
{
    public void Post(Action action) => action();
}

public sealed class FakeIdentifier : IBookIdentifier
{
    public Func<BookMetadata, string?, IReadOnlyList<MetadataCandidate>> Respond { get; set; } = (_, _) => [];

    /// <summary>When set, searches wait for it (to see what the screen shows meanwhile).</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async Task<IdentificationResult> IdentifyAsync(BookMetadata book, MetadataSettings settings, string? searchText, CancellationToken ct)
    {
        if (Gate is { } gate)
        {
            await gate.Task;
        }

        return new IdentificationResult(BookQuery.From(book, searchText), Respond(book, searchText), []);
    }

    public Task<MetadataCandidate> CompleteAsync(MetadataCandidate candidate, CancellationToken ct) =>
        Task.FromResult(candidate with { Record = candidate.Record with { IsDetailed = true } });

    public Task<IReadOnlyList<MetadataCandidate>> FromComicVineLinkAsync(BookMetadata book, string link, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MetadataCandidate>>(link.Contains("4000-")
            ? [Candidate("House of Slaughter, Vol. 1", "House of Slaughter", "1", 1.0, MatchReason.ChosenByUser)]
            : []);

    public static MetadataCandidate Candidate(string title, string series, string number, double confidence, params MatchReason[] reasons) => new(
        new MetadataRecord
        {
            Source = SourceIds.ComicVine,
            Title = title,
            SeriesName = series,
            Number = number,
            Kind = BookKind.Tpb,
            Date = new PartialDate(2012, 10, 10),
            Publisher = "Image Comics",
            Credits = [new Credit(CreditRole.Writer, "Brian K. Vaughan"), new Credit(CreditRole.Artist, "Fiona Staples")],
            ComicVineId = "4000-1",
        },
        confidence,
        reasons);
}

public sealed class FakeUrlOpener : IUrlOpener
{
    public List<Uri> Opened { get; } = [];

    public Task OpenAsync(Uri url)
    {
        Opened.Add(url);
        return Task.CompletedTask;
    }
}
