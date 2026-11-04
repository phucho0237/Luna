using Xunit;

namespace Luna.Protocol.Tests;

public class ClientContextTests
{
    [Fact]
    public void Cookies_survive_export_and_import()
    {
        var original = new ClientContext("imei", "ua");
        original.AddCookies("zpw_sek=abc123; _zlang=vi");

        IReadOnlyList<CookieSnapshot> snapshot = original.ExportCookies();

        var restored = new ClientContext("imei", "ua");
        restored.ImportCookies(snapshot);

        string header = restored.Cookies.GetCookieHeader(new Uri("https://wpa.chat.zalo.me/api/login/getLoginInfo"));
        Assert.Contains("zpw_sek=abc123", header);
        Assert.Contains("_zlang=vi", header);
    }

    [Fact]
    public void Imei_has_uuid_and_md5_parts()
    {
        string imei = ClientContext.GenerateImei("TestAgent/1.0");

        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}-[0-9a-f]{32}$", imei);
    }
}