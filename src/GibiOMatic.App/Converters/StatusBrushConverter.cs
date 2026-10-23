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
/// The brushes follow the theme: a binding is not evaluated again when the theme changes, so each
/// brush is one shared instance whose colour is updated (F6).
/// </summary>
public sealed class StatusBrushConverter : IValueConverter
{
    /// <summary>Opacity of the tinted pills.</summary>
    private const byte TintAlpha = 0x24;

    private static readonly Dictionary<(string Key, bool Tint), SolidColorBrush> Brushes = [];
    private static bool _followingTheme;

    public static StatusBrushConverter Instance { get; } = new();

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
                "Ok" => Tinted("OkColor"),
                "Warn" => Tinted("WarnColor"),
                "Bad" => Tinted("BadColor"),
                "Duplicate" or "Busy" => Tinted("AccentColor"),
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
                "Duplicate" or "Busy" => Resource("AccentTextBrush"),
                _ => Resource("TextMutedBrush"),
            },
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static object? Resource(string key) => Live(key, tint: false);

    private static object? Tinted(string colorKey) => Live(colorKey, tint: true);

    /// <summary>The shared brush of a theme resource (a brush, or a colour made translucent for a tint).</summary>
    private static SolidColorBrush? Live(string key, bool tint)
    {
        if (Application.Current is not { } app)
        {
            return null;
        }

        if (!_followingTheme)
        {
            _followingTheme = true;
            app.ActualThemeVariantChanged += (_, _) =>
            {
                foreach (var ((k, t), brush) in Brushes)
                {
                    brush.Color = ColorOf(app, k, t);
                }
            };
        }

        if (!Brushes.TryGetValue((key, tint), out var shared))
        {
            shared = new SolidColorBrush(ColorOf(app, key, tint));
            Brushes[(key, tint)] = shared;
        }

        return shared;
    }

    private static Color ColorOf(Application app, string key, bool tint)
    {
        app.TryGetResource(key, app.ActualThemeVariant, out var resource);
        var color = resource switch
        {
            Color c => c,
            ISolidColorBrush b => b.Color,
            _ => Colors.Transparent,
        };
        return tint ? Color.FromArgb(TintAlpha, color.R, color.G, color.B) : color;
    }
}
