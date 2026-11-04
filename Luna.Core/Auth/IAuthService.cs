using Luna.Protocol.Auth;

namespace Luna.Core.Auth;

/// <summary>The login operations the UI layer depends on; lets view models be tested with a fake.</summary>
public interface IAuthService
{
    /// <summary>Logs in again with the saved session; null if there is none or the server rejected it.</summary>
    Task<AuthSession?> RestoreAsync(CancellationToken cancellationToken = default);

    /// <summary>Logs in with a QR code. <paramref name="onEvent"/> is called on background threads.</summary>
    Task<AuthSession> LoginWithQrAsync(Action<QrLoginEvent> onEvent, CancellationToken cancellationToken = default);
}
