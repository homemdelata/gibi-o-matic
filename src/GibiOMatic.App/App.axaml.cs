using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GibiOMatic.App.ViewModels;
using GibiOMatic.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace GibiOMatic.App;

public partial class App : Application
{
    private readonly IServiceProvider? _services;

    // Parameterless constructor for the XAML designer.
    public App()
        : this(services: null)
    {
    }

    public App(IServiceProvider? services)
    {
        _services = services;
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && _services is not null)
        {
            var viewModel = _services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = viewModel };
            window.Opened += async (_, _) => await viewModel.InitializeAsync();
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
