using CommunityToolkit.Mvvm.ComponentModel;

namespace GibiOMatic.App.ViewModels;

public abstract class ViewModelBase : ObservableObject;

/// <summary>A page shown in the content area of the shell.</summary>
public abstract partial class PageViewModel : ViewModelBase, IDisposable
{
    public abstract string Title { get; }

    [ObservableProperty]
    public partial string? Subtitle { get; protected set; }

    /// <summary>Called every time the page is shown.</summary>
    public virtual Task OnNavigatedToAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Unsubscribes from long-lived services when the shell is replaced.</summary>
    public virtual void Dispose()
    {
    }
}
