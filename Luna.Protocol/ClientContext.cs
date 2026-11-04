using System.Net;
using Luna.Protocol.Crypto;

namespace Luna.Protocol;

/// <summary>Identity of a client "device" and the session cookie store.</summary>
public sealed class ClientContext
{
    private static readonly string[] CookieDomains = [".zalo.me", ".zaloapp.com"];

    public ClientContext(string imei, string userAgent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imei);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);
        Imei = imei;
        UserAgent = userAgent;
    }

    public string Imei { get; }

    public string UserAgent { get; }

    public string Language { get; init; } = "vi";

    /// <summary>zpw_type.</summary>
    public string ApiType { get; init; } = "30";

    /// <summary>zpw_ver. This value changes over time; re-check it with the DevTools of chat.zalo.me.</summary>
    public string ApiVersion { get; init; } = "694";

    public CookieContainer Cookies { get; } = new();

    /// <summary>Same format as zca-js: uuid + "-" + md5(userAgent).</summary>
    public static string GenerateImei(string userAgent) =>
        $"{Guid.NewGuid()}-{Md5Hex.Lower(userAgent)}";

    /// <summary>Loads a cookie string like "a=b; c=d" for every service domain.</summary>
    public void AddCookies(string cookieHeader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cookieHeader);

        string[] pairs = cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string pair in pairs)
        {
            int eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            string name = pair[..eq].Trim();
            string value = pair[(eq + 1)..].Trim();
            foreach (string domain in CookieDomains)
            {
                Cookies.Add(new Cookie(name, value, "/", domain));
            }
        }
    }

    /// <summary>Takes a snapshot of all current cookies. It must be encrypted when saved to disk.</summary>
    public IReadOnlyList<CookieSnapshot> ExportCookies()
    {
        var result = new List<CookieSnapshot>();
        foreach (Cookie cookie in Cookies.GetAllCookies())
        {
            DateTime? expires = cookie.Expires == DateTime.MinValue ? null : cookie.Expires.ToUniversalTime();
            result.Add(new CookieSnapshot(
                cookie.Name, cookie.Value, cookie.Domain, cookie.Path, expires, cookie.Secure, cookie.HttpOnly));
        }

        return result;
    }

    public void ImportCookies(IEnumerable<CookieSnapshot> cookies)
    {
        ArgumentNullException.ThrowIfNull(cookies);

        foreach (CookieSnapshot snapshot in cookies)
        {
            if (snapshot.ExpiresUtc is { } expiresUtc && expiresUtc <= DateTime.UtcNow)
            {
                continue;
            }

            var cookie = new Cookie(snapshot.Name, snapshot.Value, snapshot.Path, snapshot.Domain)
            {
                Secure = snapshot.Secure,
                HttpOnly = snapshot.HttpOnly,
            };
            if (snapshot.ExpiresUtc is { } expires)
            {
                cookie.Expires = expires;
            }

            Cookies.Add(cookie);
        }
    }
}