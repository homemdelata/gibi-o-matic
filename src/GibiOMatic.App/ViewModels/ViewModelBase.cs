using CommunityToolkit.Mvvm.ComponentModel;

namespace GibiOMatic.App.ViewModels;

public abstract class ViewModelBase : ObservableObject;

/// <summary>Go to a page, optionally with the books it should show (Editor, Review).</summary>
public sealed record NavigationRequest(NavPage Page, IReadOnlyList<string>? BookIds = null);

/// <summary>A page shown in the content area of the shell.</summary>
public abstract partial class PageViewModel : ViewModelBase, IDisposable
{
    public abstract string Title { get; }

    [ObservableProperty]
    public partial string? Subtitle { get; protected set; }

    /// <summary>Asks the shell to show another page.</summary>
    public event EventHandler<NavigationRequest>? NavigationRequested;

    /// <summary>Called every time the page is shown.</summary>
    public virtual Task OnNavigatedToAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Called when another page is shown instead of this one.</summary>
    public virtual void OnNavigatedFrom()
    {
    }

    /// <summary>Books chosen elsewhere for this page (before <see cref="OnNavigatedToAsync"/>).</summary>
    public virtual void SetBooks(IReadOnlyList<string> bookIds)
    {
    }

    protected void RequestNavigation(NavPage page, IReadOnlyList<string>? bookIds = null) =>
        NavigationRequested?.Invoke(this, new NavigationRequest(page, bookIds));

    /// <summary>Unsubscribes from long-lived services when the shell is replaced.</summary>
    public virtual void Dispose()
    {
    }
}
