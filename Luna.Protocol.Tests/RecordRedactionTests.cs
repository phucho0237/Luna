using System.Text.Json;
using Luna.Protocol.Events;
using Luna.Protocol.Messaging;
using Luna.Protocol.Realtime;
using Xunit;

namespace Luna.Protocol.Tests;

/// <summary>ToString of records and objects that hold secrets must never print them.</summary>
public class RecordRedactionTests
{
    private const string FakeImei = "fake-imei-0000-1111";
    private const string FakeUserAgent = "FakeAgent/9.9 (fake-ua-marker)";
    private const string FakeCookieName = "fake_cookie_name_zz";
    private const string FakeCookieValue = "fake-cookie-value-qq";
    private const string FakeUid = "9990001112223334445";
    private const string FakePhone = "0999000111";

    [Fact]
    public void CookieSnapshot_ToString_hides_name_and_value()
    {
        var snapshot = new CookieSnapshot(FakeCookieName, FakeCookieValue, ".zalo.me", "/", null, true, true);
        Assert.Equal(FakeCookieName, snapshot.Name);
        Assert.Equal(FakeCookieValue, snapshot.Value);

        string text = snapshot.ToString();

        Assert.Equal(nameof(CookieSnapshot), text);
        Assert.DoesNotContain(FakeCookieName, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeCookieValue, text, StringComparison.Ordinal);
    }

    [Fact]
    public void SocketEvent_ToString_prints_only_cmd_and_subcmd()
    {
        using JsonDocument document = JsonDocument.Parse(
            $$$"""{"data":{"uid":"{{{FakeUid}}}","phone":"{{{FakePhone}}}"}}""");
        var socketEvent = new SocketEvent(501, 0, document.RootElement);
        Assert.Contains(FakeUid, socketEvent.Payload.GetRawText(), StringComparison.Ordinal);
        Assert.Contains(FakePhone, socketEvent.Payload.GetRawText(), StringComparison.Ordinal);

        string text = socketEvent.ToString();

        Assert.Equal("SocketEvent { Cmd = 501, SubCmd = 0 }", text);
        Assert.DoesNotContain(FakeUid, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakePhone, text, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseResult_ToString_does_not_throw_when_the_list_is_null()
    {
        var result = new ParseResult(null!, 3);

        string text = result.ToString();

        Assert.Contains("Skipped = 3", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RouteResult_ToString_does_not_throw_when_the_list_is_null()
    {
        var result = new RouteResult(null!, 2);

        string text = result.ToString();

        Assert.Contains("Skipped = 2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientContext_ToString_is_the_type_name_only()
    {
        var context = new ClientContext(FakeImei, FakeUserAgent);
        context.AddCookies($"{FakeCookieName}={FakeCookieValue}");
        Assert.Equal(FakeImei, context.Imei);
        Assert.Equal(FakeUserAgent, context.UserAgent);
        Assert.Contains(context.ExportCookies(), c => c.Value == FakeCookieValue);

        string text = context.ToString() ?? string.Empty;

        // A plain class: the default Object.ToString prints the full type name and nothing else.
        Assert.Equal(typeof(ClientContext).FullName, text);
        Assert.DoesNotContain(FakeImei, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeCookieValue, text, StringComparison.Ordinal);
    }
}
