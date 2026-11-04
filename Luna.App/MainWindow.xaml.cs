using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Luna.Core.Presentation;
using Luna.Core.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace Luna.App;

/// <summary>Hosts the login page until the shell reaches Home, then the home page.</summary>
public sealed partial class MainWindow : Window
{
    // Window sizes in device-independent pixels; WindowPlacement scales them by the window DPI.
    private const int LoginWidthDips = 420;
    private const int LoginHeightDips = 680;
    private const int HomeWidthDips = 1000;
    private const int HomeHeightDips = 700;
    private const int MinWidthDips = 360;
    private const int MinHeightDips = 520;
    private const int MarginPixels = 16;

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

        PlaceForPage(login);
    }

    // Sizes and centers the window for the current page. It runs when the window is created and when the page
    // changes, never on other refreshes, so a window the user moved or resized is left alone in between. A maximized
    // or minimized window is not touched on the home page. The login page is fixed (no maximize button, not
    // resizable), so it restores the window first. The DPI and the display are read again on every call because the window
    // may have been dragged to another monitor since the last page change.
    private void PlaceForPage(bool login)
    {
        try
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            AppWindow appWindow = AppWindow;
            int dpi = (int)GetDpiForWindow(hwnd);
            if (dpi <= 0)
            {
                dpi = WindowPlacement.BaseDpi;
            }

            RectInt32 work = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            PixelRect target = login
                ? WindowPlacement.Compute(LoginWidthDips, LoginHeightDips, dpi, work.X, work.Y, work.Width, work.Height, MarginPixels)
                : WindowPlacement.Compute(HomeWidthDips, HomeHeightDips, dpi, work.X, work.Y, work.Width, work.Height, MarginPixels);

            OverlappedPresenter? presenter = appWindow.Presenter as OverlappedPresenter;
            ApplyMinimumSize(presenter, dpi, target);
            var bounds = new RectInt32(target.X, target.Y, target.Width, target.Height);

            if (!login)
            {
                // Home: enable the flags BEFORE the resize so nothing can block it.
                if (presenter is not null)
                {
                    presenter.IsMaximizable = true;
                    presenter.IsResizable = true;
                    if (presenter.State != OverlappedPresenterState.Restored)
                    {
                        return;
                    }
                }

                appWindow.MoveAndResize(bounds);
                return;
            }

            // Login: a fixed compact window. Restore first so the resize is not skipped and the maximize button is
            // never turned off on a maximized window, then resize, then disable the flags AFTER the resize so the
            // programmatic resize is never blocked by IsResizable = false.
            if (presenter is not null && presenter.State != OverlappedPresenterState.Restored)
            {
                presenter.Restore();
            }

            appWindow.MoveAndResize(bounds);
            if (presenter is not null)
            {
                presenter.IsMaximizable = false;
                presenter.IsResizable = false;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            // Sizing is cosmetic: if the window cannot be placed, it keeps the size the system gave it.
            CrashLog.Write(ex);
        }
    }

    // The minimum never exceeds the placed size, so a small work area cannot force the window past its edge.
    private static void ApplyMinimumSize(OverlappedPresenter? presenter, int dpi, PixelRect target)
    {
        if (presenter is null)
        {
            return;
        }

        presenter.PreferredMinimumWidth = Math.Min(ScaleToPixels(MinWidthDips, dpi), target.Width);
        presenter.PreferredMinimumHeight = Math.Min(ScaleToPixels(MinHeightDips, dpi), target.Height);
    }

    private static int ScaleToPixels(int dips, int dpi) => (int)Math.Round(dips * (double)dpi / WindowPlacement.BaseDpi);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Shell.PropertyChanged -= OnShellPropertyChanged;
        LoginPane.Detach();
        Shell.Login.Dispose();
    }
}
