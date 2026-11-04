using System.Text.Json;
using Luna.Protocol.Auth;

namespace Luna.Protocol.Tests.Auth;

public sealed class QrLoginEventsTests
{
    private const string FakeName = "Fake Display Name";
    private const string FakeAvatar = "https://fake-avatar.example/a/b.png";
    private const string FakeUid = "7000000000000000002";
    private const string FakePhone = "0999000111";
    private const string FakeQrCode = "FAKE-QR-CODE-7f3a9c";

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
    public void QrGenerated_ToString_prints_neither_code_nor_png_bytes()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0xDE, 0xAD, 0xBE, 0xEF];
        var generated = new QrGenerated(FakeQrCode, png);

        // The fake values are present in the input data.
        Assert.Equal(FakeQrCode, generated.Code);
        Assert.Equal(png, generated.PngImage);
        string text = generated.ToString();
        Assert.DoesNotContain(FakeQrCode, text);
        Assert.DoesNotContain(Convert.ToBase64String(png), text);
        Assert.DoesNotContain(Convert.ToHexString(png), text);
        Assert.DoesNotContain(Convert.ToHexString(png).ToLowerInvariant(), text);
        Assert.DoesNotContain("DEADBEEF", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("222, 173", text); // Decimal rendering of the bytes, as an array dump would print.
        Assert.DoesNotContain("222,173", text);
        Assert.DoesNotContain("[", text); // The array must not be enumerated or described.
        Assert.Equal("QrGenerated", text);
    }

    [Fact]
    public void QrGenerated_ToString_does_not_throw_for_null_fields()
    {
        var generated = new QrGenerated(null!, null!);

        Assert.Equal("QrGenerated", generated.ToString());
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
