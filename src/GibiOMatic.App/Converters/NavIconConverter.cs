using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using GibiOMatic.App.ViewModels;

namespace GibiOMatic.App.Converters;

/// <summary>
/// Maps a <see cref="NavPage"/> to its icon geometry (resource <c>Icon.{Page}</c>); a string is taken
/// as the resource key itself (<c>Icon.Check</c>).
/// </summary>
public sealed class NavIconConverter : IValueConverter
{
    public static NavIconConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value switch { NavPage page => $"Icon.{page}", string key => key, _ => null }) is { } name
        && Application.Current?.TryGetResource(name, null, out var icon) == true
            ? icon as Geometry
            : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
