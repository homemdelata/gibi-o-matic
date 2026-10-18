using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GibiOMatic.App.ViewModels;
using GibiOMatic.App.Views;
using GibiOMatic.Core.Settings;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace GibiOMatic.App.Services;

/// <summary>Opens books in the reader.</summary>
public interface IReaderLauncher
{
    void Open(ReaderRequest request);
}

/// <summary>
/// Each book opens in its own window, so it can sit next to the Review while comparing editions;
/// opening a book that is already open brings its window to the front.
/// </summary>
public sealed class ReaderWindowLauncher(
    ISettingsService settings,
    IUiThread ui,
    IReaderImages images,
    ReadingTracker reading,
    ILoggerFactory loggers) : IReaderLauncher
{
    private readonly List<ReaderWindow> _windows = [];

    public void Open(ReaderRequest request)
    {
        if (_windows.FirstOrDefault(w => w.DataContext is ReaderViewModel vm && vm.BookId == request.BookId
                                         && vm.FilePath.Equals(request.FilePath, StringComparison.OrdinalIgnoreCase)) is { } open)
        {
            open.Activate();
            return;
        }

        var viewModel = new ReaderViewModel(request, settings, ui, images, reading, loggers.CreateLogger<ReaderViewModel>());
        var window = new ReaderWindow { DataContext = viewModel };
        _windows.Add(window);
        window.Closed += async (_, _) =>
        {
            _windows.Remove(window);
            await viewModel.DisposeAsync();
        };
        window.Show();
        _ = viewModel.LoadAsync();
    }
}

/// <summary>Rendered pages as Avalonia bitmaps (the pixels are copied).</summary>
public sealed class AvaloniaReaderImages : IReaderImages
{
    public IImage Create(SKBitmap bitmap)
    {
        using (bitmap)
        {
            using var bgra = bitmap.ColorType == SKColorType.Bgra8888 ? null : bitmap.Copy(SKColorType.Bgra8888);
            var source = bgra ?? bitmap;
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, source.GetPixels(), new PixelSize(source.Width, source.Height),
                new Vector(96, 96), source.RowBytes);
        }
    }
}
