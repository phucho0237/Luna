using Xunit;
using Luna.Protocol.Events;
using Luna.Protocol.Messaging;

namespace Luna.Protocol.Tests.Events;

public class ReactionEventsTests
{
    private static RouteResult Route(string json) =>
        new EventRouter().Route(EventRouterTests.Create(612, json));

    private const string Item =
        """{"actionId":"1","msgId":"9223372036854775807","cliMsgId":"1700000000001","msgType":1,"uidFrom":"84901234567","idTo":"55500011122","content":"{\"rIcon\":\"/-heart\",\"rType\":5}","ts":"1700000000000","ttl":0}""";

    [Fact]
    public void Route_Reacts_YieldsUserReactionWithRawContent()
    {
        RouteResult result = Route(
            """{"data":{"reacts":[""" + Item + """],"lastActionId":"5","more":0,"queueStatus":{"12345678901":{"ids":["1"],"lastId":"1"}}}}""");

        ReactionReceived reaction = Assert.IsType<ReactionReceived>(Assert.Single(result.Events));
        Assert.Equal(ThreadType.User, reaction.ThreadType);
        Assert.Equal("9223372036854775807", reaction.MsgId);
        Assert.Equal("1700000000001", reaction.CliMsgId);
        Assert.Equal("84901234567", reaction.UidFrom);
        Assert.Equal("55500011122", reaction.IdTo);
        Assert.Equal("""{"rIcon":"/-heart","rType":5}""", reaction.RawContent);
        Assert.Equal("1700000000000", reaction.Timestamp);
    }

    [Fact]
    public void Route_ReactGroups_YieldsGroupReaction()
    {
        RouteResult result = Route($$$"""{"data":{"reactGroups":[{{{Item}}}]}}""");

        ReactionReceived reaction = Assert.IsType<ReactionReceived>(Assert.Single(result.Events));
        Assert.Equal(ThreadType.Group, reaction.ThreadType);
    }

    [Fact]
    public void Route_ReactionsMixedGoodAndBad_CountsSkipped()
    {
        RouteResult result = Route($$$"""{"data":{"reacts":[{{{Item}}},{"msgId":"1"},"x"],"reactGroups":[{"uidFrom":"2"}]}}""");

        Assert.Single(result.Events.OfType<ReactionReceived>());
        Assert.Equal(3, result.Skipped);
        Assert.DoesNotContain(result.Events, e => e is UnknownEvent);
    }

    [Fact]
    public void ToString_DoesNotLeakUidsOrContent()
    {
        string text = Route($$$"""{"data":{"reacts":[{{{Item}}}]}}""").Events[0].ToString()!;

        Assert.Equal("ReactionReceived { ThreadType = User, MsgId = 9223372036854775807 }", text);
        Assert.DoesNotContain("84901234567", text);
        Assert.DoesNotContain("55500011122", text);
        Assert.DoesNotContain("heart", text);
    }
}
