using Xunit;
using Luna.Protocol.Events;

namespace Luna.Protocol.Tests.Events;

public class PinnedConversationsTests
{
    private static RouteResult Route(string content) =>
        new EventRouter().Route(EventRouterTests.Create(
            601,
            """{"data":{"controls":[{"actionId":"1","controlId":"c","content":""" + content + """}],"more":0}}"""));

    private static string Pinned(string dataJson, string act = "update") =>
        $$"""{"act_type":"pinned_convers","act":"{{act}}","data":{{dataJson}}}""";

    private static void AssertFallback(RouteResult result)
    {
        ControlEvent control = Assert.IsType<ControlEvent>(Assert.Single(result.Events));
        Assert.Equal(ControlKind.PinnedConversation, control.Kind);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public void Route_Update_YieldsPinnedConversationsChanged()
    {
        RouteResult result = Route(Pinned("""{"tab":0,"action":1,"pinIds":["55500011122","84901234567"],"ts":1700000000000}"""));

        PinnedConversationsChanged pinned = Assert.IsType<PinnedConversationsChanged>(Assert.Single(result.Events));
        Assert.Equal(0, pinned.Tab);
        Assert.Equal(1, pinned.Action);
        Assert.Equal(["55500011122", "84901234567"], pinned.PinIds);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public void Route_EmptyPinIds_IsStillTyped()
    {
        PinnedConversationsChanged pinned = Assert.IsType<PinnedConversationsChanged>(
            Assert.Single(Route(Pinned("""{"tab":1,"action":0,"pinIds":[]}""")).Events));

        Assert.Empty(pinned.PinIds);
    }

    [Fact]
    public void Route_DataAsJsonString_AlsoParses()
    {
        string data = "\"{\\\"tab\\\":0,\\\"action\\\":2,\\\"pinIds\\\":[\\\"55500011122\\\"]}\"";

        PinnedConversationsChanged pinned = Assert.IsType<PinnedConversationsChanged>(Assert.Single(Route(Pinned(data)).Events));

        Assert.Equal(2, pinned.Action);
        Assert.Single(pinned.PinIds);
    }

    [Fact]
    public void Route_OddData_FallsBackToControlEvent()
    {
        foreach (string data in new[]
        {
            "\"\"", "null", "42", "[]", "{}", "\"[]\"", "\"{}\"", "\"payload\"",
            """{"tab":0,"action":1}""", """{"tab":0,"pinIds":[]}""", """{"action":1,"pinIds":[]}""",
            """{"tab":0,"action":1,"pinIds":"x"}""", """{"tab":0,"action":1,"pinIds":[{"a":1}]}""",
        })
        {
            AssertFallback(Route(Pinned(data)));
        }
    }

    [Fact]
    public void Route_UnknownAct_FallsBack()
    {
        AssertFallback(Route(Pinned("""{"tab":0,"action":1,"pinIds":[]}""", "khac")));
    }

    [Fact]
    public void Route_LoneSurrogateOrDeepNesting_FallsBackWithoutError()
    {
        string deep = new string('[', 150) + new string(']', 150);

        AssertFallback(Route(Pinned("\"\\ud83d\"")));
        AssertFallback(Route(Pinned("\"" + deep + "\"")));
    }

    [Fact]
    public void ToString_PrintsOnlyPinCount()
    {
        PinnedConversationsChanged pinned = Assert.IsType<PinnedConversationsChanged>(
            Assert.Single(Route(Pinned("""{"tab":0,"action":1,"pinIds":["55500011122","84901234567"]}""")).Events));
        Assert.Contains("55500011122", pinned.PinIds);

        string text = pinned.ToString();

        Assert.Equal("PinnedConversationsChanged { PinCount = 2 }", text);
        Assert.DoesNotContain("55500011122", text);
        Assert.DoesNotContain("84901234567", text);
    }

    [Fact]
    public void Route_ContactTabClicked_StaysControlEventWithContactTabKind()
    {
        RouteResult result = Route("""{"act_type":"contact_tab","act":"clicked","ts":1,"data":{"ts_clicked":1700000000000}}""");

        ControlEvent control = Assert.IsType<ControlEvent>(Assert.Single(result.Events));
        Assert.Equal(ControlKind.ContactTab, control.Kind);
        Assert.Equal("clicked", control.Act);
        Assert.Equal(0, result.Skipped);
    }
}
