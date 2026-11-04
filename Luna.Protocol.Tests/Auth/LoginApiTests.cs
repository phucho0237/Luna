using System.Text.Json;
using System.Web;
using Xunit;
using Luna.Protocol.Auth;
using Luna.Protocol.Crypto;
using Luna.Protocol.Http;
using Luna.Protocol.Tests.Support;

namespace Luna.Protocol.Tests.Auth;

public class LoginApiTests
{
    private const string Imei = "11111111-2222-3333-4444-555555555555-0123456789abcdef0123456789abcdef";
    private const long FixedClock = 1761900000000;
    private const string Secret = "SECRET-ENK-VALUE";

    private static ClientContext NewContext() => new(Imei, "TestAgent/1.0");

    [Fact]
    public async Task GetServerInfo_sends_signed_query_without_api_version_params()
    {
        var handler = new FakeHandler(_ => "{\"error_code\":0,\"data\":{\"hello\":\"world\"}}");
        ClientContext context = NewContext();
        using var http = new ProtocolHttpClient(context, handler);
        var api = new LoginApi(context, http, () => FixedClock);

        JsonElement info = await api.GetServerInfoAsync();

        Assert.Equal("world", info.GetProperty("hello").GetString());

        Uri uri = handler.Requests[0];
        Assert.Equal("wpa.chat.zalo.me", uri.Host);
        Assert.Equal("/api/login/getServerInfo", uri.AbsolutePath);

        var query = HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal(Imei, query["imei"]);
        Assert.Equal("30", query["type"]);
        Assert.Equal("694", query["client_version"]);
        Assert.Equal("Web", query["computer_name"]);
        Assert.Matches("^[0-9a-f]{32}$", query["signkey"] ?? "");
        Assert.Null(query["zpw_ver"]);
    }

    [Fact]
    public async Task GetServerInfo_throws_when_data_is_missing()
    {
        var handler = new FakeHandler(_ => "{\"error_code\":114,\"error_message\":\"nope\"}");
        ClientContext context = NewContext();
        using var http = new ProtocolHttpClient(context, handler);
        var api = new LoginApi(context, http, () => FixedClock);

        var ex = await Assert.ThrowsAsync<ApiException>(() => api.GetServerInfoAsync());

        Assert.Equal(114, ex.ErrorCode);
    }

    [Fact]
    public async Task Login_encrypts_params_and_parses_decrypted_reply()
    {
        string reply =
            "{\"error_code\":0,\"error_message\":\"Success\",\"data\":{" +
            "\"uid\":\"123456789\",\"zpw_enk\":\"" + Secret + "\"," +
            "\"zpw_service_map_v3\":{" +
            "\"chat\":[\"https://tt-chat3-wpa.chat.zalo.me\"]," +
            "\"group\":[\"https://tt-group-wpa.chat.zalo.me\"]}," +
            "\"zpw_ws\":[\"wss://ws1.chat.zalo.me\"]}}";

        string? decryptedRequest = null;
        var handler = new FakeHandler(request =>
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var encryptor = new ParamsEncryptor("30", Imei, FixedClock, query["zcid_ext"]);
            decryptedRequest = encryptor.DecryptResponse(query["params"] ?? "");
            return "{\"error_code\":0,\"error_message\":\"Success\",\"data\":\""
                + encryptor.EncryptPayload(reply) + "\"}";
        });

        ClientContext context = NewContext();
        using var http = new ProtocolHttpClient(context, handler);
        var api = new LoginApi(context, http, () => FixedClock);

        LoginInfo info = await api.LoginAsync();

        Assert.Equal("123456789", info.Uid);
        Assert.Equal(Secret, info.ZpwEnk);
        Assert.Equal("https://tt-chat3-wpa.chat.zalo.me", info.ServiceMap["chat"][0]);
        Assert.Equal("https://tt-group-wpa.chat.zalo.me", info.ServiceMap["group"][0]);
        Assert.Equal("wss://ws1.chat.zalo.me", info.WebSocketUrls[0]);
        Assert.DoesNotContain(Secret, info.ToString());

        // The outgoing payload, after decryption, must contain the imei and the language.
        Assert.Contains(Imei, decryptedRequest ?? "");
        Assert.Contains("\"language\":\"vi\"", decryptedRequest ?? "");

        // URL and signature.
        Uri uri = handler.Requests[0];
        Assert.Equal("/api/login/getLoginInfo", uri.AbsolutePath);
        var q = HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("694", q["zpw_ver"]);
        Assert.Equal("30", q["zpw_type"]);
        Assert.Equal("0", q["nretry"]);
        Assert.Equal("v2", q["enc_ver"]);

        // signkey signs only 6 fields, not nretry, zpw_ver or zpw_type.
        var signed = new Dictionary<string, string>
        {
            ["zcid"] = q["zcid"] ?? "",
            ["zcid_ext"] = q["zcid_ext"] ?? "",
            ["enc_ver"] = q["enc_ver"] ?? "",
            ["params"] = q["params"] ?? "",
            ["type"] = q["type"] ?? "",
            ["client_version"] = q["client_version"] ?? "",
        };
        Assert.Equal(SignKey.Compute("getlogininfo", signed), q["signkey"]);
    }

    [Fact]
    public async Task Login_throws_when_decrypted_reply_is_an_error()
    {
        var handler = new FakeHandler(request =>
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var encryptor = new ParamsEncryptor("30", Imei, FixedClock, query["zcid_ext"]);
            string inner = "{\"error_code\":18022,\"error_message\":\"Invalid Signature\"}";
            return "{\"error_code\":0,\"data\":\"" + encryptor.EncryptPayload(inner) + "\"}";
        });

        ClientContext context = NewContext();
        using var http = new ProtocolHttpClient(context, handler);
        var api = new LoginApi(context, http, () => FixedClock);

        var ex = await Assert.ThrowsAsync<ApiException>(() => api.LoginAsync());

        Assert.Equal(ApiException.InvalidSignature, ex.ErrorCode);
        Assert.Equal("Invalid Signature", ex.ServerMessage);
    }
}