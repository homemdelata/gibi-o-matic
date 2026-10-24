using System.Globalization;
using System.IO.Compression;
using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using GibiOMatic.App;
using GibiOMatic.App.Services;
using GibiOMatic.App.ViewModels;
using GibiOMatic.App.Views;
using GibiOMatic.Core;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Settings;
using GibiOMatic.Metadata;
using GibiOMatic.Metadata.Identification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace GibiOMatic.Screenshots;

/// <summary>
/// Renders the main screens of the app, headless, over a fictional library (made-up titles, people
/// and covers drawn here: nothing real, nothing copyrighted). Usage, from the repository root:
/// <c>dotnet run --project tools/Screenshots -- docs/images [pt-BR|en]</c>.
/// </summary>
internal static class Program
{
    private const string Marker = ".gibiomatic-demo";

    private static readonly string Repository = FindRepository();

    public static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Repository, "docs", "images"));
        var culture = CultureInfo.GetCultureInfo(args.Length > 1 ? args[1] : "pt-BR");
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture;
        Directory.CreateDirectory(output);

        // The paths show on the screens: a neutral folder, not one under the user's profile.
        var temp = OperatingSystem.IsWindows()
            ? Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "gibiomatic-demo")
            : "/tmp/gibiomatic-demo";
        if (Directory.Exists(temp))
        {
            if (!File.Exists(Path.Combine(temp, Marker)))
            {
                Console.Error.WriteLine($"{temp} exists and was not made by this tool; remove it first.");
                return 1;
            }

            Directory.Delete(temp, recursive: true);
        }

        Directory.CreateDirectory(temp);
        File.WriteAllText(Path.Combine(temp, Marker), "Made by tools/Screenshots; safe to delete.");
        try
        {
            var paths = new AppPaths(Path.Combine(temp, "config"), Path.Combine(temp, "data"));
            using var services = new ServiceCollection()
                .AddGibiOMatic(paths, new ConfigurationBuilder().Build())
                .AddSingleton<IBookIdentifier>(new FictionalIdentifier())
                .AddSingleton<IAppUpdater>(new NoUpdates())
                .BuildServiceProvider();

            AppBuilder.Configure(() => new GibiOMatic.App.App(services))
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .With(new FontManagerOptions { DefaultFamilyName = "avares://GibiOMatic/Assets/Fonts#IBM Plex Sans" })
                .SetupWithoutStarting();

            var done = new CancellationTokenSource();
            Exception? failure = null;
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await RunAsync(services, temp, output, culture.Name == "en" ? "-en" : string.Empty);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    done.Cancel();
                }
            });
            Dispatcher.UIThread.MainLoop(done.Token);
            if (failure is not null)
            {
                Console.Error.WriteLine(failure);
                return 1;
            }

            Console.WriteLine($"Screens written to {output}");
            return 0;
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task RunAsync(IServiceProvider services, string temp, string output, string suffix)
    {
        var settings = services.GetRequiredService<ISettingsService>();
        await settings.LoadAsync();
        var inbox = Directory.CreateDirectory(Path.Combine(temp, "Entrada")).FullName;
        var komga = Directory.CreateDirectory(Path.Combine(temp, "Komga")).FullName;
        await settings.SaveAsync(settings.Current with
        {
            InboxPath = inbox,
            Destinations =
            [
                new DestinationConfig { Id = "komga", Type = "komga", Name = "Komga de casa", Folder = komga, IncludeUnreviewed = false },
            ],
        });

        // The library: books arrive through the inbox and are identified like in the app.
        var session = services.GetRequiredService<LibrarySession>();
        await session.CreateAsync(Path.Combine(temp, "Arquivo"));
        var downloads = Path.Combine(temp, "Downloads");
        var files = FictionalBooks.All.Select(b => b.Write(downloads)).ToList();
        await session.Queue!.AddAsync(files);
        await session.Queue.WaitUntilIdleAsync();
        await session.Queue.ConfirmAsync();
        await session.Queue.WaitUntilIdleAsync();
        await Task.Delay(500);
        await session.Identification!.WaitUntilIdleAsync();

        // What waits in the inbox: a new book, a better format of one in the library, a file already there.
        var arriving = Path.Combine(temp, "Novos");
        await session.Queue.AddAsync(
        [
            FictionalBooks.Arriving.Write(arriving),
            FictionalBooks.All[0].Write(arriving, "pdf"),
            File.Exists(files[3]) ? Copy(files[3], Path.Combine(arriving, "noite_de_neon_v1.cbz")) : files[3],
        ]);
        await session.Queue.WaitUntilIdleAsync();

        var main = services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var shell = (ShellViewModel)main.Content!;
        var window = new MainWindow { DataContext = main, Width = 1440, Height = 900 };
        window.Show();

        async Task ShotAsync(NavPage page, string name, IReadOnlyList<string>? books = null, ThemeVariant? theme = null)
        {
            Application.Current!.RequestedThemeVariant = theme ?? ThemeVariant.Dark;
            shell.Navigate(new NavigationRequest(page, books));
            await shell.CurrentNavigation;
            for (var i = 0; i < 30; i++)
            {
                // Covers and lists load in the background; give them time and frames.
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                await Task.Delay(40);
            }

            var file = Path.Combine(output, $"{name}{suffix}.png");
            window.CaptureRenderedFrame()!.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            Console.WriteLine(file);
        }

        var library = (LibraryPageViewModel)shell.NavItems.First(n => n.Page == NavPage.Library).Content;
        await ShotAsync(NavPage.Library, "biblioteca");
        library.ViewMode = LibraryViewMode.Books;
        await ShotAsync(NavPage.Library, "biblioteca-claro", theme: ThemeVariant.Light);
        await ShotAsync(NavPage.Inbox, "entrada");
        await ShotAsync(NavPage.Review, "revisao");
        var editing = (await session.Index!.GetBooksAsync()).First(b => b.Metadata.Title.StartsWith("Capitã Cometa", StringComparison.Ordinal));
        await ShotAsync(NavPage.Editor, "editor", [editing.Metadata.Id]);
        await ShotAsync(NavPage.Destinations, "destinos");
        window.Close();
    }

    private static string Copy(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite: true);
        return to;
    }

    internal static string FontFile(string name) => Path.Combine(Repository, "src", "GibiOMatic.App", "Assets", "Fonts", name);

    private static string FindRepository()
    {
        var folder = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(folder, "GibiOMatic.sln")))
        {
            folder = Path.GetDirectoryName(folder) ?? throw new InvalidOperationException("Run from inside the repository.");
        }

        return folder;
    }
}

