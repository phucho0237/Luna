using Xunit;
using Luna.Protocol.Api;
using Luna.Protocol.Tests.Support;

namespace Luna.Protocol.Tests.Api;

public sealed class HostNormalizationTests
{
    [Theory]
    [InlineData("https://profile.zalo.me/x")]
    [InlineData("https://profile.zalo.me?x=1")]
    [InlineData("https://profile.zalo.me#f")]
    [InlineData("https://profile.zalo.me:8443")]
    [InlineData("https://1.2.3.4")]
    [InlineData("https://[::1]")]
    [InlineData("https://profile.zalo.me.")]
    public async Task Malformed_host_is_rejected_before_any_request(string host)
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.OkEnvelope());
        using var client = new ApiClient(
            ApiTestKit.Context(), ApiTestKit.Login(host), new ApiTestKit.FakeTime().Options(), handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetEncryptedAsync("profile", "/p", "{}"));

        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData("HTTPS://PROFILE.ZALO.ME")]
    [InlineData("https://profile.zalo.me/")]
    [InlineData("https://profile.zalo.me:443")]
    [InlineData("HTTPS://PROFILE.ZALO.ME/")]
    public async Task Accepted_host_is_sent_as_lowercase_https_host_plus_path(string host)
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.OkEnvelope());
        using var client = new ApiClient(
            ApiTestKit.Context(), ApiTestKit.Login(host), new ApiTestKit.FakeTime().Options(), handler);

        await client.GetEncryptedAsync("profile", "/p", "{}");

        Uri sent = Assert.Single(handler.Calls).Uri;
        Assert.Equal("https://profile.zalo.me/p", sent.GetLeftPart(UriPartial.Path));
    }
}
