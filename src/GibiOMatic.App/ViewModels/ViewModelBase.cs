using CommunityToolkit.Mvvm.ComponentModel;

namespace GibiOMatic.App.ViewModels;

public abstract class ViewModelBase : ObservableObject;

/// <summary>Go to a page, optionally with the books it should show (Editor, Review).</summary>
/// <param name="Sequence">
/// The books the user was going through, in order (the Library's filter and search): the Editor's
/// "Save and next" walks it. <c>null</c> when the books were not picked from such a list.
/// </param>
public sealed record NavigationRequest(NavPage Page, IReadOnlyList<string>? BookIds = null, IReadOnlyList<string>? Sequence = null);

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

    /// <summary>The list the books were picked from (see <see cref="NavigationRequest.Sequence"/>), set with them.</summary>
    public virtual void SetSequence(IReadOnlyList<string>? bookIds)
    {
    }

    protected void RequestNavigation(NavPage page, IReadOnlyList<string>? bookIds = null, IReadOnlyList<string>? sequence = null) =>
        NavigationRequested?.Invoke(this, new NavigationRequest(page, bookIds, sequence));

    /// <summary>Unsubscribes from long-lived services when the shell is replaced.</summary>
    public virtual void Dispose()
    {
    }
}
