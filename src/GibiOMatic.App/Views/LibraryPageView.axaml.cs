using Avalonia.Controls;
using GibiOMatic.App.ViewModels;

namespace GibiOMatic.App.Views;

public partial class LibraryPageView : UserControl
{
    public LibraryPageView()
    {
        InitializeComponent();

        // The grid needs its width to decide how many columns fit; everything else is in the view model.
        GridRows.SizeChanged += (_, e) => (DataContext as LibraryPageViewModel)?.SetViewportWidth(e.NewSize.Width);
    }
}