/// <summary>The tool is not an installed app: Velopack is not set up, and there is nothing to update.</summary>
internal sealed class NoUpdates : IAppUpdater
{
    public bool IsSupported => false;

    public string CurrentVersion => AppInfo.Version;

    public Task<AppUpdate?> CheckAsync(CancellationToken ct) => Task.FromResult<AppUpdate?>(null);

    public Task DownloadAsync(AppUpdate update, IProgress<int>? progress, CancellationToken ct) => Task.CompletedTask;

    public void ApplyOnExit(AppUpdate update)
    {
    }
}

/// <summary>A made-up book: metadata for ComicInfo.xml and a cover drawn with its colours.</summary>
internal sealed record FictionalBook(
    string FileName, string? Series, string? Number, string? Title, int? Year, string? Writer, string? Artist, string? Publisher,
    string CoverTitle, string CoverLine, SKColor Background, SKColor Ink, string? Format = "TPB")
{
    public string Write(string folder, string extension = "cbz")
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Path.ChangeExtension(FileName, extension));
        if (extension == "pdf")
        {
            FictionalPdf.Write(path, Cover(), Title);
            return path;
        }

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "000.jpg", Cover());
        for (var i = 1; i <= 3; i++)
        {
            Add(zip, $"{i:000}.jpg", Page(i));
        }

        if (Series is not null || Title is not null)
        {
            var entry = zip.CreateEntry("ComicInfo.xml");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(ComicInfo());
        }

        return path;
    }

    private static void Add(ZipArchive zip, string name, byte[] bytes)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
        stream.Write(bytes);
    }

    private string ComicInfo()
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ComicInfo>\n");
        void Add(string tag, object? value)
        {
            if (value is not null)
            {
                sb.Append($"  <{tag}>{System.Security.SecurityElement.Escape(value.ToString())}</{tag}>\n");
            }
        }

        Add("Title", Title);
        Add("Series", Series);
        Add("Number", Number);
        Add("Year", Year);
        Add("Writer", Writer);
        Add("Penciller", Artist);
        Add("Publisher", Publisher);
        Add("Format", Format);
        Add("LanguageISO", "pt");
        return sb.Append("</ComicInfo>\n").ToString();
    }

    /// <summary>A comic cover: flat colour, halftone dots, a band with the title.</summary>
    public byte[] Cover()
    {
        const int width = 800, height = 1200;
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(Background);
        using (var dots = new SKPaint { Color = Ink.WithAlpha(60), IsAntialias = true })
        {
            for (var y = 0; y < height; y += 22)
            {
                for (var x = (y / 22 % 2) * 11; x < width; x += 22)
                {
                    canvas.DrawCircle(x, y, 2 + 6f * y / height, dots);
                }
            }
        }

        using var circle = new SKPaint { Color = Ink.WithAlpha(200), IsAntialias = true };
        canvas.DrawCircle(width * 0.62f, height * 0.55f, 230, circle);
        using var band = new SKPaint { Color = SKColors.White.WithAlpha(235) };
        canvas.DrawRect(0, 70, width, 250, band);

        using var display = SKTypeface.FromFile(Program.FontFile("Anybody-ExtraBold.ttf"));
        using var text = SKTypeface.FromFile(Program.FontFile("IBMPlexSans-SemiBold.ttf"));
        using var titleFont = new SKFont(display, 96);
        using var lineFont = new SKFont(text, 40);
        // The title sits on a white band: the darker of the two colours.
        using var ink = new SKPaint { Color = Luminance(Ink) < Luminance(Background) ? Ink : Background, IsAntialias = true };
        var lines = CoverTitle.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            canvas.DrawText(lines[i], 40, 175 + i * 95, SKTextAlign.Left, titleFont, ink);
        }

        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawText(CoverLine, 40, height - 60, SKTextAlign.Left, lineFont, white);
        return Encode(surface);
    }

    private byte[] Page(int number)
    {
        using var surface = SKSurface.Create(new SKImageInfo(800, 1200));
        surface.Canvas.Clear(SKColors.White);
        using var panel = new SKPaint { Color = Background.WithAlpha(120), IsAntialias = true };
        using var stroke = new SKPaint { Color = SKColors.Black, IsStroke = true, StrokeWidth = 6 };
        for (var row = 0; row < 3; row++)
        {
            var rect = new SKRect(40, 40 + row * 380, 760, 400 + row * 380);
            surface.Canvas.DrawRect(rect, panel);
            surface.Canvas.DrawRect(rect, stroke);
        }

        _ = number;
        return Encode(surface);
    }

    private static double Luminance(SKColor c) => (0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue) / 255;

    private static byte[] Encode(SKSurface surface)
    {
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }
}

