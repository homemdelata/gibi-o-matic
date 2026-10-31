using GibiOMatic.App.Services;
using GibiOMatic.Core;
using Microsoft.Extensions.Configuration;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Settings;
using GibiOMatic.Metadata;
using GibiOMatic.Metadata.Identification;
using Microsoft.Extensions.DependencyInjection;

namespace GibiOMatic.App.Tests;

/// <summary>
/// The real composition root over a temp folder, with a scripted folder picker. Tests that use view
/// models start with <see cref="EnterUiThreadAsync"/>, like the app runs them on the UI thread.
/// </summary>
public sealed class AppTestContext : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ServiceProvider _services;

    public AppTestContext()
    {
        Paths = new AppPaths(_temp.Combine("config"), _temp.Combine("data"));
        RecycleBin = new FakeRecycleBin(_temp.Combine("Lixeira"));
        _services = CreateServices();
    }

    /// <summary>Never the real recycle bin: files go to a folder of the test.</summary>
    public FakeRecycleBin RecycleBin { get; }

    public AppPaths Paths { get; }

    public FakeFolderPicker Picker { get; } = new();

    /// <summary>This test's UI thread, shared by every <see cref="CreateServices"/>.</summary>
    public TestUiThread Ui { get; } = new();

    /// <summary>Moves the rest of the test to <see cref="Ui"/>.</summary>
    public TestUiThread.Awaitable EnterUiThreadAsync() => Ui.EnterAsync();

    public FakeFilePicker FilePicker { get; } = new();

    public FakeUrlOpener Urls { get; } = new();

    /// <summary>Records the books opened in the reader instead of opening windows.</summary>
    public FakeReaderLauncher Reader { get; } = new();

    /// <summary>Never calls the real APIs; answers what each test scripts.</summary>
    public FakeIdentifier Identifier { get; } = new();

    /// <summary>A Kindle in memory, connected or not.</summary>
    public FakeKindleDevice Kindle { get; } = new();

    /// <summary>Writes a small file instead of running the KCC.</summary>
    public FakeKccRunner Kcc { get; } = new();

    /// <summary>Records what the tray shows instead of touching the system tray.</summary>
    public FakeTrayIcon Tray { get; } = new();

    /// <summary>Records the start with the system instead of writing the registry or ~/.config.</summary>
    public FakeStartupRegistration Startup { get; } = new();

    /// <summary>An update source in memory.</summary>
    public FakeAppUpdater Updater { get; } = new();

    /// <summary>Puts a stand-in KCC where the app looks for the one it downloads.</summary>
    public void InstallFakeKcc()
    {
        var tool = Path.Combine(Paths.ToolsDirectory, "kcc", "kcc-c2e-12.0.0.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(tool)!);
        File.WriteAllText(tool, "fake");
    }

    public string Folder(string name) => _temp.Combine(name);

    public T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    /// <summary>A second "run" of the app on the same machine (same settings and index folders).</summary>
    public ServiceProvider CreateServices()
    {
        var services = new ServiceCollection()
            .AddGibiOMatic(Paths, new ConfigurationBuilder().Build())
            .AddSingleton<IFolderPicker>(Picker)
            .AddSingleton<IFilePicker>(FilePicker)
            .AddSingleton<IUiThread>(Ui)
            .AddSingleton<IBookIdentifier>(Identifier)
            .AddSingleton<IUrlOpener>(Urls)
            .AddSingleton<IReaderLauncher>(Reader)
            .AddSingleton<GibiOMatic.App.ViewModels.IReaderImages>(new FakeReaderImages())
            .AddSingleton<GibiOMatic.Destinations.Kindle.IKindleDevices>(Kindle)
            .AddSingleton<GibiOMatic.Conversion.Kcc.IKccRunner>(Kcc)
            .AddSingleton<GibiOMatic.App.Services.Tray.ITrayIcon>(Tray)
            .AddSingleton<IStartupRegistration>(Startup)
            .AddSingleton<IAppUpdater>(Updater)
            .AddSingleton<GibiOMatic.Core.IO.IRecycleBin>(RecycleBin)
            // The app waits for downloads to settle for seconds; tests do not.
            .AddSingleton(new GibiOMatic.Library.Import.InboxWatchOptions
            {
                SettleTime = TimeSpan.FromMilliseconds(100),
                PollInterval = TimeSpan.FromMilliseconds(40),
                QuietTime = TimeSpan.FromMilliseconds(100),
            })
            .BuildServiceProvider();
        // Automatic runs gather changes for 1.5 s in the app; tests do not wait that long.
        services.GetRequiredService<GibiOMatic.Destinations.Sync.DestinationServiceFactory>().GatherDelay = TimeSpan.FromMilliseconds(20);
        return services;
    }

    public void Dispose()
    {
        // The UI thread stops first: nothing it runs afterwards would find the services alive.
        Ui.Dispose();
        _services.Dispose();
        _temp.Dispose();
        Ui.ThrowIfFailed();
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

    public string? NextExecutable { get; set; }

    public Task<string?> PickExecutableAsync(string title, CancellationToken ct = default) => Task.FromResult(NextExecutable);
}

public sealed class FakeKccRunner : GibiOMatic.Conversion.Kcc.IKccRunner
{
    public int Runs { get; private set; }

    public async Task<string> ConvertAsync(GibiOMatic.Conversion.Kcc.KccJob job, IProgress<double>? progress, CancellationToken ct)
    {
        Runs++;
        Directory.CreateDirectory(job.OutputDirectory);
        var output = Path.Combine(job.OutputDirectory, "book.mobi");
        await File.WriteAllTextAsync(output, job.Title, ct);
        return output;
    }
}

public sealed class FakeKindleDevice : GibiOMatic.Destinations.Kindle.IKindleDevices, GibiOMatic.Destinations.Kindle.IKindleConnection
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Connected { get; set; }

    public bool IsSupported => true;

    public string Name => "Kindle Colorsoft";

    public IReadOnlyList<GibiOMatic.Destinations.Kindle.KindleDeviceInfo> Find() => Connected ? [new("usb-1", Name)] : [];

    public GibiOMatic.Destinations.Kindle.IKindleConnection Connect(string deviceId) =>
        Connected ? this : throw new GibiOMatic.Destinations.Kindle.KindleDeviceException("unplugged");

    public GibiOMatic.Destinations.Kindle.KindleSpace? Space() => new(9_800_000_000, 16_000_000_000);

    public IReadOnlyList<GibiOMatic.Destinations.Kindle.KindleFile> ListBooks() =>
        Files.Keys.Order().Select(f => new GibiOMatic.Destinations.Kindle.KindleFile(f, Files[f].Length, null)).ToList();

    public bool Exists(string path) => Files.ContainsKey(path);

    public void Upload(string localFile, string path, IProgress<double>? progress, CancellationToken ct) => Files[path] = File.ReadAllText(localFile);

    public void Delete(string path) => Files.Remove(path);

    public void Dispose()
    {
    }
}

