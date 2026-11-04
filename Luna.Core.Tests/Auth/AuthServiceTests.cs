using System.Web;
using Xunit;
using Luna.Core.Auth;
using Luna.Core.Sessions;
using Luna.Core.Tests.Support;
using Luna.Protocol;
using Luna.Protocol.Crypto;

namespace Luna.Core.Tests.Auth;

public class AuthServiceTests
{
    private const string Imei = "imei-123";
    private const long FixedClock = 1761900000000;

    private const string Reply =
        "{\"error_code\":0,\"error_message\":\"Success\",\"data\":{" +
        "\"uid\":\"123456789\",\"zpw_enk\":\"ENK\"," +
        "\"zpw_service_map_v3\":{\"chat\":[\"https://tt-chat3-wpa.chat.zalo.me\"]}," +
        "\"zpw_ws\":[\"wss://ws1.chat.zalo.me\"]}}";

    private static SessionData SavedSession()
    {
        var context = new ClientContext(Imei, "TestAgent/1.0");
        context.AddCookies("zpw_sek=SEKVALUE");
        return SessionData.FromContext(context, DateTime.UtcNow);
    }

    [Fact]
    public async Task Restore_returns_null_when_nothing_is_saved()
    {
        var store = new InMemoryStore(null);
        var service = new AuthService(store, new FakeHandler(_ => throw new InvalidOperationException("The network must not be called.")));

        Assert.Null(await service.RestoreAsync());
    }

    [Fact]
    public async Task Restore_logs_in_with_saved_session_and_refreshes_it()
    {
        var store = new InMemoryStore(SavedSession());
        var handler = new FakeHandler(request =>
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var encryptor = new ParamsEncryptor("30", Imei, FixedClock, query["zcid_ext"]);
            return "{\"error_code\":0,\"data\":\"" + encryptor.EncryptPayload(Reply) + "\"}";
        });
        var service = new AuthService(store, handler, () => FixedClock);

        AuthSession? session = await service.RestoreAsync();

        Assert.NotNull(session);
        Assert.Equal("123456789", session.Info.Uid);
        Assert.Equal(Imei, session.Context.Imei);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task Restore_returns_null_but_keeps_session_when_server_rejects_it()
    {
        var store = new InMemoryStore(SavedSession());
        var handler = new FakeHandler(_ => "{\"error_code\":-100,\"error_message\":\"session expired\"}");
        var service = new AuthService(store, handler, () => FixedClock);

        Assert.Null(await service.RestoreAsync());
        Assert.NotNull(store.Current);
        Assert.Equal(0, store.DeleteCount);
    }

    [Fact]
    public async Task Restore_does_not_swallow_network_errors()
    {
        var store = new InMemoryStore(SavedSession());
        var handler = new FakeHandler(_ => throw new HttpRequestException("offline"));
        var service = new AuthService(store, handler, () => FixedClock);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.RestoreAsync());
    }
}