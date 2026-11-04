using Xunit;
using Luna.Protocol.Events;

namespace Luna.Protocol.Tests.Events;

public class ReceiptEventsTests
{
    private static RouteResult Route(ushort cmd, string json) =>
        new EventRouter().Route(EventRouterTests.Create(cmd, json));

    [Fact]
    public void Route_502_YieldsUserDeliveryReceipt()
    {
        string json = """{"data":{"delivereds":[{"msgId":"9223372036854775807","seen":1,"deliveredUids":["84901234567"],"seenUids":["84907654321"],"realMsgId":"4004","mSTs":1700000000000}],"more":0}}""";

        RouteResult result = Route(502, json);

        DeliveryReceipt receipt = Assert.IsType<DeliveryReceipt>(Assert.Single(result.Events));
        Assert.False(receipt.IsGroup);
        Assert.Equal("9223372036854775807", receipt.MsgId);
        Assert.Equal("4004", receipt.RealMsgId);
        Assert.Null(receipt.GroupId);
        Assert.Equal(1, receipt.Seen);
        Assert.Equal(["84901234567"], receipt.DeliveredUids);
        Assert.Equal(["84907654321"], receipt.SeenUids);
    }

    [Fact]
    public void Route_522_YieldsGroupDeliveryReceiptWithGroupId()
    {
        string json = """{"data":{"delivereds":[{"msgId":"77","seen":0,"deliveredUids":["84901234567"],"seenUids":[],"realMsgId":"78","groupId":"55500011122"}]}}""";

        RouteResult result = Route(522, json);

        DeliveryReceipt receipt = Assert.IsType<DeliveryReceipt>(Assert.Single(result.Events));
        Assert.True(receipt.IsGroup);
        Assert.Equal("55500011122", receipt.GroupId);
        Assert.Empty(receipt.SeenUids);
    }

    [Fact]
    public void Route_522WithDeliveredAndUnknownNonEmptyArray_YieldsReceiptAndExactlyOneUnknownEvent()
    {
        string json = """{"data":{"delivereds":[{"msgId":"77","groupId":"55500011122"}],"extra":[{"x":1}]}}""";

        RouteResult result = Route(522, json);

        Assert.Single(result.Events.OfType<DeliveryReceipt>());
        Assert.Single(result.Events.OfType<UnknownEvent>());
    }

