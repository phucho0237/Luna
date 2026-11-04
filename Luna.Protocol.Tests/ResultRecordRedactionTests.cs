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
        using JsonDocument document = JsonDocument.Parse($$$"""{"uid":"{{{FakeUid}}}"}""");
        var events = new List<ChatEvent>
        {
            new UnknownEvent(999, 0, document.RootElement),
            new UnknownEvent(998, 1, document.RootElement),
        };
        var result = new RouteResult(events, 3);
        Assert.Equal(2, result.Events.Count);

        string text = result.ToString();

        Assert.Equal("RouteResult { Count = 2, Skipped = 3 }", text);
        Assert.DoesNotContain(FakeUid, text, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseResult_ToString_prints_counts_only()
    {
        var result = new ParseResult([], 3);

        Assert.Equal("ParseResult { Count = 0, Skipped = 3 }", result.ToString());
    }
}
