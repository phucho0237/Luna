using System.Text.Json;
using Luna.Protocol.Auth;

namespace Luna.Protocol.Tests.Auth;

public sealed class LoginInfoTests
{
    private const string FakeUid = "7000000000000000001";
    private const string FakeEnk = "AAECAwQFBgcICQoLDA0ODw==";

    private static LoginInfo Parse(string extra = "")
    {
        using JsonDocument doc = JsonDocument.Parse(
            $"{{\"uid\":\"{FakeUid}\",\"zpw_enk\":\"{FakeEnk}\"{extra}}}");
        return LoginInfo.Parse(doc.RootElement);
    }

    private const string FakeSend2Me = "7000000000000000009";

    [Fact]
    public void Send2MeId_is_read_when_non_empty_string()
    {
        Assert.Equal(FakeSend2Me, Parse($",\"send2me_id\":\"{FakeSend2Me}\"").Send2MeId);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"send2me_id\":\"\"")]
    [InlineData(",\"send2me_id\":123")]
    [InlineData(",\"send2me_id\":null")]
    [InlineData(",\"send2me_id\":{\"a\":1}")]
    [InlineData(",\"send2me_id\":[\"x\"]")]
    [InlineData(",\"send2me_id\":true")]
    [InlineData(",\"send2me_id\":\"\\ud800\"")]
    public void Send2MeId_is_null_when_missing_empty_or_wrong_type(string extra)
    {
        Assert.Null(Parse(extra).Send2MeId);
    }

    [Fact]
    public void ToString_does_not_print_send2me_id()
    {
        LoginInfo info = Parse($",\"send2me_id\":\"{FakeSend2Me}\"");

        Assert.Equal(FakeSend2Me, info.Send2MeId);
        Assert.DoesNotContain(FakeSend2Me, info.ToString());
    }

    [Fact]
    public void ToString_does_not_print_uid_or_key()
    {
        LoginInfo info = Parse();

        Assert.Equal(FakeUid, info.Uid); // The fake value is present in the input data.
        string text = info.ToString();
        Assert.DoesNotContain(FakeUid, text);
        Assert.DoesNotContain(FakeEnk, text);
        Assert.Equal("LoginInfo", text);
    }
}
