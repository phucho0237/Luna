using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Luna.Core.Auth;

namespace Luna.Core.ViewModels;

/// <summary>The top-level page the shell shows.</summary>
public enum ShellPage
{
    Login,
    Home,
}

/// <summary>
/// Root view model: shows the login page until the login reaches <see cref="LoginState.SignedIn"/>, then home.
/// It follows the login view model, which only changes on the UI thread, so it needs no dispatcher.
/// While signed in it owns one <see cref="HomeViewModel"/>, built by the injected factory from the session; the home is
/// built before the page changes and disposed as soon as the login leaves the signed-in state. The shell never starts
/// the home: the home view does (<see cref="HomeViewModel.StartCommand"/>).
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly Func<AuthSession, HomeViewModel>? _homeFactory;
    private bool _disposed;

    /// <param name="homeFactory">
    /// Builds the home for a session. Optional: without it <see cref="Home"/> stays null (the old behavior). A factory
    /// that throws also leaves <see cref="Home"/> null; the shell keeps working.
    /// </param>
    public ShellViewModel(LoginViewModel login, Func<AuthSession, HomeViewModel>? homeFactory = null)
    {
        ArgumentNullException.ThrowIfNull(login);
        Login = login;
        _homeFactory = homeFactory;
        login.PropertyChanged += OnLoginPropertyChanged;
        Sync();
    }

    public LoginViewModel Login { get; }

    [ObservableProperty]
    public partial ShellPage Page { get; private set; }

    [ObservableProperty]
    public partial bool IsSignedIn { get; private set; }

    /// <summary>The signed-in session; null until the login succeeds.</summary>
    [ObservableProperty]
    public partial AuthSession? Session { get; private set; }

    /// <summary>The home of the signed-in session; null while signed out, without a factory, or when the factory failed.</summary>
    [ObservableProperty]
    public partial HomeViewModel? Home { get; private set; }

    /// <summary>Never prints the session.</summary>
    public override string ToString() => nameof(ShellViewModel);

    /// <summary>Stops following the login and disposes the home. Safe to call twice.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Login.PropertyChanged -= OnLoginPropertyChanged;
        HomeViewModel? home = Home;
        Home = null;
        home?.Dispose();
    }

    private void OnLoginPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_disposed && e.PropertyName == nameof(LoginViewModel.State))
        {
            Sync();
        }
    }

    private void Sync()
    {
        bool signedIn = Login.State == LoginState.SignedIn && Login.Session is not null;
        AuthSession? session = signedIn ? Login.Session : null;

        // The home follows the session: dispose it when the session goes away or changes, build it before the page changes.
        if (Home is not null && !ReferenceEquals(session, Session))
        {
            HomeViewModel old = Home;
            Home = null;
            old.Dispose();
        }

        if (session is not null && Home is null)
        {
            Home = BuildHome(session);
        }

        Session = session;
        IsSignedIn = signedIn;
        Page = signedIn ? ShellPage.Home : ShellPage.Login;
    }

    private HomeViewModel? BuildHome(AuthSession session)
    {
        if (_homeFactory is null)
        {
            return null;
        }

        try
        {
            return _homeFactory(session);
        }
        catch (Exception)
        {
            // The exception is dropped on purpose: it could carry session or server text. The page stays usable
            // without a home view model.
            return null;
        }
    }
}