internal static class FictionalBooks
{
    private static readonly SKColor Navy = new(0x1B, 0x26, 0x4F);

    public static readonly IReadOnlyList<FictionalBook> All =
    [
        new("capita_cometa_v1.cbz", "Capitã Cometa", "1", "Capitã Cometa, Vol. 1: Órbita Baixa", 2019, "Ana Ribeiro", "Tomás Okafor", "Estrela Press",
            "CAPITÃ\nCOMETA", "VOL. 1 · ÓRBITA BAIXA", new SKColor(0xE6, 0x39, 0x46), Navy),
        new("capita_cometa_v2.cbz", "Capitã Cometa", "2", "Capitã Cometa, Vol. 2: Cauda de Fogo", 2020, "Ana Ribeiro", "Tomás Okafor", "Estrela Press",
            "CAPITÃ\nCOMETA", "VOL. 2 · CAUDA DE FOGO", new SKColor(0xF4, 0xA2, 0x61), Navy),
        new("capita_cometa_v3.cbz", "Capitã Cometa", "3", "Capitã Cometa, Vol. 3: Eclipse", 2021, "Ana Ribeiro", "Tomás Okafor", "Estrela Press",
            "CAPITÃ\nCOMETA", "VOL. 3 · ECLIPSE", new SKColor(0x2A, 0x9D, 0x8F), Navy),
        new("noite_de_neon.cbz", "Noite de Neon", "1", "Noite de Neon, Vol. 1", 2022, "Lia Matsuda", "Lia Matsuda", "Maré Alta Comics",
            "NOITE DE\nNEON", "VOL. 1", new SKColor(0x7B, 0x2C, 0xBF), new SKColor(0x10, 0xE0, 0xD0)),
        new("ultimos_farois_v1.cbz", "Os Últimos Faróis", "1", "Os Últimos Faróis, Vol. 1", 2018, "Bruno Carvalho", "Ines Duarte", "Maré Alta Comics",
            "OS ÚLTIMOS\nFARÓIS", "VOL. 1", new SKColor(0x26, 0x46, 0x53), new SKColor(0xE9, 0xC4, 0x6A)),
        new("ultimos_farois_v2.cbz", "Os Últimos Faróis", "2", "Os Últimos Faróis, Vol. 2", 2019, "Bruno Carvalho", "Ines Duarte", "Maré Alta Comics",
            "OS ÚLTIMOS\nFARÓIS", "VOL. 2", new SKColor(0x1D, 0x35, 0x57), new SKColor(0xF1, 0xFA, 0xEE)),
        new("bicho_papao_sa.cbz", "Bicho-Papão S/A", "1", "Bicho-Papão S/A", 2023, "Rafa Monteiro", "Duda Lima", "Selo Rabisco",
            "BICHO-\nPAPÃO S/A", "UMA HISTÓRIA COMPLETA", new SKColor(0x6A, 0x99, 0x4E), new SKColor(0x1A, 0x1A, 0x1A), "One-Shot"),
        new("jardim_de_ferro_v1.cbz", "O Jardim de Ferro", "1", "O Jardim de Ferro, Vol. 1", 2020, "Carla Nunes", "Kenji Arai", "Estrela Press",
            "O JARDIM\nDE FERRO", "VOL. 1", new SKColor(0x58, 0x58, 0x5A), new SKColor(0xF7, 0x7F, 0x00)),
        new("a_arte_de_capita_cometa.cbz", null, null, "A Arte de Capitã Cometa", 2022, "Tomás Okafor", "Tomás Okafor", "Estrela Press",
            "A ARTE DE\nCAPITÃ COMETA", "ESBOÇOS E CAPAS", new SKColor(0xF1, 0xFA, 0xEE), new SKColor(0xE6, 0x39, 0x46), "Artbook"),
        // Downloaded with no metadata at all: the identification proposes a match, which waits in the Review.
        new("ultimos_farois_v03_digital.cbz", null, null, null, null, null, null, null,
            "OS ÚLTIMOS\nFARÓIS", "VOL. 3", new SKColor(0x45, 0x7B, 0x9D), new SKColor(0xF1, 0xFA, 0xEE), null),
    ];