public sealed class FakeTrayIcon : GibiOMatic.App.Services.Tray.ITrayIcon
{
    public bool IsSupported { get; set; } = true;

    public string? Tooltip { get; private set; }

    public IReadOnlyList<GibiOMatic.App.Services.Tray.TrayMenuItem?> Menu { get; private set; } = [];

    public List<(string Title, string Text)> Notifications { get; } = [];

    public event EventHandler? Activated;

    public event EventHandler? NotificationClicked;

    public void Show(string tooltip, IReadOnlyList<GibiOMatic.App.Services.Tray.TrayMenuItem?> menu) => (Tooltip, Menu) = (tooltip, menu);

    public void Notify(string title, string text) => Notifications.Add((title, text));

    public void Click() => Activated?.Invoke(this, EventArgs.Empty);

    public void ClickNotification() => NotificationClicked?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
    }
}

public sealed class FakeAppUpdater : IAppUpdater
{
    public bool IsSupported { get; set; } = true;

    public string CurrentVersion => "0.6.0";

    /// <summary>The version the source offers; null = up to date.</summary>
    public string? Offered { get; set; }

    public bool Unreachable { get; set; }

    public AppUpdate? Applied { get; private set; }

    public int Checks { get; private set; }

    public Task<AppUpdate?> CheckAsync(CancellationToken ct)
    {
        Checks++;
        return Unreachable
            ? throw new HttpRequestException("404")
            : Task.FromResult(Offered is null ? null : new AppUpdate(Offered, Offered));
    }

    public Task DownloadAsync(AppUpdate update, IProgress<int>? progress, CancellationToken ct)
    {
        progress?.Report(100);
        return Task.CompletedTask;
    }

    public void ApplyOnExit(AppUpdate update) => Applied = update;
}

public sealed class FakeStartupRegistration : IStartupRegistration
{
    public bool IsSupported => true;

    public List<StartupMode> Applied { get; } = [];

    public bool Apply(StartupMode mode)
    {
        Applied.Add(mode);
        return true;
    }
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

public sealed class FakeReaderLauncher : IReaderLauncher
{
    public List<GibiOMatic.App.ViewModels.ReaderRequest> Opened { get; } = [];

    public void Open(GibiOMatic.App.ViewModels.ReaderRequest request) => Opened.Add(request);
}

/// <summary>Pages as plain objects that only know their size (no Avalonia platform in tests).</summary>
public sealed class FakeReaderImages : GibiOMatic.App.ViewModels.IReaderImages
{
    public Avalonia.Media.IImage Create(SkiaSharp.SKBitmap bitmap)
    {
        using (bitmap)
        {
            return new FakeImage(bitmap.Width, bitmap.Height, bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2));
        }
    }
}

public sealed class FakeImage(int width, int height, SkiaSharp.SKColor center) : Avalonia.Media.IImage, IDisposable
{
    public Avalonia.Size Size { get; } = new(width, height);

    public SkiaSharp.SKColor Center { get; } = center;

    public bool Disposed { get; private set; }

    public void Draw(Avalonia.Media.DrawingContext context, Avalonia.Rect sourceRect, Avalonia.Rect destRect)
    {
    }

    public void Dispose() => Disposed = true;
}
