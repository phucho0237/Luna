using System.Net;
using Xunit;
using Luna.Protocol.Auth;
using Luna.Protocol.Tests.Support;

namespace Luna.Protocol.Tests.Auth;

public class QrLoginClientTests
{
    private const string ScanOk =
        "{\"error_code\":0,\"data\":{\"display_name\":\"Tester\",\"avatar\":\"https://a/b.png\"}}";

    /// <summary>Fake server with default responses for one successful login.</summary>
    private static RoutingHandler CreateHandler(Func<string, string?> custom) =>
        new((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            string? overridden = custom(path);
            if (overridden is not null)
            {
                return overridden;
            }

            return path switch
            {
                "/account" => "<html><script src=\"https://stc-zlogin.zdn.vn/main-1.2.3.js\"></script></html>",
                "/account/logininfo" => "{\"error_code\":0,\"data\":{}}",
                "/account/verify-client" => "{\"error_code\":0}",
                "/account/authen/qr/generate" =>
                    "{\"error_code\":0,\"data\":{\"code\":\"CODE1\",\"image\":\"data:image/png;base64,iVBORw0KGgo=\"}}",
                "/account/authen/qr/waiting-scan" => ScanOk,
                "/account/authen/qr/waiting-confirm" => "{\"error_code\":0}",
                "/account/checksession" => "",
                "/jr/userinfo" => "{\"error_code\":0,\"data\":{\"logged\":true,\"info\":{\"name\":\"Tester\"}}}",
                _ => throw new InvalidOperationException("Unexpected request: " + request.RequestUri),
            };
        });

    private static ClientContext NewContext() => new("imei", "TestAgent/1.0");

    [Fact]
    public async Task Login_runs_full_flow_and_reports_events()
    {
        int scanCalls = 0;
        RoutingHandler handler = CreateHandler(path =>
            path == "/account/authen/qr/waiting-scan" && ++scanCalls == 1 ? "{\"error_code\":8}" : null);
        using var client = new QrLoginClient(NewContext(), handler);
        var events = new List<QrLoginEvent>();

        QrLoginResult result = await client.LoginAsync(events.Add);

        Assert.Equal("Tester", result.DisplayName);
        Assert.Equal("Tester", result.UserInfo.GetProperty("name").GetString());

        var generated = Assert.IsType<QrGenerated>(events[0]);
        Assert.Equal("CODE1", generated.Code);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, generated.PngImage);

        var scanned = Assert.IsType<QrScanned>(events[1]);
        Assert.Equal("Tester", scanned.DisplayName);
        Assert.Equal("https://a/b.png", scanned.AvatarUrl);

        // The first time the server returns "waiting" (8), so it must ask again.
        Assert.Equal(2, handler.Calls.Count(c => c.Uri.AbsolutePath.EndsWith("waiting-scan", StringComparison.Ordinal)));

        string confirmBody = handler.Calls
            .Single(c => c.Uri.AbsolutePath.EndsWith("waiting-confirm", StringComparison.Ordinal)).Body;
        Assert.Contains("code=CODE1", confirmBody);
        Assert.Contains("gAction=CONFIRM_QR", confirmBody);
        Assert.Contains("v=1.2.3", confirmBody);
    }

    [Fact]
    public async Task Login_retries_when_qr_expires_then_gives_up()
    {
        RoutingHandler handler = CreateHandler(path =>
            path == "/account/authen/qr/waiting-scan" ? "{\"error_code\":8}" : null);
        using var client = new QrLoginClient(NewContext(), handler, qrLifetime: TimeSpan.FromMilliseconds(50));
        var events = new List<QrLoginEvent>();

        await Assert.ThrowsAsync<TimeoutException>(() => client.LoginAsync(events.Add, maxAttempts: 2));

        Assert.Equal(2, events.OfType<QrGenerated>().Count());
        Assert.Equal(2, events.OfType<QrExpired>().Count());
    }

    [Fact]
    public async Task Login_raises_declined_event_and_throws_when_user_rejects()
    {
        RoutingHandler handler = CreateHandler(path =>
            path == "/account/authen/qr/waiting-confirm" ? "{\"error_code\":-13}" : null);
        using var client = new QrLoginClient(NewContext(), handler);
        var events = new List<QrLoginEvent>();

        var ex = await Assert.ThrowsAsync<ApiException>(() => client.LoginAsync(events.Add));

        Assert.Equal(-13, ex.ErrorCode);
        Assert.IsType<QrDeclined>(events[^1]);
    }

    [Fact]
    public async Task Login_fails_when_account_is_not_logged_in_afterwards()
    {
        RoutingHandler handler = CreateHandler(path =>
            path == "/jr/userinfo" ? "{\"error_code\":0,\"data\":{\"logged\":false}}" : null);
        using var client = new QrLoginClient(NewContext(), handler);

        await Assert.ThrowsAsync<ApiException>(() => client.LoginAsync(_ => { }));
    }

    [Fact]
    public async Task Login_follows_session_redirect_and_keeps_cookies_for_other_service_domains()
    {
        RoutingHandler handler = CreateHandler(path => path == "/index.html" ? "" : null);
        handler.Decorate = (request, response) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/account/checksession")
            {
                response.StatusCode = HttpStatusCode.Found;
                response.Headers.Location = new Uri("https://chat.zalo.me/index.html");
                response.Headers.TryAddWithoutValidation(
                    "Set-Cookie", "zpw_sek=SEKVALUE; Domain=chat.zalo.me; Path=/; Secure; HttpOnly");
            }
            else if (path == "/index.html")
            {
                response.Headers.TryAddWithoutValidation("Set-Cookie", "zpsid=SIDVALUE; Domain=.zalo.me; Path=/; Secure");
            }
        };

        ClientContext context = NewContext();
        using var client = new QrLoginClient(context, handler);

        await client.LoginAsync(_ => { });

        // Must follow the redirect to chat.zalo.me.
        Assert.Contains(handler.Calls, c => c.Uri.Host == "chat.zalo.me" && c.Uri.AbsolutePath == "/index.html");

        // The cookie of another domain (set by id.zalo.me) must still be kept and sent to the right place.
        Assert.Contains("zpw_sek=SEKVALUE", context.Cookies.GetCookieHeader(new Uri("https://chat.zalo.me/")));
        Assert.Contains("zpsid=SIDVALUE", context.Cookies.GetCookieHeader(new Uri("https://jr.chat.zalo.me/jr/userinfo")));
    }

    [Fact]
    public async Task Login_ignores_cookies_that_claim_a_non_service_domain()
    {
        RoutingHandler handler = CreateHandler(_ => null);
        handler.Decorate = (request, response) =>
        {
            if (request.RequestUri!.AbsolutePath == "/account/checksession")
            {
                response.Headers.TryAddWithoutValidation("Set-Cookie", "evil=1; Domain=attacker.example; Path=/");
            }
        };

        ClientContext context = NewContext();
        using var client = new QrLoginClient(context, handler);

        await client.LoginAsync(_ => { });

        Assert.DoesNotContain("evil=1", context.Cookies.GetCookieHeader(new Uri("https://attacker.example/")));
    }
}