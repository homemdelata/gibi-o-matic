using Avalonia.Controls.Primitives;

namespace GibiOMatic.App.Controls;

/// <summary>
/// The ornament on the right of a page title: a magenta bar over a shorter cyan one, like the
/// misregistration of the icon. Drawn by its template in Themes/Controls.axaml.
/// </summary>
public sealed class TitleMark : TemplatedControl
{
    public TitleMark()
    {
        IsHitTestVisible = false;
        Focusable = false;
    }
}
