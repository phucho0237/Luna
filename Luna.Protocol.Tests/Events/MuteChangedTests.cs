using Xunit;
using Luna.Protocol.Events;

namespace Luna.Protocol.Tests.Events;

public class MuteChangedTests
{
    private static RouteResult Route(string content) =>
        new EventRouter().Route(EventRouterTests.Create(
            601,
            """{"data":{"controls":[{"actionId":"1","controlId":"c","content":""" + content + """}],"more":0}}"""));

    private static string Mute(string act, string dataJson) =>
        $$"""{"act_type":"mute","act":"{{act}}","data":{{dataJson}}}""";

    private static void AssertFallback(RouteResult result)
    {
        ControlEvent control = Assert.IsType<ControlEvent>(Assert.Single(result.Events));
        Assert.Equal(ControlKind.Mute, control.Kind);
        Assert.Equal(0, result.Skipped);
    }

    // The real data is a JSON string (PROTOCOL.md): { toId, muteType, duration, startTime, systemTime, advanced }.
    private const string SetData =
        "\"{\\\"toId\\\":\\\"55500011122\\\",\\\"muteType\\\":1,\\\"duration\\\":3600,\\\"startTime\\\":1700000000,\\\"systemTime\\\":1700000001000,\\\"advanced\\\":{\\\"reminder\\\":0}}\"";

    private const string UnsetData =
        "\"{\\\"toId\\\":\\\"55500011122\\\",\\\"muteType\\\":1,\\\"duration\\\":0,\\\"startTime\\\":0,\\\"systemTime\\\":1700000001000,\\\"advanced\\\":null}\"";

    [Fact]
    public void Route_MuteSet_YieldsMutedTrue()
    {
        MuteChanged mute = Assert.IsType<MuteChanged>(Assert.Single(Route(Mute("set", SetData)).Events));

        Assert.True(mute.Muted);
        Assert.Equal("55500011122", mute.ThreadId);
        Assert.Equal("3600", mute.Duration);
        Assert.Equal("1700000000", mute.StartTime);
    }

    [Fact]
    public void Route_MuteUnset_YieldsMutedFalse()
    {
        MuteChanged mute = Assert.IsType<MuteChanged>(Assert.Single(Route(Mute("unset", UnsetData)).Events));

        Assert.False(mute.Muted);
        Assert.Equal("55500011122", mute.ThreadId);
    }

    [Fact]
    public void Route_MuteDataAsObject_AlsoParses()
    {
        MuteChanged mute = Assert.IsType<MuteChanged>(
            Assert.Single(Route(Mute("set", """{"toId":"55500011122","duration":-1}""")).Events));

        Assert.Equal("-1", mute.Duration);
        Assert.Null(mute.StartTime);
    }

    [Theory]
    [InlineData("set")]
    [InlineData("unset")]
    [InlineData("khac")]
    public void Route_OddData_FallsBackToControlEvent(string act)
    {
        foreach (string data in new[] { "\"\"", "null", "42", "[]", "{}", "\"[]\"", "\"{}\"", "\"payload\"", "{\"duration\":1}" })
        {
            AssertFallback(Route(Mute(act, data)));
        }
    }

    [Fact]
    public void Route_UnknownAct_FallsBackEvenWithValidData()
    {
        AssertFallback(Route(Mute("khac", SetData)));
    }

    [Fact]
    public void Route_LoneSurrogateOrDeepNesting_FallsBackWithoutError()
    {
        string deep = new string('[', 150) + new string(']', 150);

        AssertFallback(Route(Mute("set", "\"\\ud83d\"")));
        AssertFallback(Route(Mute("set", "\"{\\\"toId\\\":\\\"\\ud83d\\\"}\"")));
        AssertFallback(Route(Mute("set", "\"" + deep + "\"")));
    }

    [Fact]
    public void ToString_PrintsOnlyMuted()
    {
        MuteChanged mute = Assert.IsType<MuteChanged>(Assert.Single(Route(Mute("set", SetData)).Events));
        Assert.Equal("55500011122", mute.ThreadId);

        string text = mute.ToString();

        Assert.Equal("MuteChanged { Muted = True }", text);
        Assert.DoesNotContain("55500011122", text);
    }
}
