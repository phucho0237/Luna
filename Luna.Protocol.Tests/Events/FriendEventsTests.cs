using Xunit;
using Luna.Protocol.Events;

namespace Luna.Protocol.Tests.Events;

public class FriendEventsTests
{
    private static RouteResult Route(string content) =>
        new EventRouter().Route(EventRouterTests.Create(
            601,
            """{"data":{"controls":[{"actionId":"1","controlId":"c","content":""" + content + """}],"lastActionId":"5","more":0}}"""));

    private static string Fr(string act, string dataJson) =>
        $$"""{"act_type":"fr","act":"{{act}}","data":{{dataJson}},"ts":1700000000000}""";

    private static void AssertFallback(RouteResult result)
    {
        ControlEvent control = Assert.IsType<ControlEvent>(Assert.Single(result.Events));
        Assert.Equal(ControlKind.Friend, control.Kind);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public void Route_ReqV2_YieldsRequestSent()
    {
        RouteResult result = Route(Fr("req_v2", """{"toUid":"84901234567","fromUid":"84907654321","src":1,"message":"xin chao"}"""));

        FriendEvent friend = Assert.IsType<FriendEvent>(Assert.Single(result.Events));
        Assert.Equal(FriendEventKind.RequestSent, friend.Kind);
        Assert.Equal("84901234567", friend.ToUid);
        Assert.Equal("84907654321", friend.FromUid);
        Assert.Equal("xin chao", friend.Message);
        Assert.Null(friend.Uid);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public void Route_UndoReq_YieldsRequestUndone()
    {
        RouteResult result = Route(Fr("undo_req", """{"toUid":"84901234567","fromUid":"84907654321"}"""));

        FriendEvent friend = Assert.IsType<FriendEvent>(Assert.Single(result.Events));
        Assert.Equal(FriendEventKind.RequestUndone, friend.Kind);
        Assert.Null(friend.Message);
    }

    [Fact]
    public void Route_Req_DataIsJsonString_YieldsRequestReceived()
    {
        string data = "\"{\\\"uid\\\":84901234567,\\\"src\\\":2,\\\"message\\\":\\\"hi\\\"}\"";

        FriendEvent friend = Assert.IsType<FriendEvent>(Assert.Single(Route(Fr("req", data)).Events));

        Assert.Equal(FriendEventKind.RequestReceived, friend.Kind);
        Assert.Equal("84901234567", friend.Uid);
        Assert.Equal("hi", friend.Message);
    }

    [Fact]
    public void Route_Req_UidAboveLongMaxValue_IsKeptVerbatim()
    {
        string data = "\"{\\\"uid\\\":18446744073709551615,\\\"src\\\":2}\"";

        FriendEvent friend = Assert.IsType<FriendEvent>(Assert.Single(Route(Fr("req", data)).Events));

        Assert.Equal("18446744073709551615", friend.Uid);
    }

    [Theory]
    [InlineData("add", FriendEventKind.Added)]
    [InlineData("remove", FriendEventKind.Removed)]
    public void Route_AddAndRemove_PlainStringData_YieldsFriendEvent(string act, FriendEventKind kind)
    {
        FriendEvent friend = Assert.IsType<FriendEvent>(Assert.Single(Route(Fr(act, "\"84901234567\"")).Events));

        Assert.Equal(kind, friend.Kind);
        Assert.Null(friend.Uid);
    }

    [Theory]
    [InlineData("req_v2")]
    [InlineData("undo_req")]
    [InlineData("req")]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("khac")]
    public void Route_OddData_FallsBackToControlEvent(string act)
    {
        foreach (string data in new[] { "\"\"", "null", "42", "[]", "{}" })
        {
            AssertFallback(Route(Fr(act, data)));
        }

        // add/remove accept a non-empty plain string; the other acts need a JSON object inside the string.
        if (act is not ("add" or "remove"))
        {
            foreach (string data in new[] { "\"[]\"", "\"{}\"", "\"khong phai json\"" })
            {
                AssertFallback(Route(Fr(act, data)));
            }
        }
    }

    [Fact]
    public void Route_MissingData_FallsBackToControlEvent()
    {
        AssertFallback(Route("""{"act_type":"fr","act":"req_v2"}"""));
    }

    [Fact]
    public void Route_LoneSurrogateInDataString_FallsBackWithoutError()
    {
        // JSON "\ud83d" is an invalid UTF-16 string: GetString/Parse must not make the router throw.
        AssertFallback(Route(Fr("req", "\"{\\\"uid\\\":\\\"\\ud83d\\\"}\"")));
        AssertFallback(Route(Fr("req", "\"\\ud83d\"")));
        AssertFallback(Route(Fr("add", "\"\\ud83d\"")));
    }

    [Fact]
    public void Route_DataNestedDeeperThan100Levels_FallsBackWithoutError()
    {
        string deep = new string('[', 150) + new string(']', 150);

        AssertFallback(Route(Fr("req", "\"" + deep + "\"")));
        AssertFallback(Route(Fr("req_v2", "\"" + deep + "\"")));
    }

    [Fact]
    public void ToString_PrintsOnlyKind()
    {
        RouteResult result = Route(Fr("req_v2", """{"toUid":"84901234567","fromUid":"84907654321","src":1,"message":"bi mat"}"""));
        FriendEvent friend = Assert.IsType<FriendEvent>(Assert.Single(result.Events));
        Assert.Equal("84901234567", friend.ToUid);
        Assert.Equal("84907654321", friend.FromUid);
        Assert.Equal("bi mat", friend.Message);

        string text = friend.ToString();

        Assert.Equal("FriendEvent { Kind = RequestSent }", text);
        Assert.DoesNotContain("84901234567", text);
        Assert.DoesNotContain("84907654321", text);
        Assert.DoesNotContain("bi mat", text);
    }
}
