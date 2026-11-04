using System.Text;
using Xunit;
using Luna.Core.Sessions;
using Luna.Core.Tests.Support;
using Luna.Protocol;

namespace Luna.Core.Tests.Sessions;

public sealed class EncryptedFileSessionStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "luna-core-tests-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "nested", "session.bin");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static SessionData Sample(string userAgent = "TestAgent/1.0")
    {
        var context = new ClientContext("imei-123", userAgent);
        context.AddCookies("zpw_sek=SEKVALUE; zpsid=SIDVALUE");
        return SessionData.FromContext(context, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Load_returns_null_when_file_is_missing()
    {
        var store = new EncryptedFileSessionStore(FilePath, new XorProtector());

        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task Save_then_load_restores_identity_and_cookies()
    {
        var store = new EncryptedFileSessionStore(FilePath, new XorProtector());
        await store.SaveAsync(Sample());

        SessionData? loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal("imei-123", loaded.Imei);
        Assert.Equal("TestAgent/1.0", loaded.UserAgent);

        ClientContext context = loaded.ToContext();
        string header = context.Cookies.GetCookieHeader(new Uri("https://wpa.chat.zalo.me/"));
        Assert.Contains("zpw_sek=SEKVALUE", header);
        Assert.Contains("zpsid=SIDVALUE", header);
    }

    [Fact]
    public async Task File_on_disk_does_not_contain_plaintext_secrets()
    {
        var store = new EncryptedFileSessionStore(FilePath, new XorProtector());
        await store.SaveAsync(Sample());

        string raw = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(FilePath));

        Assert.DoesNotContain("SEKVALUE", raw);
        Assert.DoesNotContain("imei-123", raw);
    }

    [Fact]
    public async Task Save_overwrites_previous_session_and_leaves_no_temp_file()
    {
        var store = new EncryptedFileSessionStore(FilePath, new XorProtector());
        await store.SaveAsync(Sample("First/1.0"));
        await store.SaveAsync(Sample("Second/2.0"));

        SessionData? loaded = await store.LoadAsync();

        Assert.Equal("Second/2.0", loaded?.UserAgent);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public async Task Load_returns_null_for_corrupt_file()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllBytesAsync(FilePath, [1, 2, 3, 4, 5]);
        var store = new EncryptedFileSessionStore(FilePath, new XorProtector());

        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task Load_returns_null_when_protector_cannot_decrypt()
    {
        await new EncryptedFileSessionStore(FilePath, new XorProtector()).SaveAsync(Sample());
        var store = new EncryptedFileSessionStore(FilePath, new ThrowingProtector());

        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task Load_returns_null_for_unknown_version()
    {
        var store = new EncryptedFileSessionStore(FilePath, new XorProtector());
        await store.SaveAsync(Sample() with { Version = 99 });

        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task Delete_removes_the_file_and_is_safe_when_missing()
    {
        var store = new EncryptedFileSessionStore(FilePath, new XorProtector());
        await store.SaveAsync(Sample());

        await store.DeleteAsync();
        await store.DeleteAsync();

        Assert.False(File.Exists(FilePath));
    }
}