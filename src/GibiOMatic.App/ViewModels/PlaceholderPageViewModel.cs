namespace GibiOMatic.App.ViewModels;

/// <summary>Page for screens delivered in later phases.</summary>
public sealed class PlaceholderPageViewModel : PageViewModel
{
    public PlaceholderPageViewModel(string title, string subtitle)
    {
        Title = title;
        Subtitle = subtitle;
    }

    public override string Title { get; }
}
