using Xunit;
using Luna.Protocol.Events;
using Luna.Protocol.Messaging;

namespace Luna.Protocol.Tests.Events;

public class MessageEventsTests
{
    private const string FakeUid = "84901234567";
    private const string FakeSelf = "84907654321";

    private static string UserMsg(string msgId, string uidFrom = FakeUid, string content = "\"Nội dung bí mật\"") =>
        "{\"actionId\":\"1\",\"msgId\":\"" + msgId + "\",\"cliMsgId\":\"3003\",\"msgType\":\"chat.text\",\"uidFrom\":\"" + uidFrom
        + "\",\"idTo\":\"0\",\"ts\":\"1700000000000\",\"content\":" + content + "}";

    private static string Msgs(params string[] items) => "{\"data\":{\"msgs\":[" + string.Join(",", items) + "]}}";

    [Fact]
    public void Route_501_YieldsMessageReceivedAndNoUnknownEvent()
    {
        string json = "{\"data\":{\"msgs\":[" + UserMsg("2002") + "],\"lastActionId\":\"9\",\"more\":0,\"queueStatus\":{\"12345678901\":{\"ids\":[\"1\"],\"lastId\":\"1\"}}}}";

        RouteResult result = new EventRouter().Route(EventRouterTests.Create(501, json));

        MessageReceived received = Assert.IsType<MessageReceived>(Assert.Single(result.Events));
        Assert.Equal("2002", received.Message.MsgId);
        Assert.Equal(ThreadType.User, received.Message.ThreadType);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public void Route_521_YieldsGroupMessage()
    {
        string json = """{"data":{"groupMsgs":[{"msgId":"77","cliMsgId":"88","msgType":"webchat","uidFrom":"84901234567","idTo":"55500011122","ts":"1700000000000","content":"hi"}]}}""";

        RouteResult result = new EventRouter().Route(EventRouterTests.Create(521, json));

        MessageReceived received = Assert.IsType<MessageReceived>(Assert.Single(result.Events));
        Assert.Equal(ThreadType.Group, received.Message.ThreadType);
        Assert.Equal("55500011122", received.Message.ThreadId);
    }

    [Fact]
    public void Route_UndoMessage_YieldsMessageUndoneInsteadOfMessageReceived()
    {
        string undo = """{"deleteMsg":"1","globalMsgId":"9223372036854775807","cliMsgId":"3003","srcId":"1","destId":"2"}""";
        string json = Msgs(UserMsg("2002", content: undo));

        RouteResult result = new EventRouter().Route(EventRouterTests.Create(501, json));

        MessageUndone undone = Assert.IsType<MessageUndone>(Assert.Single(result.Events));
        Assert.Equal(ThreadType.User, undone.ThreadType);
        Assert.Equal(FakeUid, undone.ThreadId);
        Assert.Equal("9223372036854775807", undone.GlobalMsgId);
        Assert.Equal("3003", undone.CliMsgId);
    }

    [Fact]
    public void Route_MixedGoodAndBadMessages_KeepsGoodAndCountsSkipped()
    {
        string json = Msgs(UserMsg("1"), "\"not-an-object\"", UserMsg("2"));

        RouteResult result = new EventRouter().Route(EventRouterTests.Create(501, json));

        Assert.Equal(2, result.Events.OfType<MessageReceived>().Count());
        Assert.Equal(1, result.Skipped);
        Assert.DoesNotContain(result.Events, e => e is UnknownEvent);
    }

    [Fact]
    public void Route_SelfUid_SetsIsSelf()
    {
        string json = Msgs(UserMsg("1", uidFrom: FakeSelf), UserMsg("2"));

        RouteResult result = new EventRouter(FakeSelf).Route(EventRouterTests.Create(501, json));

        MessageReceived[] received = result.Events.OfType<MessageReceived>().ToArray();
        Assert.True(received[0].Message.IsSelf);
        Assert.False(received[1].Message.IsSelf);
    }

    [Fact]
    public void Route_MessageWithExtraNonEmptyArray_AddsOneUnknownEvent()
    {
        string json = "{\"data\":{\"msgs\":[" + UserMsg("1") + "],\"seens\":[{\"x\":1}]}}";

        RouteResult result = new EventRouter().Route(EventRouterTests.Create(501, json));

        Assert.Single(result.Events.OfType<MessageReceived>());
        Assert.Single(result.Events.OfType<UnknownEvent>());
    }

    [Fact]
    public void Route_EmptyMsgsArray_YieldsNothing()
    {
        RouteResult result = new EventRouter().Route(EventRouterTests.Create(501, """{"data":{"msgs":[]}}"""));

        Assert.Empty(result.Events);
    }

    [Fact]
    public void ToString_DoesNotLeakUidContentOrThreadId()
    {
        string undo = """{"deleteMsg":"1","globalMsgId":"555","cliMsgId":"666"}""";
        string json = Msgs(UserMsg("2002"), UserMsg("2003", content: undo));

        RouteResult result = new EventRouter().Route(EventRouterTests.Create(501, json));

        foreach (ChatEvent e in result.Events)
        {
            string text = e.ToString()!;
            Assert.DoesNotContain(FakeUid, text);
            Assert.DoesNotContain("Nội dung bí mật", text);
        }

        Assert.Equal("MessageReceived { MsgId = 2002 }", result.Events[0].ToString());
        Assert.Equal("MessageUndone { GlobalMsgId = 555, CliMsgId = 666 }", result.Events[1].ToString());
    }
}
