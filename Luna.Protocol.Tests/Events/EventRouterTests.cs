using System.Text.Json;
using Xunit;
using Luna.Protocol.Events;
using Luna.Protocol.Realtime;

namespace Luna.Protocol.Tests.Events;

public class EventRouterTests
{
    internal static SocketEvent Create(ushort cmd, string json, byte subCmd = 0)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return new SocketEvent(cmd, subCmd, doc.RootElement.Clone());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Route_Cmd1And2_AreIgnored(ushort cmd)
    {
        RouteResult result = new EventRouter().Route(Create(cmd, """{"data":{"key":"x"}}"""));

        Assert.Empty(result.Events);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public void Route_UnsupportedCmd_YieldsExactlyOneUnknownEvent()
    {
        string json = """{"data":{"foo":[{"a":1}],"bar":[{"b":2}]}}""";

        RouteResult result = new EventRouter().Route(Create(999, json, 3));

        UnknownEvent unknown = Assert.IsType<UnknownEvent>(Assert.Single(result.Events));
        Assert.Equal((ushort)999, unknown.Cmd);
        Assert.Equal((byte)3, unknown.SubCmd);
        Assert.Equal(JsonValueKind.Object, unknown.Payload.ValueKind);
    }

    [Fact]
    public void Route_Cmd621_GoesToUnknownEvent()
    {
        string json = """{"data":{"error_message":"x","data":"y","error_code":0}}""";

        RouteResult result = new EventRouter().Route(Create(621, json));

        UnknownEvent unknown = Assert.IsType<UnknownEvent>(Assert.Single(result.Events));
        Assert.Equal((ushort)621, unknown.Cmd);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    [InlineData("""{"other":1}""")]
    [InlineData("""{"data":"not-object"}""")]
    public void Route_PayloadWithoutDataObject_YieldsOneUnknownEvent(string json)
    {
        RouteResult result = new EventRouter().Route(Create(501, json));

        Assert.IsType<UnknownEvent>(Assert.Single(result.Events));
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public void Route_DefaultJsonElement_DoesNotThrow()
    {
        var socketEvent = new SocketEvent(612, 0, default);

        RouteResult result = new EventRouter().Route(socketEvent);

        UnknownEvent unknown = Assert.IsType<UnknownEvent>(Assert.Single(result.Events));
        Assert.Equal("UnknownEvent { Cmd = 612, SubCmd = 0, Keys = [] }", unknown.ToString());
    }

    [Fact]
    public void UnknownEvent_ToString_PrintsOnlyCmdAndSafeKeyNames()
    {
        string json = """{"data":{"reacts":[{"uidFrom":"84901234567"}],"12345678901":{"x":"secret-value"}}}""";

        RouteResult result = new EventRouter().Route(Create(612, json));

        string text = Assert.Single(result.Events).ToString()!;
        Assert.Contains("612", text);
        Assert.Contains("reacts", text);
        Assert.Contains("<key>", text);
        Assert.DoesNotContain("84901234567", text);
        Assert.DoesNotContain("12345678901", text);
        Assert.DoesNotContain("secret-value", text);
    }
}
