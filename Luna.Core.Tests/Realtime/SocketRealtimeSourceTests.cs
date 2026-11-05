using System.Text.Json;
using Luna.Core.Realtime;
using Luna.Protocol.Messaging;
using Luna.Protocol.Realtime;

namespace Luna.Core.Tests.Realtime;

/// <summary>
/// The pure parts of the thin <see cref="SocketRealtimeSource"/> adapter: the state mapping and the choice of which socket
/// events carry messages. The run loop itself is the already tested <see cref="SocketClient"/>.
/// </summary>
public sealed class SocketRealtimeSourceTests
{
    private static SocketEvent Event(ushort cmd, string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return new SocketEvent(cmd, 0, document.RootElement.Clone());
    }

    private const string UserPayload =
        """{"data":{"msgs":[{"msgId":"9001","cliMsgId":"9002","msgType":"webchat","dName":"Fake","ts":"1700000000123","uidFrom":"fake-peer-uid","idTo":"fake-self-uid","content":"fake text"}]}}""";

    [Theory]
    [InlineData(SocketState.Connecting, LiveSyncState.Connecting)]
    [InlineData(SocketState.Connected, LiveSyncState.Connected)]
    [InlineData(SocketState.Disconnected, LiveSyncState.Disconnected)]
    [InlineData(SocketState.DuplicateConnection, LiveSyncState.DuplicateConnection)]
    public void Every_socket_state_maps_to_one_live_sync_state(SocketState socket, LiveSyncState expected)
    {
        Assert.Equal(expected, SocketRealtimeSource.Map(socket));
    }

    [Fact]
    public void A_user_message_event_yields_its_message()
    {
        var parser = new MessageParser("fake-self-uid");

        IReadOnlyList<ChatMessage> messages = SocketRealtimeSource.ExtractMessages(parser, Event(501, UserPayload));

        ChatMessage message = Assert.Single(messages);
        Assert.Equal("9001", message.MsgId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(502)]
    [InlineData(601)]
    [InlineData(612)]
    public void Events_that_carry_no_message_yield_nothing(int cmd)
    {
        var parser = new MessageParser("fake-self-uid");

        Assert.Empty(SocketRealtimeSource.ExtractMessages(parser, Event((ushort)cmd, UserPayload)));
    }

    [Fact]
    public void A_message_event_without_usable_data_yields_nothing_and_does_not_throw()
    {
        var parser = new MessageParser("fake-self-uid");

        Assert.Empty(SocketRealtimeSource.ExtractMessages(parser, Event(501, """{"data":{"msgs":[42,"text",null]}}""")));
        Assert.Empty(SocketRealtimeSource.ExtractMessages(parser, Event(501, """{"nodata":1}""")));
    }

    [Fact]
    public void ToString_prints_the_type_name_only()
    {
        var source = new SocketRealtimeSource(
            new SocketClient(new Luna.Protocol.ClientContext("fake-imei", "FakeAgent/1.0"), ["wss://fake.zalo.me"]),
            "fake-self-uid");

        Assert.Equal(nameof(SocketRealtimeSource), source.ToString());
    }
}
