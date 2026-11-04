using Xunit;
using Luna.Protocol.Events;

namespace Luna.Protocol.Tests.Events;

public class RouterRobustnessTests
{
    [Theory]
    [InlineData(502, """{"data":{"delivereds":{"msgId":"1"}}}""")]
    [InlineData(504, """{"data":{"clearUnreads":"x"}}""")]
    [InlineData(601, """{"data":{"controls":{"content":{"act_type":"fr"}}}}""")]
    [InlineData(612, """{"data":{"reacts":[],"reactGroups":"x"}}""")]
    [InlineData(602, """{"data":{"actions":{"act_type":"typing"}}}""")]
    public void Route_HandledKeyThatIsNotAnArray_YieldsUnknownEvent(int cmd, string json)
    {
        RouteResult result = new EventRouter().Route(EventRouterTests.Create((ushort)cmd, json));

        UnknownEvent unknown = Assert.IsType<UnknownEvent>(Assert.Single(result.Events));
        Assert.Equal(cmd, unknown.Cmd);
    }

    [Fact]
    public void Route_NullArray_IsTreatedAsEmpty()
    {
        RouteResult result = new EventRouter().Route(
            EventRouterTests.Create(502, """{"data":{"delivereds":null}}"""));

        Assert.Empty(result.Events);
    }

    [Fact]
    public void Trace_NeverContainsUidsOrContent()
    {
        var traces = new List<string>();
        var router = new EventRouter("84900000000", traces.Add);
        (ushort Cmd, string Json)[] inputs =
        [
            (502, """{"data":{"delivereds":[{"deliveredUids":["84901234567"]},"84901234567"]}}"""),
            (504, """{"data":{"clearUnreads":[{"idTo":"84901234567"}]}}"""),
            (612, """{"data":{"reacts":[{"uidFrom":"84901234567","content":"noi-dung-bi-mat"}]}}"""),
            (601, """{"data":{"controls":[{"content":{"act":"84901234567","data":"noi-dung-bi-mat"}}]}}"""),
            (602, """{"data":{"actions":[{"act_type":"typing","act":"typing","data":"noi-dung-bi-mat 84901234567"}]}}"""),
            (501, """{"data":{"msgs":[{"uidFrom":"84901234567","content":"noi-dung-bi-mat"}]}}"""),
        ];

        foreach ((ushort cmd, string json) in inputs)
        {
            router.Route(EventRouterTests.Create(cmd, json));
        }

        Assert.NotEmpty(traces);
        foreach (string line in traces)
        {
            Assert.DoesNotContain("84901234567", line);
            Assert.DoesNotContain("noi-dung-bi-mat", line);
        }
    }
}
