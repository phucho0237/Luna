using System;
using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Luna.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Luna.App;

/// <summary>
/// The QR login page. It only presents <see cref="LoginViewModel"/>: texts come from Resources.resw through
/// <see cref="LoginText"/>, and it shows no name, avatar or uid.
/// </summary>
public sealed partial class LoginView : UserControl
{
    private LoginViewModel? _login;
    private byte[]? _shownPng;
    private int _qrVersion;
    private Button? _focusTarget;

    public LoginView()
    {
        InitializeComponent();
        Loaded += (_, _) => FocusVisibleButton(force: true);
    }

    /// <summary>Starts presenting <paramref name="login"/>. Call <see cref="Detach"/> when the window closes.</summary>
    public void Attach(LoginViewModel login)
    {
        ArgumentNullException.ThrowIfNull(login);
        Detach();
        _login = login;
        StartButton.Command = login.StartCommand;
        CancelButton.Command = login.CancelCommand;
        login.PropertyChanged += OnLoginPropertyChanged;
        Refresh();
    }

    /// <summary>Stops presenting and releases the decoded QR image.</summary>
    public void Detach()
    {
        if (_login is not null)
        {
            _login.PropertyChanged -= OnLoginPropertyChanged;
        }

        _login = null;
        StartButton.Command = null;
        CancelButton.Command = null;
        ReleaseImage();
    }

    /// <summary>Drops the QR image; an older decode still running is discarded when it finishes.</summary>
    public void ReleaseImage()
    {
        _qrVersion++;
        _shownPng = null;
        QrImage.Source = null;
    }

    private void OnLoginPropertyChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        LoginViewModel? login = _login;
        if (login is null)
        {
            return;
        }

        LoginState state = login.State;
        Status.Text = LoginText.Status(state, login.Failure, login.ServerErrorCode);

        bool busy = state is LoginState.Restoring or LoginState.RefreshingCode;
        Progress.IsActive = busy;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        bool showQr = state is LoginState.WaitingForScan or LoginState.WaitingForConfirm;
        Instruction.Visibility = state == LoginState.WaitingForScan ? Visibility.Visible : Visibility.Collapsed;

        bool canStart = state is LoginState.Idle or LoginState.Failed or LoginState.Declined or LoginState.Cancelled;
        bool canCancel = state is LoginState.Restoring or LoginState.WaitingForScan
            or LoginState.WaitingForConfirm or LoginState.RefreshingCode;
        StartButton.Visibility = canStart ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = canCancel ? Visibility.Visible : Visibility.Collapsed;

        UpdateQr(showQr ? login.QrPng : null);
        FocusVisibleButton(force: false);
    }

    private void UpdateQr(byte[]? png)
    {
        if (ReferenceEquals(png, _shownPng))
        {
            return;
        }

        _shownPng = png;
        int version = ++_qrVersion;
        if (png is null)
        {
            QrImage.Source = null;
            return;
        }

        _ = DecodeQrAsync(png, version);
    }

    // Only the newest version may assign the image: if QrPng changes twice quickly, the older decode is discarded
    // when it finishes. A decode error (invalid PNG, disposed stream) leaves the image empty. The stream is disposed
    // on every path.
    private async Task DecodeQrAsync(byte[] png, int version)
    {
        BitmapImage? bitmap = null;
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(png.AsBuffer());
            stream.Seek(0);
            var decoded = new BitmapImage();
            await decoded.SetSourceAsync(stream);
            bitmap = decoded;
        }
        catch (Exception)
        {
            bitmap = null;
        }

        if (version == _qrVersion)
        {
            QrImage.Source = bitmap;
        }
    }

    // Moves keyboard focus to the button that is visible, so Enter or Space works at once. It is re-applied when the
    // visible button changes (Start and Cancel swap places), not on every refresh, so it never steals focus later.
    private void FocusVisibleButton(bool force)
    {
        Button target = CancelButton.Visibility == Visibility.Visible ? CancelButton : StartButton;
        if (target.Visibility != Visibility.Visible)
        {
            return;
        }

        if (!force && ReferenceEquals(target, _focusTarget))
        {
            return;
        }

        _focusTarget = target;
        if (!target.Focus(FocusState.Programmatic))
        {
            // The button became visible in this pass and has no layout yet; try again once it has.
            DispatcherQueue.TryEnqueue(() => target.Focus(FocusState.Programmatic));
        }
    }
}
