using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Luna.Core.Auth;
using Luna.Protocol;
using Luna.Protocol.Auth;

namespace Luna.Core.ViewModels;

/// <summary>
/// State of the QR login screen: restores the saved session, otherwise runs a QR login.
/// It exposes states and flags only; the view maps them to localized text. Every state change goes through
/// <see cref="IUiDispatcher.Post"/> because the QR callbacks arrive on background threads.
/// </summary>
public sealed partial class LoginViewModel : ObservableObject, IDisposable
{
    /// <summary>The server error code sent when the user declines the login on the phone.</summary>
    private const int DeclinedErrorCode = -13;

    private readonly IAuthService _auth;
    private readonly IUiDispatcher _ui;
    private readonly object _gate = new();

    // 0 = idle, 1 = a run is in progress. Entered with CompareExchange from any thread.
    private int _running;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public LoginViewModel(IAuthService auth, IUiDispatcher ui)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(ui);
        _auth = auth;
        _ui = ui;
        StartCommand = new AsyncRelayCommand(StartAsync);
        CancelCommand = new RelayCommand(Cancel);
    }

    [ObservableProperty]
    public partial LoginState State { get; private set; }

    [ObservableProperty]
    public partial LoginFailure Failure { get; private set; }

    /// <summary>The numeric server error code; set only when <see cref="Failure"/> is <see cref="LoginFailure.Server"/>.</summary>
    [ObservableProperty]
    public partial int? ServerErrorCode { get; private set; }

    /// <summary>The PNG of the QR code to scan; null when none is shown.</summary>
    [ObservableProperty]
    public partial byte[]? QrPng { get; private set; }

    [ObservableProperty]
    public partial AuthSession? Session { get; private set; }

    /// <summary>Starts the login; ignored while a run is in progress. Use it again to retry.</summary>
    public IAsyncRelayCommand StartCommand { get; }

    /// <summary>Cancels the run in progress; does nothing when idle.</summary>
    public IRelayCommand CancelCommand { get; }

    /// <summary>Cancels a running login. The token source is disposed when the run unwinds.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _cts?.Cancel();
        }
    }

    /// <summary>Never prints the QR image or the session.</summary>
    public override string ToString() => nameof(LoginViewModel);

    private void Cancel()
    {
        lock (_gate)
        {
            _cts?.Cancel();
        }
    }

    private async Task StartAsync()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_disposed)
            {
                Interlocked.Exchange(ref _running, 0);
                return;
            }

            cts = _cts = new CancellationTokenSource();
        }

        try
        {
            await RunAsync(cts.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _cts = null;
                cts.Dispose();
            }

            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        Post(() =>
        {
            Failure = LoginFailure.None;
            ServerErrorCode = null;
            QrPng = null;
            Session = null;
            State = LoginState.Restoring;
        });

        try
        {
            AuthSession? session = await _auth.RestoreAsync(token).ConfigureAwait(false);
            session ??= await _auth.LoginWithQrAsync(OnQrEvent, token).ConfigureAwait(false);

            Post(() =>
            {
                QrPng = null;
                Session = session;
                State = LoginState.SignedIn;
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Post(() => End(LoginState.Cancelled, LoginFailure.None));
        }
        catch (OperationCanceledException)
        {
            // HttpClient reports its own timeout as a cancellation while our token is still alive.
            Post(() => End(LoginState.Failed, LoginFailure.Network));
        }
        catch (HttpRequestException)
        {
            Post(() => End(LoginState.Failed, LoginFailure.Network));
        }
        catch (ApiException ex) when (ex.ErrorCode == DeclinedErrorCode)
        {
            // The Declined event normally arrived first; posting it again is harmless.
            Post(() => End(LoginState.Declined, LoginFailure.None));
        }
        catch (ApiException ex)
        {
            // Only the numeric code is kept, never the server's message text.
            int code = ex.ErrorCode;
            Post(() => End(LoginState.Failed, LoginFailure.Server, code));
        }
        catch (TimeoutException)
        {
            Post(() => End(LoginState.Failed, LoginFailure.QrTimedOut));
        }
        catch (InvalidDataException)
        {
            Post(() => End(LoginState.Failed, LoginFailure.UnexpectedResponse));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // Saving the session after a successful login failed (disk full, protected folder, DPAPI).
            Post(() => End(LoginState.Failed, LoginFailure.Storage));
        }
        catch (Exception)
        {
            // A bug, not a user-visible condition: leave the progress state, then let it reach the app's handler.
            Post(() => End(LoginState.Failed, LoginFailure.Unexpected));
            throw;
        }
    }

    private void OnQrEvent(QrLoginEvent e)
    {
        switch (e)
        {
            case QrGenerated generated:
                byte[] png = generated.PngImage;
                Post(() =>
                {
                    QrPng = png;
                    State = LoginState.WaitingForScan;
                });
                break;
            case QrScanned:
                // The display name and avatar of the scanning user are deliberately not kept.
                Post(() => State = LoginState.WaitingForConfirm);
                break;
            case QrExpired:
                Post(() => State = LoginState.RefreshingCode);
                break;
            case QrDeclined:
                Post(() => End(LoginState.Declined, LoginFailure.None));
                break;
        }
    }

    /// <summary>Applies a terminal state. Runs on the UI thread, inside a posted action.</summary>
    private void End(LoginState state, LoginFailure failure, int? serverErrorCode = null)
    {
        QrPng = null;
        Failure = failure;
        ServerErrorCode = serverErrorCode;
        State = state;
    }

    private void Post(Action action) => _ui.Post(action);
}
