using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using GibiOMatic.App.ViewModels;
using GibiOMatic.Core.Quality;

namespace GibiOMatic.App.Converters;

/// <summary>
/// Colours of statuses (quality band, check, inbox item). The parameter picks the part:
/// <c>Foreground</c> (text/icon colour, the default), <c>Fill</c> (solid dot) or <c>Background</c> (tinted pill).
/// </summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public static StatusBrushConverter Instance { get; } = new();

    private static readonly IBrush OkTint = new SolidColorBrush(Color.FromArgb(0x24, 0x4C, 0xC3, 0x8A));
    private static readonly IBrush WarnTint = new SolidColorBrush(Color.FromArgb(0x24, 0xF0, 0x8A, 0x4B));
    private static readonly IBrush BadTint = new SolidColorBrush(Color.FromArgb(0x24, 0xF2, 0x67, 0x7A));
    private static readonly IBrush AccentTint = new SolidColorBrush(Color.FromArgb(0x24, 0xF5, 0xC5, 0x42));
    private static readonly IBrush DuplicateText = new SolidColorBrush(Color.Parse("#F5D87A"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var tone = value switch
        {
            QualityBand.Ok or CheckStatus.Ok or StatusKind.Ok => "Ok",
            QualityBand.Review or CheckStatus.Warn or StatusKind.Warn => "Warn",
            QualityBand.Bad or CheckStatus.Bad or StatusKind.Bad => "Bad",
            StatusKind.Duplicate => "Duplicate",
            StatusKind.Busy => "Busy",
            _ => "Neutral",
        };

        return (parameter as string) switch
        {
            "Background" => tone switch
            {
                "Ok" => OkTint,
                "Warn" => WarnTint,
                "Bad" => BadTint,
                "Duplicate" or "Busy" => AccentTint,
                _ => Resource("HoverBrush"),
            },
            "Fill" => tone switch
            {
                "Ok" => Resource("OkBrush"),
                "Warn" => Resource("WarnBrush"),
                "Bad" => Resource("BadBrush"),
                "Busy" => Resource("AccentBrush"),
                _ => Resource("OffBrush"),
            },
            _ => tone switch
            {
                "Ok" => Resource(value is StatusKind ? "OkTextBrush" : "OkBrush"),
                "Warn" => Resource(value is StatusKind ? "WarnTextBrush" : "WarnBrush"),
                "Bad" => Resource("BadBrush"),
                "Duplicate" => DuplicateText,
                "Busy" => Resource("AccentBrush"),
                _ => Resource("TextMutedBrush"),
            },
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static object? Resource(string key) =>
        Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var brush) == true ? brush : null;
}
