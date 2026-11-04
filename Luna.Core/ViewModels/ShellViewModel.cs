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
/// Home will gain the conversation list in T7b.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public ShellViewModel(LoginViewModel login)
    {
        ArgumentNullException.ThrowIfNull(login);
        Login = login;
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

    /// <summary>Never prints the session.</summary>
    public override string ToString() => nameof(ShellViewModel);

    private void OnLoginPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LoginViewModel.State))
        {
            Sync();
        }
    }

    private void Sync()
    {
        bool signedIn = Login.State == LoginState.SignedIn && Login.Session is not null;
        Session = signedIn ? Login.Session : null;
        IsSignedIn = signedIn;
        Page = signedIn ? ShellPage.Home : ShellPage.Login;
    }
}
