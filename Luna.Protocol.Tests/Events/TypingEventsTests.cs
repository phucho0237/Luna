using Xunit;
using Luna.Protocol.Events;

namespace Luna.Protocol.Tests.Events;

public class TypingEventsTests
{
    private static RouteResult Route(string json) =>
        new EventRouter().Route(EventRouterTests.Create(602, json));

    [Fact]
    public void Route_Typing_YieldsUserTypingNotice()
    {
        string json = """{"data":{"actions":[{"act_type":"typing","act":"typing","data":"\"uid\":\"84901234567\",\"ts\":\"1700000000000\",\"isPC\":0"}],"more":0}}""";

        RouteResult result = Route(json);

        TypingNotice notice = Assert.IsType<TypingNotice>(Assert.Single(result.Events));
        Assert.False(notice.IsGroup);
        Assert.Equal("84901234567", notice.Uid);
        Assert.Null(notice.GroupId);
        Assert.Equal("1700000000000", notice.Timestamp);
    }

    [Fact]
    public void Route_Gtyping_YieldsGroupTypingNoticeWithGroupId()
    {
        string json = """{"data":{"actions":[{"act_type":"typing","act":"gtyping","data":"\"gid\":\"55500011122\",\"uid\":\"84901234567\",\"ts\":1700000000000"}]}}""";

        TypingNotice notice = Assert.IsType<TypingNotice>(Assert.Single(Route(json).Events));

        Assert.True(notice.IsGroup);
        Assert.Equal("55500011122", notice.GroupId);
        Assert.Equal("1700000000000", notice.Timestamp);
    }

    [Fact]
    public void Route_DataAlreadyWrappedInBraces_StillParses()
    {
        string json = """{"data":{"actions":[{"act_type":"typing","act":"typing","data":"{\"uid\":\"84901234567\"}"}]}}""";

        TypingNotice notice = Assert.IsType<TypingNotice>(Assert.Single(Route(json).Events));

        Assert.Equal("84901234567", notice.Uid);
        Assert.Null(notice.Timestamp);
    }

    [Fact]
    public void Route_BadTypingElements_AreSkipped()
    {
        string json = """{"data":{"actions":[{"act_type":"typing","act":"typing","data":"\"ts\":\"1\""},{"act_type":"typing","act":"typing","data":"khong phai json"},{"act_type":"typing","act":"typing"},{"act_type":"typing","act":"typing","data":"\"uid\":\"1\""}]}}""";

        RouteResult result = Route(json);

        Assert.Single(result.Events.OfType<TypingNotice>());
        Assert.Equal(3, result.Skipped);
        Assert.DoesNotContain(result.Events, e => e is UnknownEvent);
    }

    [Fact]
    public void Route_OtherActionType_LeftToUnknownEvent()
    {
        string json = """{"data":{"actions":[{"act_type":"typing","act":"typing","data":"\"uid\":\"1\""},{"act_type":"khac","act":"x","data":"y"}]}}""";

        RouteResult result = Route(json);

        Assert.Single(result.Events.OfType<TypingNotice>());
        Assert.Single(result.Events.OfType<UnknownEvent>());
    }

    [Fact]
    public void Route_UnknownActForTypingType_LeftToUnknownEvent()
    {
        string json = """{"data":{"actions":[{"act_type":"typing","act":"lạ","data":"x"}]}}""";

        RouteResult result = Route(json);

        Assert.IsType<UnknownEvent>(Assert.Single(result.Events));
    }

    [Fact]
    public void ToString_DoesNotLeakUidOrGid()
    {
        string json = """{"data":{"actions":[{"act_type":"typing","act":"gtyping","data":"\"gid\":\"55500011122\",\"uid\":\"84901234567\""}]}}""";

        string text = Route(json).Events[0].ToString()!;

        Assert.Equal("TypingNotice { IsGroup = True }", text);
        Assert.DoesNotContain("84901234567", text);
        Assert.DoesNotContain("55500011122", text);
    }
}