    public static readonly FictionalBook Arriving =
        new("rota_66_graus_v1.cbz", "Rota 66 Graus", "1", "Rota 66 Graus, Vol. 1", 2024, "Paula Freitas", "Ivo Prado", "Selo Rabisco",
            "ROTA 66\nGRAUS", "VOL. 1", new SKColor(0xFF, 0xB7, 0x03), new SKColor(0x02, 0x30, 0x47));
}

/// <summary>Answers like ComicVine would, from the made-up books: safe matches for most, one uncertain.</summary>
internal sealed class FictionalIdentifier : IBookIdentifier
{
    public Task<IdentificationResult> IdentifyAsync(BookMetadata book, MetadataSettings settings, string? searchText, CancellationToken ct)
    {
        IReadOnlyList<MetadataCandidate> candidates = book.Title.Contains("farois v03", StringComparison.OrdinalIgnoreCase)
            ? [Candidate("Os Últimos Faróis, Vol. 3", "Os Últimos Faróis", "3", 2021, "Bruno Carvalho", "Ines Duarte", 0.78, MatchReason.Title, MatchReason.Number),
               Candidate("Os Últimos Faróis, Vol. 2", "Os Últimos Faróis", "2", 2019, "Bruno Carvalho", "Ines Duarte", 0.41, MatchReason.Title)]
            : [Same(book)];
        return Task.FromResult(new IdentificationResult(BookQuery.From(book, searchText), candidates, []));
    }

