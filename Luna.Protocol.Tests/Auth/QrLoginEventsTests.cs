using System.Text.Json;
using Luna.Protocol.Auth;

namespace Luna.Protocol.Tests.Auth;

public sealed class QrLoginEventsTests
{
    private const string FakeName = "Fake Display Name";
    private const string FakeAvatar = "https://fake-avatar.example/a/b.png";
    private const string FakeUid = "7000000000000000002";
    private const string FakePhone = "0999000111";

    [Fact]
    public void QrScanned_ToString_prints_neither_name_nor_avatar()
    {
        var scanned = new QrScanned(FakeName, FakeAvatar);

        // The fake values are present in the input data.
        Assert.Equal(FakeName, scanned.DisplayName);
        Assert.Equal(FakeAvatar, scanned.AvatarUrl);
        string text = scanned.ToString();
        Assert.DoesNotContain(FakeName, text);
        Assert.DoesNotContain(FakeAvatar, text);
        Assert.Equal("QrScanned", text);
    }

    [Fact]
    public void QrScanned_ToString_does_not_throw_for_null_avatar()
    {
        var scanned = new QrScanned(FakeName, null);

        string text = scanned.ToString();

        Assert.DoesNotContain(FakeName, text);
        Assert.Equal("QrScanned", text);
    }

    [Fact]
    public void QrLoginResult_ToString_prints_neither_name_nor_user_info()
    {
        string json = $"{{\"name\":\"{FakeName}\",\"uid\":\"{FakeUid}\",\"phone\":\"{FakePhone}\"}}";
        using JsonDocument doc = JsonDocument.Parse(json);
        var result = new QrLoginResult(FakeName, doc.RootElement);

        // The fake values are present in the input data.
        Assert.Equal(FakeName, result.DisplayName);
        Assert.Equal(FakeUid, result.UserInfo.GetProperty("uid").GetString());
        string text = result.ToString();
        Assert.DoesNotContain(FakeName, text);
        Assert.DoesNotContain(FakeUid, text);
        Assert.DoesNotContain(FakePhone, text);
        Assert.DoesNotContain(json, text);
        Assert.Equal("QrLoginResult", text);
    }

    [Fact]
    public void QrLoginResult_ToString_does_not_throw_for_default_user_info()
    {
        var result = new QrLoginResult(FakeName, default);

        string text = result.ToString();

        Assert.DoesNotContain(FakeName, text);
        Assert.Equal("QrLoginResult", text);
    }
}
