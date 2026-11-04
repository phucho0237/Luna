using Luna.Core.Auth;
using Luna.Core.Sessions;
using Luna.Protocol;
using Luna.Protocol.Auth;

namespace Luna.Core.Tests.Hygiene;

/// <summary>ToString of the session records must never print the IMEI, the user agent, cookies or login info.</summary>
public class CoreRecordRedactionTests
{
    private const string FakeImei = "fake-imei-0000-1111";
    private const string FakeUserAgent = "FakeAgent/9.9 (fake-ua-marker)";
    private const string FakeCookieName = "fake_cookie_name_zz";
    private const string FakeCookieValue = "fake-cookie-value-qq";
    private const string FakeUid = "9990001112223334445";
    private const string FakeSecretKey = "fake-zpw-enk-key";

    [Fact]
    public void SessionData_ToString_hides_imei_user_agent_and_cookies()
    {
        var cookies = new List<CookieSnapshot>
        {
            new(FakeCookieName, FakeCookieValue, ".zalo.me", "/", null, true, true),
        };
        var data = new SessionData(SessionData.CurrentVersion, FakeImei, FakeUserAgent, cookies, DateTime.UtcNow);
        Assert.Equal(FakeImei, data.Imei);
        Assert.Equal(FakeUserAgent, data.UserAgent);
        Assert.Equal(FakeCookieValue, data.Cookies[0].Value);

        string text = data.ToString();

        Assert.Equal(nameof(SessionData), text);
        Assert.DoesNotContain(FakeImei, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeUserAgent, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeCookieName, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeCookieValue, text, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthSession_ToString_hides_context_and_login_info()
    {
        var context = new ClientContext(FakeImei, FakeUserAgent);
        context.AddCookies($"{FakeCookieName}={FakeCookieValue}");
        var info = new LoginInfo(
            FakeUid,
            FakeSecretKey,
            new Dictionary<string, IReadOnlyList<string>>(),
            ["wss://ws.chat.zalo.me"]);
        var session = new AuthSession(context, info);
        Assert.Equal(FakeImei, session.Context.Imei);
        Assert.Equal(FakeUid, session.Info.Uid);
        Assert.Equal(FakeSecretKey, session.Info.ZpwEnk);

        string text = session.ToString();

        Assert.Equal(nameof(AuthSession), text);
        Assert.DoesNotContain(FakeImei, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeCookieValue, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeUid, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeSecretKey, text, StringComparison.Ordinal);
    }
}
