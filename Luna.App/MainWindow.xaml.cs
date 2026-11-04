using System;
using System.ComponentModel;
using Luna.Core.ViewModels;
using Microsoft.UI.Xaml;

namespace Luna.App;

/// <summary>Hosts the login page until the shell reaches Home, then the home page.</summary>
public sealed partial class MainWindow : Window
{
    private bool _started;

    public MainWindow(ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        Shell = shell;
        InitializeComponent();

        // A Window is not a FrameworkElement, so x:Uid does not apply to its title.
        string title = LoginText.Get("AppTitle");
        if (title.Length > 0)
        {
            Title = title;
        }

        LoginPane.Attach(shell.Login);
        Shell.PropertyChanged += OnShellPropertyChanged;
        ApplyPage();
        Root.Loaded += OnRootLoaded;
        Closed += OnClosed;
    }

    public ShellViewModel Shell { get; }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= OnRootLoaded;
        if (_started)
        {
            return;
        }

        _started = true;
        Shell.Login.StartCommand.Execute(null);
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Page))
        {
            ApplyPage();
        }
    }

    private void ApplyPage()
    {
        bool login = Shell.Page == ShellPage.Login;
        LoginPane.Visibility = login ? Visibility.Visible : Visibility.Collapsed;
        HomePane.Visibility = login ? Visibility.Collapsed : Visibility.Visible;
        if (!login)
        {
            LoginPane.ReleaseImage();
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Shell.PropertyChanged -= OnShellPropertyChanged;
        LoginPane.Detach();
        Shell.Login.Dispose();
    }
}