    public Task<MetadataCandidate> CompleteAsync(MetadataCandidate candidate, CancellationToken ct) => Task.FromResult(candidate);

    public Task<IReadOnlyList<MetadataCandidate>> FromComicVineLinkAsync(BookMetadata book, string link, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MetadataCandidate>>([]);

    /// <summary>The book's own metadata, confirmed: approved automatically.</summary>
    private static MetadataCandidate Same(BookMetadata book) => new(new MetadataRecord
    {
        Source = SourceIds.ComicVine,
        Title = book.Title,
        SeriesName = book.Series?.Name,
        Number = book.Series?.Number,
        Kind = book.Kind,
        Date = book.Date,
        Publisher = book.Publisher,
        Credits = book.Credits,
        Summary = "Uma história inventada para as capturas de tela do Gibi-o-matic.",
        ComicVineId = "4000-1",
    }, 0.99, [MatchReason.Isbn, MatchReason.SecondSource, MatchReason.Title, MatchReason.Number]);

    private static MetadataCandidate Candidate(string title, string series, string number, int year, string writer, string artist,
        double confidence, params MatchReason[] reasons) => new(new MetadataRecord
    {
        Source = SourceIds.ComicVine,
        Title = title,
        SeriesName = series,
        Number = number,
        Kind = BookKind.Tpb,
        Date = new PartialDate(year, 3, null),
        Publisher = "Maré Alta Comics",
        Credits = [new Credit(CreditRole.Writer, writer), new Credit(CreditRole.Artist, artist)],
        Summary = "O farol da ilha apaga, e só a guardiã sabe por quê.",
        ComicVineId = "4000-3",
    }, confidence, reasons);
}

/// <summary>A one-page PDF whose page is the cover (a JPEG drawn over the whole page).</summary>
internal static class FictionalPdf
{
    public static void Write(string path, byte[] jpeg, string? title)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        var offsets = new List<long>();
        void Text(string s) => stream.Write(Encoding.ASCII.GetBytes(s));
        void Object(string body)
        {
            offsets.Add(stream.Position);
            Text($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }

        Text("%PDF-1.4\n");
        Object("<< /Type /Catalog /Pages 2 0 R >>");
        Object("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Object("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 900] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>");
        offsets.Add(stream.Position);
        Text($"4 0 obj\n<< /Type /XObject /Subtype /Image /Width 800 /Height 1200 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
        stream.Write(jpeg);
        Text("\nendstream\nendobj\n");
        const string content = "q 600 0 0 900 0 0 cm /Im0 Do Q";
        Object($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        var info = string.Empty;
        if (title is not null)
        {
            // Info strings in UTF-16BE with a BOM, as hex: accents survive.
            Object($"<< /Title <{Convert.ToHexString([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(title)])}> >>");
            info = $" /Info {offsets.Count} 0 R";
        }

        var xref = stream.Position;
        Text($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            Text($"{offset:0000000000} 00000 n \n");
        }

        Text($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R{info} >>\nstartxref\n{xref}\n%%EOF\n");
    }
}
