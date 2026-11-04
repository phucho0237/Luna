using System.Text.Json;
using Luna.Protocol.Events;
using Luna.Protocol.Messaging;
using Luna.Protocol.Realtime;
using Xunit;

namespace Luna.Protocol.Tests;

/// <summary>ToString of the result and frame records must print counts and flags only, never content.</summary>
public class ResultRecordRedactionTests
{
    private const string FakeUid = "9990001112223334445";
    private const string FakePhone = "0999000111";
    private const string FakeReason = "fake-disconnect-reason-token";
    private const string FakeText = "fake-message-text-token";

    // One valid text message plus one non-object item (counted as skipped).
    private static string FakeMessagePayload() =>
        "{\"data\":{\"msgs\":[{\"actionId\":\"1\",\"msgId\":\"2002\",\"cliMsgId\":\"3003\",\"msgType\":\"chat.text\","
        + "\"uidFrom\":\"" + FakeUid + "\",\"idTo\":\"0\",\"ts\":\"1700000000000\",\"content\":\"" + FakeText + "\"},"
        + "\"not-an-object\"]}}";

    [Fact]
    public void SocketFrame_ToString_hides_the_body()
    {
        var frame = new SocketFrame(1, 501, 0, $$$"""{"uid":"{{{FakeUid}}}","phone":"{{{FakePhone}}}"}""");
        Assert.Contains(FakeUid, frame.Body, StringComparison.Ordinal);
        Assert.Contains(FakePhone, frame.Body, StringComparison.Ordinal);

        string text = frame.ToString();

        Assert.Equal("SocketFrame { Version = 1, Cmd = 501, SubCmd = 0 }", text);
        Assert.DoesNotContain(FakeUid, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakePhone, text, StringComparison.Ordinal);
    }

    [Fact]
    public void SocketStateChange_ToString_hides_the_detail()
    {
        var change = new SocketStateChange(SocketState.Disconnected, FakeReason);
        Assert.Equal(FakeReason, change.Detail);

        string text = change.ToString();

        Assert.Equal("SocketStateChange { State = Disconnected }", text);
        Assert.DoesNotContain(FakeReason, text, StringComparison.Ordinal);
    }

    [Fact]
    public void RouteResult_ToString_prints_counts_only()
    {
        SocketEvent socketEvent = Events.EventRouterTests.Create(501, FakeMessagePayload());

        RouteResult result = new EventRouter().Route(socketEvent);

        MessageReceived received = Assert.IsType<MessageReceived>(Assert.Single(result.Events));
        Assert.Equal(FakeText, received.Message.Text);
        Assert.Equal(1, result.Skipped);

        string text = result.ToString();

        Assert.Equal("RouteResult { Count = 1, Skipped = 1 }", text);
        Assert.DoesNotContain(FakeText, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeUid, text, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseResult_ToString_prints_counts_only()
    {
        SocketEvent socketEvent = Events.EventRouterTests.Create(501, FakeMessagePayload());

        ParseResult result = MessageParser.ParseEvent(socketEvent);

        ChatMessage message = Assert.Single(result.Messages);
        Assert.Equal(FakeText, message.Text);
        Assert.Equal(1, result.Skipped);

        string text = result.ToString();

        Assert.Equal("ParseResult { Count = 1, Skipped = 1 }", text);
        Assert.DoesNotContain(FakeText, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeUid, text, StringComparison.Ordinal);
    }
}
