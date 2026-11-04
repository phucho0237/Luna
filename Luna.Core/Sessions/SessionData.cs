using Luna.Protocol;

namespace Luna.Core.Sessions;

/// <summary>
/// What must be stored to log in again without scanning a QR code: IMEI, User-Agent and the session cookies.
/// Cookies are the key to the account and must never be written to disk in plain text.
/// </summary>
public sealed record SessionData(
    int Version,
    string Imei,
    string UserAgent,
    IReadOnlyList<CookieSnapshot> Cookies,
    DateTime SavedAtUtc)
{
    public const int CurrentVersion = 1;

    public static SessionData FromContext(ClientContext context, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new SessionData(CurrentVersion, context.Imei, context.UserAgent, context.ExportCookies(), nowUtc);
    }

    public ClientContext ToContext()
    {
        var context = new ClientContext(Imei, UserAgent);
        context.ImportCookies(Cookies);
        return context;
    }
}