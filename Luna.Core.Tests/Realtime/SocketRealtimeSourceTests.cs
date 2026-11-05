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
    public void A_default_payload_yields_nothing_and_does_not_throw()
    {
        var parser = new MessageParser("fake-self-uid");

        // default(JsonElement) is the "no payload at all" case: its ValueKind is Undefined.
        Assert.Empty(SocketRealtimeSource.ExtractMessages(parser, new SocketEvent(501, 0, default)));
        Assert.Empty(SocketRealtimeSource.ExtractMessages(parser, new SocketEvent(521, 0, default)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("[{\"data\":{}}]")]
    [InlineData("{\"data\":42}")]
    [InlineData("{\"data\":\"text\"}")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":[1,2]}")]
    [InlineData("{\"data\":{\"msgs\":42}}")]
    [InlineData("{\"data\":{\"msgs\":\"text\"}}")]
    [InlineData("{\"data\":{\"msgs\":{\"a\":1}}}")]
    [InlineData("{\"data\":{\"msgs\":[[1],{\"msgId\":{\"a\":1}}]}}")]
    [InlineData("{\"data\":{\"msgs\":[{\"msgId\":1e999,\"ts\":-1e999,\"content\":[1],\"uidFrom\":{},\"idTo\":[]}]}}")]
    [InlineData("{\"data\":{\"msgs\":[{\"msgId\":\"1\",\"ts\":\"99999999999999999999999999\",\"content\":{\"deleteMsg\":[1],\"globalMsgId\":{}}}]}}")]
    [InlineData("{\"data\":{\"msgs\":[{\"msgId\":\"\ud800\",\"cliMsgId\":\"\udc00\",\"dName\":\"\ud800\",\"content\":\"\ud800\"}]}}")]
    [InlineData("{\"data\":{\"msgs\":[{\"msgId\":\"1\",\"content\":{\"title\":1,\"href\":2,\"params\":{},\"childnumber\":\"x\"},\"quote\":42,\"mentions\":\"x\",\"propertyExt\":[]}]}}")]
    public void Unusable_payloads_never_throw(string json)
    {
        var parser = new MessageParser("fake-self-uid");

        // The result may be empty or hold whatever the parser could read; the point is that nothing is thrown.
        foreach (ushort cmd in new ushort[] { 501, 521 })
        {
            IReadOnlyList<ChatMessage> messages = SocketRealtimeSource.ExtractMessages(parser, Event(cmd, json));
            Assert.NotNull(messages);
        }
    }

    [Fact]
    public void A_valid_event_after_a_bad_one_is_still_parsed()
    {
        var parser = new MessageParser("fake-self-uid");

        Assert.Empty(SocketRealtimeSource.ExtractMessages(parser, Event(501, """{"data":42}""")));
        Assert.Empty(SocketRealtimeSource.ExtractMessages(parser, new SocketEvent(501, 0, default)));
        IReadOnlyList<ChatMessage> messages = SocketRealtimeSource.ExtractMessages(parser, Event(501, UserPayload));

        Assert.Equal("9001", Assert.Single(messages).MsgId);
    }

    [Fact]
    public void A_payload_whose_document_was_disposed_yields_nothing_and_does_not_throw()
    {
        var parser = new MessageParser("fake-self-uid");
        JsonDocument document = JsonDocument.Parse(UserPayload);
        JsonElement dangling = document.RootElement;
        document.Dispose();

        // Reading a dangling element throws ObjectDisposedException, which is none of the exceptions the parser handles.
        Assert.Empty(SocketRealtimeSource.ExtractMessages(parser, new SocketEvent(501, 0, dangling)));
        Assert.Single(SocketRealtimeSource.ExtractMessages(parser, Event(501, UserPayload)));
    }

    public static TheoryData<Exception> ParserFailures() => new()
    {
        new ArgumentException("fake failure"),
        new ArgumentOutOfRangeException("fake"),
        new OverflowException("fake failure"),
        new NullReferenceException("fake failure"),
        new InvalidCastException("fake failure"),
        new IndexOutOfRangeException("fake failure"),
        new ArithmeticException("fake failure"),
        new NotSupportedException("fake failure"),
        new FormatException("fake failure"),
        new Exception("fake failure"),
    };

    [Theory]
    [MemberData(nameof(ParserFailures))]
    public void Any_parser_failure_yields_nothing_and_never_ends_the_connection(Exception failure)
    {
        // The dropped exception could carry message content, so nothing of it may come out either.
        IReadOnlyList<ChatMessage> messages = SocketRealtimeSource.ExtractMessages(
            Event(501, UserPayload), _ => throw failure);

        Assert.Empty(messages);
    }

    [Fact]
    public void A_cancellation_is_not_swallowed()
    {
        Assert.Throws<OperationCanceledException>(() => SocketRealtimeSource.ExtractMessages(
            Event(501, UserPayload), _ => throw new OperationCanceledException()));
    }

    [Fact]
    public void The_guard_does_not_call_the_parser_for_events_without_messages()
    {
        int calls = 0;

        IReadOnlyList<ChatMessage> messages = SocketRealtimeSource.ExtractMessages(
            Event(502, UserPayload),
            _ =>
            {
                calls++;
                return [];
            });

        Assert.Empty(messages);
        Assert.Equal(0, calls);
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