    [Fact]
    public void Route_502Seens_YieldsUserSeenReceipt()
    {
        string json = """{"data":{"delivereds":[],"seens":[{"idTo":"84901234567","msgId":"9223372036854775807","realMsgId":"4004"}],"more":0}}""";

        RouteResult result = Route(502, json);

        SeenReceipt seen = Assert.IsType<SeenReceipt>(Assert.Single(result.Events));
        Assert.False(seen.IsGroup);
        Assert.Equal("9223372036854775807", seen.MsgId);
        Assert.Equal("4004", seen.RealMsgId);
        Assert.Equal("84901234567", seen.ThreadId);
        Assert.Empty(seen.SeenUids);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public void Route_522GroupSeens_YieldsGroupSeenReceiptWithUids()
    {
        string json = """{"data":{"delivereds":[],"groupSeens":[{"msgId":"77","groupId":"55500011122","seenUids":["84907654321","84901112223"]}]}}""";

        RouteResult result = Route(522, json);

        SeenReceipt seen = Assert.IsType<SeenReceipt>(Assert.Single(result.Events));
        Assert.True(seen.IsGroup);
        Assert.Equal("55500011122", seen.ThreadId);
        Assert.Equal(["84907654321", "84901112223"], seen.SeenUids);
        Assert.Null(seen.RealMsgId);
    }

    [Fact]
    public void Route_SeensMixedGoodAndBad_CountsSkippedWithoutUnknown()
    {
        string json = """{"data":{"seens":[{"x":1},"text",{"idTo":"1","msgId":"2"}]}}""";

        RouteResult result = Route(502, json);

        Assert.Single(result.Events.OfType<SeenReceipt>());
        Assert.Equal(2, result.Skipped);
        Assert.DoesNotContain(result.Events, e => e is UnknownEvent);
    }

    [Fact]
    public void Route_502WithGroupSeens_StaysUnknown()
    {
        string json = """{"data":{"seens":[],"groupSeens":[{"msgId":"1","groupId":"2"}]}}""";

        RouteResult result = Route(502, json);

        Assert.Single(result.Events.OfType<UnknownEvent>());
    }

    [Fact]
    public void SeenReceipt_ToString_PrintsOnlyKindAndMsgId()
    {
        string json = """{"data":{"groupSeens":[{"msgId":"77","groupId":"55500011122","seenUids":["84907654321"]}]}}""";

        string text = Route(522, json).Events[0].ToString()!;

        Assert.Equal("SeenReceipt { IsGroup = True, MsgId = 77 }", text);
        Assert.DoesNotContain("55500011122", text);
        Assert.DoesNotContain("84907654321", text);
    }

    [Fact]
    public void Route_DeliveredsMixedGoodAndBad_CountsSkipped()
    {
        string json = """{"data":{"delivereds":[{"msgId":"1"},{"seen":1},"text",{"msgId":"2"}]}}""";

        RouteResult result = Route(502, json);

        Assert.Equal(2, result.Events.OfType<DeliveryReceipt>().Count());
        Assert.Equal(2, result.Skipped);
        Assert.DoesNotContain(result.Events, e => e is UnknownEvent);
    }

    [Fact]
    public void Route_504_YieldsUnreadCleared()
    {
        string json = """{"data":{"clearUnreads":[{"idTo":"84901234567","lastMsgId":"100","isGroup":0,"type":0,"ts":"1700000000000"}],"more":0}}""";

        RouteResult result = Route(504, json);

        UnreadCleared cleared = Assert.IsType<UnreadCleared>(Assert.Single(result.Events));
        Assert.Equal("84901234567", cleared.IdTo);
        Assert.Equal("100", cleared.LastMsgId);
        Assert.Null(cleared.LastCliMsgId);
        Assert.False(cleared.IsGroup);
        Assert.Equal("1700000000000", cleared.Timestamp);
    }

    [Fact]
    public void Route_613WithLastCliMsgIdAndGroup_YieldsUnreadCleared()
    {
        string json = """{"data":{"clearUnreads":[{"idTo":"55500011122","lastMsgId":"100","lastCliMsgId":"200","isGroup":1,"type":1,"ts":"1700000000000","sct":3}],"lastActionId":"5","queueStatus":{"12345678901":{"ids":["1"],"lastId":"1"}}}}""";

        RouteResult result = Route(613, json);

        UnreadCleared cleared = Assert.IsType<UnreadCleared>(Assert.Single(result.Events));
        Assert.Equal("200", cleared.LastCliMsgId);
        Assert.True(cleared.IsGroup);
    }

    [Fact]
    public void Route_524WithoutIsGroup_FallsBackToGroup()
    {
        string json = """{"data":{"clearUnreads":[{"idTo":"1","lastMsgId":"2","ts":"3"}]}}""";

        UnreadCleared cleared = Assert.IsType<UnreadCleared>(Assert.Single(Route(524, json).Events));

        Assert.True(cleared.IsGroup);
    }

    [Fact]
    public void Route_ClearUnreadsMissingFieldsOrWrongType_AreSkipped()
    {
        string json = """{"data":{"clearUnreads":[{"idTo":"1","ts":"3"},[],{"idTo":"1","lastMsgId":"2","ts":"3"}]}}""";

        RouteResult result = Route(504, json);

        Assert.Single(result.Events.OfType<UnreadCleared>());
        Assert.Equal(2, result.Skipped);
    }

    [Fact]
    public void Route_EmptyArrays_YieldNothing()
    {
        Assert.Empty(Route(502, """{"data":{"delivereds":[],"more":0}}""").Events);
        Assert.Empty(Route(504, """{"data":{"clearUnreads":[]}}""").Events);
    }

    [Fact]
    public void ToString_DoesNotLeakUidsOrThreadIds()
    {
        RouteResult delivery = Route(522, """{"data":{"delivereds":[{"msgId":"77","deliveredUids":["84901234567"],"seenUids":["84907654321"],"groupId":"55500011122"}]}}""");
        RouteResult cleared = Route(504, """{"data":{"clearUnreads":[{"idTo":"84901234567","lastMsgId":"100","ts":"1"}]}}""");

        string deliveryText = delivery.Events[0].ToString()!;
        string clearedText = cleared.Events[0].ToString()!;

        Assert.Equal("DeliveryReceipt { IsGroup = True, MsgId = 77 }", deliveryText);
        Assert.Equal("UnreadCleared { IsGroup = False, LastMsgId = 100 }", clearedText);
        foreach (string secret in new[] { "84901234567", "84907654321", "55500011122" })
        {
            Assert.DoesNotContain(secret, deliveryText + clearedText);
        }
    }
}
