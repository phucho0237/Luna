using System.Text.Json;
using Xunit;
using Luna.Protocol.Events;

namespace Luna.Protocol.Tests.Events;

public class ControlEventsTests
{
    private static RouteResult Route(string json) =>
        new EventRouter().Route(EventRouterTests.Create(601, json));

    private static string Wrap(string content) =>
        """{"data":{"controls":[{"actionId":"1","controlId":"c","content":""" + content + """}],"lastActionId":"5","more":0}}""";

    [Theory]
    [InlineData("group", "update", ControlKind.Group)]
    [InlineData("fr", "req_v2", ControlKind.Friend)]
    [InlineData("mute", "set", ControlKind.Mute)]
    [InlineData("pinned_convers", "update", ControlKind.PinnedConversation)]
    [InlineData("emoji_status", "add", ControlKind.EmojiStatus)]
    [InlineData("oneone", "remove_reminder11", ControlKind.OneOne)]
    [InlineData("file_done", "x", ControlKind.FileDone)]
    [InlineData("khac", "y", ControlKind.Other)]
    public void Route_Controls_ClassifiesByActType(string actType, string act, ControlKind kind)
    {
        string content = $$$"""{"flag":1,"act":"{{{act}}}","data":"payload","ts":1700000000000,"act_type":"{{{actType}}}"}""";

        ControlEvent control = Assert.IsType<ControlEvent>(Assert.Single(Route(Wrap(content)).Events));

        Assert.Equal(kind, control.Kind);
        Assert.Equal(actType, control.ActType);
        Assert.Equal(act, control.Act);
    }

    [Fact]
    public void Route_DataAsObject_KeepsContentVerbatim()
    {
        string content = """{"act_type":"mute","act":"set","ts":1,"data":{"ts_clicked":2}}""";

        ControlEvent control = Assert.IsType<ControlEvent>(Assert.Single(Route(Wrap(content)).Events));

        Assert.Equal(JsonValueKind.Object, control.Content.GetProperty("data").ValueKind);
        Assert.Equal(2, control.Content.GetProperty("data").GetProperty("ts_clicked").GetInt32());
    }

    [Fact]
    public void Route_ContentWithoutTsOrFlag_StillParses()
    {
        ControlEvent control = Assert.IsType<ControlEvent>(
            Assert.Single(Route(Wrap("""{"data":"s","act_type":"group","act":"join"}""")).Events));

        Assert.Equal(ControlKind.Group, control.Kind);
    }

    [Fact]
    public void Route_MissingContentOrActType_AreSkipped()
    {
        string json = """{"data":{"controls":[{"actionId":"1"},{"content":{"act":"x"}},{"content":"chuoi"},{"content":{"act_type":"fr","act":"add"}}]}}""";

        RouteResult result = Route(json);

        Assert.Single(result.Events.OfType<ControlEvent>());
        Assert.Equal(3, result.Skipped);
        Assert.DoesNotContain(result.Events, e => e is UnknownEvent);
    }

    [Fact]
    public void ToString_PrintsOnlyKindAndSafeTokens_NotContent()
    {
        string content = """{"act_type":"group","act":"update","data":"{\"groupId\":\"55500011122\",\"uid\":\"84901234567\"}","ts":1}""";

        string text = Route(Wrap(content)).Events[0].ToString()!;

        Assert.Equal("ControlEvent { Kind = Group, ActType = group, Act = update }", text);
        Assert.DoesNotContain("84901234567", text);
        Assert.DoesNotContain("55500011122", text);
    }

    [Fact]
    public void ToString_UnsafeActIsReplacedByPlaceholder()
    {
        string text = Route(Wrap("""{"act_type":"group","act":"84901234567"}""")).Events[0].ToString()!;

        Assert.DoesNotContain("84901234567", text);
    }
}
