using Xunit;
using Luna.Protocol.Http;
using Luna.Protocol.Tests.Support;

namespace Luna.Protocol.Tests.Http;

public class ProtocolHttpClientTests
{
    private static readonly KeyValuePair<string, string>[] Query =
    [
        new KeyValuePair<string, string>("a", "b+c/d="),
        new KeyValuePair<string, string>("k", "v w"),
    ];

    [Fact]
    public void BuildUrl_escapes_values_and_appends_api_version()
    {
        var context = new ClientContext("imei", "ua");
        using var http = new ProtocolHttpClient(context, new FakeHandler(_ => "{}"));

        string url = http.BuildUrl("https://example.com/api/x", Query, includeApiVersion: true);

        Assert.Equal("https://example.com/api/x?a=b%2Bc%2Fd%3D&k=v%20w&zpw_ver=694&zpw_type=30", url);
    }

    [Fact]
    public void BuildUrl_can_omit_api_version()
    {
        var context = new ClientContext("imei", "ua");
        using var http = new ProtocolHttpClient(context, new FakeHandler(_ => "{}"));

        string url = http.BuildUrl("https://example.com/api/x", Query, includeApiVersion: false);

        Assert.Equal("https://example.com/api/x?a=b%2Bc%2Fd%3D&k=v%20w", url);
    }

    [Fact]
    public async Task PostFormAsync_sends_urlencoded_post_with_headers_and_decodable_fields()
    {
        var context = new ClientContext("imei", "UA-test");
        HttpRequestMessage? seen = null;
        var handler = new RoutingHandler((request, _) =>
        {
            seen = request;
            return "ok-body";
        });
        using var http = new ProtocolHttpClient(context, handler);
        KeyValuePair<string, string>[] fields = [new("params", "a+b/c=&d e")];

        string result = await http.PostFormAsync(
            "https://example.com/api/x", [new("nretry", "0")], fields, includeApiVersion: true);

        Assert.Equal("ok-body", result);
        (HttpMethod method, Uri uri, string body) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("https://example.com/api/x?nretry=0&zpw_ver=694&zpw_type=30", uri.ToString());
        Assert.Equal("application/x-www-form-urlencoded", seen!.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("params=a%2Bb%2Fc%3D%26d+e", body);
        Assert.Equal("https://chat.zalo.me", seen.Headers.GetValues("Origin").Single());
        Assert.Equal("https://chat.zalo.me/", seen.Headers.GetValues("Referer").Single());
        Assert.Equal("UA-test", seen.Headers.UserAgent.ToString());
    }

    [Theory]
    [InlineData(400)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task PostFormAsync_throws_HttpRequestException_with_status_on_failure(int status)
    {
        var context = new ClientContext("imei", "ua");
        var handler = new RoutingHandler((_, _) => "x") { Decorate = (_, r) => r.StatusCode = (System.Net.HttpStatusCode)status };
        using var http = new ProtocolHttpClient(context, handler);

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            http.PostFormAsync("https://example.com/api/x", [], [new("params", "SECRET-BODY")], includeApiVersion: false));

        Assert.Equal(status, (int?)ex.StatusCode);
        // (c) The form body does not leak into the exception.
        Assert.DoesNotContain("SECRET-BODY", ex.ToString());
        Assert.DoesNotContain("SECRET-BODY", ex.Message);
    }

    [Fact]
    public void AddCookies_makes_cookies_available_on_service_hosts()
    {
        var context = new ClientContext("imei", "ua");
        context.AddCookies("zpw_sek=abc123; _zlang=vi");

        string chat = context.Cookies.GetCookieHeader(new Uri("https://wpa.chat.zalo.me/api/login/getLoginInfo"));
        string app = context.Cookies.GetCookieHeader(new Uri("https://tt-profile-wpa.zaloapp.com/x"));

        Assert.Contains("zpw_sek=abc123", chat);
        Assert.Contains("_zlang=vi", chat);
        Assert.Contains("zpw_sek=abc123", app);
    }
}