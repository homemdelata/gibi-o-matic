using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GibiOMatic.App.Views;

public partial class ReviewPageView : UserControl
{
    public ReviewPageView()
    {
        InitializeComponent();
    }

    /// <summary>The box of a field being edited takes the focus with its text selected, ready to type over.</summary>
    private void OnEditorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is Control editor && e.Property == IsVisibleProperty && editor.IsVisible
            && editor.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } box)
        {
            Dispatcher.UIThread.Post(() =>
            {
                box.Focus();
                box.SelectAll();
            }, DispatcherPriority.Input);
        }
    }
}
