using Avalonia;
using Avalonia.Controls;

namespace GibiOMatic.App.Controls;

/// <summary>Takes the available width and a height of <c>width × Ratio</c> (covers are 2:3, so 1.5).</summary>
public sealed class AspectBox : Decorator
{
    public static readonly StyledProperty<double> RatioProperty =
        AvaloniaProperty.Register<AspectBox, double>(nameof(Ratio), 1.5);

    static AspectBox()
    {
        AffectsMeasure<AspectBox>(RatioProperty);
    }

    public double Ratio
    {
        get => GetValue(RatioProperty);
        set => SetValue(RatioProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 150 : availableSize.Width;
        var size = new Size(width, width * Ratio);
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
