using System.Globalization;
using Avalonia;
using Avalonia.Controls;

namespace GibiOMatic.App.Controls;

/// <summary>
/// Attached behaviour: shows the text of a <see cref="TextBlock"/> in capitals (section labels), like
/// CSS <c>text-transform: uppercase</c>. The resx keeps the normal case for screen readers and translations.
/// </summary>
public static class UpperCase
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, bool>("IsEnabled", typeof(UpperCase));

    static UpperCase()
    {
        IsEnabledProperty.Changed.AddClassHandler<TextBlock>((block, _) => Apply(block));
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((block, _) => Apply(block));
    }

    public static bool GetIsEnabled(TextBlock block) => block.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(TextBlock block, bool value) => block.SetValue(IsEnabledProperty, value);

    private static void Apply(TextBlock block)
    {
        // SetCurrentValue keeps the binding; the upper text does not change again, so this ends.
        if (GetIsEnabled(block) && block.Text is { } text && text.ToUpper(CultureInfo.CurrentCulture) is var upper && upper != text)
        {
            block.SetCurrentValue(TextBlock.TextProperty, upper);
        }
    }
}
