using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GibiOMatic.App.ViewModels;

/// <summary>
/// Shown while the remembered library opens: the window keeps answering even when the drive
/// (Google Drive) is slow. A failure stays here with "Try again"; nothing retries by itself.
/// </summary>
public sealed partial class LibraryOpeningViewModel(string path, Func<Task> retry, Action chooseAnother) : ViewModelBase
{
    public string Path { get; } = path;

    /// <summary>Still opening after a while: offers to keep waiting or to choose another library.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpening))]
    public partial bool IsSlow { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpening), nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    /// <summary>The plain "abrindo" state: no slow warning and no error.</summary>
    public bool IsOpening => !IsSlow && !HasError;

    [RelayCommand]
    private void KeepWaiting() => IsSlow = false;

    [RelayCommand]
    private void ChooseAnother() => chooseAnother();

    [RelayCommand]
    private Task RetryAsync() => retry();
}
