using Xunit;
using Luna.Protocol.Realtime;

namespace Luna.Protocol.Tests.Realtime;

public class SocketClientTests
{
    [Fact]
    public void BuildUrl_adds_slash_when_endpoint_has_only_a_host()
    {
        Assert.Equal(
            "wss://ws1.chat.zalo.me/?zpw_ver=694&zpw_type=30&t=123",
            SocketClient.BuildUrl("wss://ws1.chat.zalo.me", "694", "30", 123));
    }

    [Fact]
    public void BuildUrl_keeps_an_existing_path()
    {
        Assert.Equal(
            "wss://h.example/path?zpw_ver=694&zpw_type=30&t=5",
            SocketClient.BuildUrl("wss://h.example/path", "694", "30", 5));
    }

    [Fact]
    public void BuildUrl_merges_with_an_existing_query()
    {
        Assert.Equal(
            "wss://h.example/?a=1&zpw_ver=694&zpw_type=30&t=5",
            SocketClient.BuildUrl("wss://h.example?a=1", "694", "30", 5));
    }

    [Fact]
    public void Constructor_requires_at_least_one_endpoint()
    {
        var context = new ClientContext("imei", "ua");

        Assert.Throws<ArgumentException>(() => new SocketClient(context, []));
    }
}