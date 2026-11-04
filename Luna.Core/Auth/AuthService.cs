using Luna.Core.Sessions;
using Luna.Protocol;
using Luna.Protocol.Auth;
using Luna.Protocol.Http;

namespace Luna.Core.Auth;

/// <summary>A logged-in session: the context (cookies, IMEI) and the getLoginInfo information.</summary>
public sealed record AuthSession(ClientContext Context, LoginInfo Info);

/// <summary>Combines "restore the saved session" and "log in with QR", then saves the session.</summary>
public sealed class AuthService
{
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:133.0) Gecko/20100101 Firefox/133.0";

    private readonly ISessionStore _store;
    private readonly HttpMessageHandler? _handler;
    private readonly Func<long>? _unixMillis;
    private readonly string _userAgent;
    private readonly Action<string>? _trace;

    /// <param name="handler">Only pass in tests.</param>
    /// <param name="unixMillis">Only pass in tests, to fix the clock.</param>
    public AuthService(
        ISessionStore store,
        HttpMessageHandler? handler = null,
        Func<long>? unixMillis = null,
        string? userAgent = null,
        Action<string>? trace = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _handler = handler;
        _unixMillis = unixMillis;
        _userAgent = userAgent ?? DefaultUserAgent;
        _trace = trace;
    }

    /// <summary>
    /// Logs in again with the saved session.
    /// </summary>
    /// <returns>
    /// The new session, or null if there is no session or the server rejected it (then a QR login is needed).
    /// A rejected session is not deleted, because the fault may be in the app itself; a new QR login will overwrite it.
    /// </returns>
    /// <exception cref="HttpRequestException">Network error. Deliberately not swallowed so the UI can show "offline".</exception>
    public async Task<AuthSession?> RestoreAsync(CancellationToken cancellationToken = default)
    {
        SessionData? saved = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (saved is null)
        {
            return null;
        }

        ClientContext context = saved.ToContext();
        try
        {
            LoginInfo info = await LoginAsync(context, cancellationToken).ConfigureAwait(false);
            await SaveAsync(context, cancellationToken).ConfigureAwait(false);
            return new AuthSession(context, info);
        }
        catch (ApiException ex)
        {
            _trace?.Invoke($"The saved session was rejected ({ex.ErrorCode}).");
            return null;
        }
    }

    /// <summary>Logs in with a QR code using a new IMEI, then saves the session.</summary>
    public async Task<AuthSession> LoginWithQrAsync(
        Action<QrLoginEvent> onEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onEvent);

        var context = new ClientContext(ClientContext.GenerateImei(_userAgent), _userAgent);

        using (var qrClient = new QrLoginClient(context, _handler, trace: _trace))
        {
            await qrClient.LoginAsync(onEvent, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        LoginInfo info = await LoginAsync(context, cancellationToken).ConfigureAwait(false);
        await SaveAsync(context, cancellationToken).ConfigureAwait(false);
        return new AuthSession(context, info);
    }

    private async Task<LoginInfo> LoginAsync(ClientContext context, CancellationToken cancellationToken)
    {
        using var http = new ProtocolHttpClient(context, _handler);
        var api = new LoginApi(context, http, _unixMillis);
        return await api.LoginAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task SaveAsync(ClientContext context, CancellationToken cancellationToken) =>
        _store.SaveAsync(SessionData.FromContext(context, DateTime.UtcNow), cancellationToken);
}