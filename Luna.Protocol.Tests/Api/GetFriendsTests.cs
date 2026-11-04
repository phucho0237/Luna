using System.Text.Json;
using Xunit;
using Luna.Protocol.Api;
using Luna.Protocol.Crypto;
using Luna.Protocol.Tests.Support;

namespace Luna.Protocol.Tests.Api;

public sealed class GetFriendsTests
{
    private const string ExpectedPayload =
        "{\"incInvalid\":1,\"page\":1,\"count\":50,\"avatar_size\":120,\"actiontime\":0,\"imei\":\"11111111-2222-3333-4444-555555555555-0123456789abcdef0123456789abcdef\"}";

    // Vector produced with crypto-js (the library zca-js uses): AES with the fake key FakeEnk for ExpectedPayload.
    private const string ExpectedParams =
        "xbHhJ3ecUEfeHWsYjS9X0t8vX+nU1bNBDRZZBPU2WWi66LfTKJ7fARAt0ymfFn0J0ewEfzzSOlgWZLmfmZLHvaNGg6rh/9Jqrz2xnQvD/1izldmSRQE3Rfni6Lw/92R5P3ypGyZPdTGMTeMTlJYKpchBerFhxZRXmLzAyB3oWhYN5ak1FG2XQW9iUttnowyJ9fNd9oisQKjNg+31Vqlgng==";

    private static (ApiClient Client, RoutingHandler Handler) New()
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.OkEnvelope("[{\"userId\":\"9999\"}]"));
        var client = new ApiClient(
            ApiTestKit.Context(), ApiTestKit.Login("https://profile.zalo.me"), new ApiTestKit.FakeTime().Options(), handler);
        return (client, handler);
    }

    [Fact]
    public async Task Sends_reference_params_vector_with_version_and_no_signkey()
    {
        (ApiClient client, RoutingHandler handler) = New();
        using (client)
        {
            await client.GetFriendsAsync(count: 50, page: 1, avatarSize: 120);
        }

        Uri uri = Assert.Single(handler.Calls).Uri;
        Assert.Equal("https://profile.zalo.me/api/social/friend/getfriends", uri.GetLeftPart(UriPartial.Path));
        Assert.Equal(ExpectedParams, ApiTestKit.ParamsOf(uri));
        Assert.Contains($"params={Uri.EscapeDataString(ExpectedParams)}", uri.Query, StringComparison.Ordinal);
        Assert.Contains("zpw_ver=694", uri.Query, StringComparison.Ordinal);
        Assert.Contains("zpw_type=30", uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("signkey", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Payload_decrypts_to_exact_expected_string()
    {
        (ApiClient client, RoutingHandler handler) = New();
        using (client)
        {
            await client.GetFriendsAsync();
        }

        string sent = ApiTestKit.ParamsOf(Assert.Single(handler.Calls).Uri);
        Assert.Equal(ExpectedPayload, new SessionCipher(ApiTestKit.FakeEnk).Decrypt(sent));
    }

    [Fact]
    public async Task Custom_arguments_appear_in_payload_in_key_order()
    {
        (ApiClient client, RoutingHandler handler) = New();
        using (client)
        {
            await client.GetFriendsAsync(count: 20, page: 3, avatarSize: 240);
        }

        string payload = new SessionCipher(ApiTestKit.FakeEnk).Decrypt(ApiTestKit.ParamsOf(Assert.Single(handler.Calls).Uri));
        Assert.StartsWith("{\"incInvalid\":1,\"page\":3,\"count\":20,\"avatar_size\":240,\"actiontime\":0,\"imei\":", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Imei_is_json_escaped()
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.OkEnvelope());
        using var client = new ApiClient(
            new ClientContext("a\"b\\c", "UA-test"),
            ApiTestKit.Login("https://profile.zalo.me"),
            new ApiTestKit.FakeTime().Options(),
            handler);

        await client.GetFriendsAsync();

        string payload = new SessionCipher(ApiTestKit.FakeEnk).Decrypt(ApiTestKit.ParamsOf(Assert.Single(handler.Calls).Uri));
        using JsonDocument doc = JsonDocument.Parse(payload);
        Assert.Equal("a\"b\\c", doc.RootElement.GetProperty("imei").GetString());
    }

    [Fact]
    public async Task Returns_inner_data_element()
    {
        (ApiClient client, _) = New();
        using (client)
        {
            JsonElement result = await client.GetFriendsAsync();

            Assert.Equal("9999", result[0].GetProperty("userId").GetString());
        }
    }

    [Theory]
    [InlineData(0, 1, 120)]
    [InlineData(50, 0, 120)]
    [InlineData(50, 1, 0)]
    public async Task Rejects_non_positive_arguments(int count, int page, int avatar)
    {
        (ApiClient client, RoutingHandler handler) = New();
        using (client)
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetFriendsAsync(count, page, avatar));
        }

        Assert.Empty(handler.Calls);
    }
}
