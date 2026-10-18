using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using GibiOMatic.App.ViewModels;

namespace GibiOMatic.App.Views;

public partial class ReaderWindow : Window
{
    private ReaderViewModel? _viewModel;
    private NativeWebView? _web;

    public ReaderWindow()
    {
        InitializeComponent();
        AddHandler(PointerMovedEvent, OnPointerMovedAnywhere, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.NavigationRequested -= OnNavigationRequested;
            _viewModel.CloseRequested -= OnCloseRequested;
        }

        _viewModel = DataContext as ReaderViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
            _viewModel.NavigationRequested += OnNavigationRequested;
            _viewModel.CloseRequested += OnCloseRequested;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Pages.Focus();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && _viewModel is not null)
        {
            _viewModel.IsFullScreen = WindowState == WindowState.FullScreen;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(ReaderViewModel.IsFullScreen):
                WindowState = vm.IsFullScreen ? WindowState.FullScreen : WindowState.Normal;
                vm.PointerAtEdge = false;
                break;
            case nameof(ReaderViewModel.CurrentPage):
                Strip.ScrollIntoView(vm.CurrentPage);
                break;
        }
    }

    /// <summary>In full screen, the toolbar and the thumbnails come back when the pointer reaches the edges.</summary>
    private void OnPointerMovedAnywhere(object? sender, PointerEventArgs e)
    {
        if (_viewModel is not { IsFullScreen: true } vm)
        {
            return;
        }

        var y = e.GetPosition(this).Y;
        vm.PointerAtEdge = y < 64 || (vm.IsPaged && vm.ShowThumbnails && y > Bounds.Height - 210);
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    private void OnNavigationRequested(object? sender, Uri uri)
    {
        if (_web is null)
        {
            _web = CreateWebView();
            WebHost.Children.Add(_web);
        }

        _web.Navigate(uri);
    }

    /// <summary>
    /// A private profile (nothing kept between sessions), no developer tools, no new windows, and
    /// only the book's own pages: anything else is cancelled before it loads.
    /// </summary>
    private NativeWebView CreateWebView()
    {
        var web = new NativeWebView();
        web.EnvironmentRequested += (_, e) =>
        {
            e.EnableDevTools = false;
            switch (e)
            {
                case WindowsWebView2EnvironmentRequestedEventArgs webView2:
                    webView2.IsInPrivateModeEnabled = true;
                    webView2.UserDataFolder = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GibiOMatic", "webview");
                    break;
                case GtkWebViewEnvironmentRequestedEventArgs gtk:
                    gtk.EphemeralDataManager = true;
                    break;
            }
        };
        web.NavigationStarted += (_, e) =>
        {
            if (e.Request is null || _viewModel?.AllowNavigation(e.Request) != true)
            {
                e.Cancel = true;
            }
        };
        web.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess && e.Request is { } uri)
            {
                _viewModel?.OnNavigated(uri);
            }
        };
        web.NewWindowRequested += (_, e) => e.Handled = true;
        return web;
    }
}
