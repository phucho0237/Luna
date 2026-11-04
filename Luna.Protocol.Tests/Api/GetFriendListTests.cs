using Xunit;
using Luna.Protocol.Api;
using Luna.Protocol.Crypto;
using Luna.Protocol.Tests.Support;

namespace Luna.Protocol.Tests.Api;

public sealed class GetFriendListTests
{
    private const string ExpectedPayload =
        "{\"incInvalid\":1,\"page\":1,\"count\":20000,\"avatar_size\":120,\"actiontime\":0,\"imei\":\"11111111-2222-3333-4444-555555555555-0123456789abcdef0123456789abcdef\"}";

    // Vector produced with crypto-js (the library zca-js uses): AES with the fake key FakeEnk for ExpectedPayload.
    private const string ExpectedParams =
        "xbHhJ3ecUEfeHWsYjS9X0t8vX+nU1bNBDRZZBPU2WWhOPsW6qsPXK2+cTveVdVWJslfMIWATW/O50v9HULhtjIhx4bRFj58Kf5yqKsND5LRGsSjpyWfrGuDXNF/jFWrVBN0vF+SG+1UYL+ohfud0qg/1cA3/R6YonPnCV2a/S7rx3FFj1XvBF+XRaJQ/O6fv7OeSJJh8Dl/f2JoFS4TttQ==";

    private static (ApiClient Client, RoutingHandler Handler) New(string body)
    {
        var handler = new RoutingHandler((_, _) => body);
        var client = new ApiClient(
            ApiTestKit.Context(), ApiTestKit.Login("https://profile.zalo.me"), new ApiTestKit.FakeTime().Options(), handler);
        return (client, handler);
    }

    [Fact]
    public async Task Sends_exactly_one_request_with_reference_params_vector()
    {
        (ApiClient client, RoutingHandler handler) = New(ApiTestKit.OkEnvelope("[{\"userId\":\"9999\"}]"));
        using (client)
        {
            FriendListResult result = await client.GetFriendListAsync();

            Assert.Equal(1, result.Pages);
            Assert.Single(result.Friends);
        }

        Uri uri = Assert.Single(handler.Calls).Uri;
        Assert.Equal("https://profile.zalo.me/api/social/friend/getfriends", uri.GetLeftPart(UriPartial.Path));
        string sent = ApiTestKit.ParamsOf(uri);
        Assert.Equal(ExpectedParams, sent);
        Assert.Equal(ExpectedPayload, new SessionCipher(ApiTestKit.FakeEnk).Decrypt(sent));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"userId\":\"a\"},{\"userId\":\"b\"},{\"userId\":\"c\"}]")]
    public async Task Never_sends_more_than_one_request(string data)
    {
        (ApiClient client, RoutingHandler handler) = New(ApiTestKit.OkEnvelope(data));
        using (client)
        {
            await client.GetFriendListAsync();
        }

        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Malformed_elements_count_as_skipped()
    {
        (ApiClient client, _) = New(ApiTestKit.OkEnvelope("[{\"userId\":\"a\"},{\"name\":\"khong-co-id\"},7]"));
        using (client)
        {
            FriendListResult result = await client.GetFriendListAsync();

            Assert.Single(result.Friends);
            Assert.Equal(2, result.Skipped);
            Assert.Equal(1, result.Pages);
        }
    }

    [Fact]
    public async Task OnElement_receives_every_raw_element_including_malformed_ones_without_extra_requests()
    {
        (ApiClient client, RoutingHandler handler) = New(ApiTestKit.OkEnvelope("[{\"userId\":\"a\"},7,{\"x\":1}]"));
        var kinds = new List<System.Text.Json.JsonValueKind>();
        using (client)
        {
            await client.GetFriendListAsync(e => kinds.Add(e.ValueKind));
        }

        Assert.Equal(3, kinds.Count);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Non_array_data_throws_InvalidData()
    {
        (ApiClient client, _) = New(ApiTestKit.OkEnvelope("{\"x\":1}"));
        using (client)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => client.GetFriendListAsync());
        }
    }

    [Fact]
    public async Task Error_112_on_the_only_request_still_throws()
    {
        (ApiClient client, RoutingHandler handler) = New(
            ApiTestKit.Envelope("{\"error_code\":112,\"error_message\":\"x\"}"));
        using (client)
        {
            var ex = await Assert.ThrowsAsync<ApiException>(() => client.GetFriendListAsync());

            Assert.Equal(112, ex.ErrorCode);
        }

        Assert.Single(handler.Calls);
    }
}
