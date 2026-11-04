using System.Globalization;
using System.Text.Json;
using Xunit;
using Luna.Core.Throttling;

namespace Luna.Core.Tests.Throttling;

/// <summary>Each test uses its own temporary directory (deleted after the test) and an injected clock; it does not touch the real %LOCALAPPDATA%.</summary>
public sealed class ApiThrottleGuardTests : IDisposable
{
    private static readonly TimeSpan Ten = TimeSpan.FromMinutes(10);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "luna-core-tests-" + Guid.NewGuid().ToString("N"));

    private DateTimeOffset _now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private string FilePath => Path.Combine(_directory, "nested", "api-throttle.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private ApiThrottleGuard NewGuard() => new(FilePath, () => _now);

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private void WriteRaw(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, text);
    }

    [Fact]
    public void RecordCall_creates_file_with_only_timestamps()
    {
        NewGuard().RecordCall("getfriends");

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(FilePath));
        JsonElement root = doc.RootElement;
        Assert.Equal(["lastCallsUtc"], root.EnumerateObject().Select(p => p.Name).ToArray());
        JsonElement calls = root.GetProperty("lastCallsUtc");
        Assert.Equal(["getfriends"], calls.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(Iso(_now), calls.GetProperty("getfriends").GetString());
    }

    [Fact]
    public void Legacy_file_with_only_blockedUntilUtc_is_still_usable()
    {
        WriteRaw($"{{\"blockedUntilUtc\":\"{Iso(_now.AddMinutes(30))}\"}}");
        ApiThrottleGuard guard = NewGuard();

        Assert.Equal(30, guard.MinutesRemaining());
        Assert.Null(guard.MinutesUntilAllowed("getfriends", Ten));
    }

    [Fact]
    public void RecordCall_keeps_blockedUntilUtc()
    {
        string blocked = Iso(_now.AddMinutes(30));
        WriteRaw($"{{\"blockedUntilUtc\":\"{blocked}\"}}");
        ApiThrottleGuard guard = NewGuard();

        guard.RecordCall("getfriends");

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(FilePath));
        Assert.Equal(blocked, doc.RootElement.GetProperty("blockedUntilUtc").GetString());
        Assert.Equal(30, guard.MinutesRemaining());
        Assert.Equal(10, guard.MinutesUntilAllowed("getfriends", Ten));
    }

    [Fact]
    public void RecordBlock_keeps_lastCallsUtc()
    {
        ApiThrottleGuard guard = NewGuard();
        guard.RecordCall("getfriends");

        guard.RecordBlock();

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(FilePath));
        Assert.Equal(Iso(_now), doc.RootElement.GetProperty("lastCallsUtc").GetProperty("getfriends").GetString());
        Assert.Equal(Iso(_now + ApiThrottleGuard.BlockDuration), doc.RootElement.GetProperty("blockedUntilUtc").GetString());
        Assert.Equal(60, guard.MinutesRemaining());
        Assert.Equal(10, guard.MinutesUntilAllowed("getfriends", Ten));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"x\"")]
    [InlineData("{\"blockedUntilUtc\":5,\"lastCallsUtc\":7}")]
    [InlineData("{\"blockedUntilUtc\":\"khong-phai-ngay\",\"lastCallsUtc\":{\"getfriends\":\"hong\"}}")]
    [InlineData("{\"lastCallsUtc\":[1,2]}")]
    public void Empty_or_corrupt_file_is_treated_as_empty_and_never_throws(string content)
    {
        WriteRaw(content);
        ApiThrottleGuard guard = NewGuard();

        Assert.Null(guard.MinutesRemaining());
        Assert.Null(guard.MinutesUntilAllowed("getfriends", Ten));

        guard.RecordCall("getfriends"); // Can overwrite a corrupt file.
        Assert.Equal(10, guard.MinutesUntilAllowed("getfriends", Ten));
    }

    [Fact]
    public void Missing_file_and_missing_directory_do_not_throw()
    {
        ApiThrottleGuard guard = NewGuard();

        Assert.Null(guard.MinutesRemaining());
        Assert.Null(guard.MinutesUntilAllowed("getfriends", Ten));
        Assert.False(Directory.Exists(_directory)); // Reading does not create the directory.

        guard.RecordCall("getfriends");

        Assert.True(File.Exists(FilePath));
    }

    [Fact]
    public void Unwritable_path_never_throws()
    {
        Directory.CreateDirectory(FilePath); // The file path coincides with a directory: writing will fail.
        ApiThrottleGuard guard = NewGuard();

        guard.RecordCall("getfriends");
        guard.RecordBlock();

        Assert.Null(guard.MinutesRemaining());
        Assert.Null(guard.MinutesUntilAllowed("getfriends", Ten));
    }

    [Fact]
    public void Far_future_marks_are_clamped()
    {
        WriteRaw(
            $"{{\"blockedUntilUtc\":\"{Iso(_now.AddYears(10))}\",\"lastCallsUtc\":{{\"getfriends\":\"{Iso(_now.AddYears(10))}\"}}}}");
        ApiThrottleGuard guard = NewGuard();

        Assert.Equal(60, guard.MinutesRemaining());
        Assert.Equal(10, guard.MinutesUntilAllowed("getfriends", Ten));
    }

    [Fact]
    public void MinutesUntilAllowed_rounds_up_and_is_null_once_interval_has_passed()
    {
        ApiThrottleGuard guard = NewGuard();
        guard.RecordCall("getfriends");

        _now += TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(30);
        Assert.Equal(1, guard.MinutesUntilAllowed("getfriends", Ten));

        _now += TimeSpan.FromSeconds(29);
        Assert.Equal(1, guard.MinutesUntilAllowed("getfriends", Ten));

        _now += TimeSpan.FromSeconds(1);
        Assert.Null(guard.MinutesUntilAllowed("getfriends", Ten));

        _now += TimeSpan.FromHours(1);
        Assert.Null(guard.MinutesUntilAllowed("getfriends", Ten));
    }

    [Fact]
    public void Different_keys_do_not_affect_each_other()
    {
        ApiThrottleGuard guard = NewGuard();

        guard.RecordCall("getfriends");

        Assert.Equal(10, guard.MinutesUntilAllowed("getfriends", Ten));
        Assert.Null(guard.MinutesUntilAllowed("other", Ten));

        guard.RecordCall("other");
        _now += TimeSpan.FromMinutes(5);
        guard.RecordCall("third");

        Assert.Equal(5, guard.MinutesUntilAllowed("getfriends", Ten));
        Assert.Equal(5, guard.MinutesUntilAllowed("other", Ten));
        Assert.Equal(10, guard.MinutesUntilAllowed("third", Ten));
    }

    [Fact]
    public void Consecutive_writes_keep_the_file_valid_and_keep_all_keys()
    {
        ApiThrottleGuard guard = NewGuard();

        guard.RecordCall("a");
        guard.RecordCall("b");
        guard.RecordBlock();
        guard.RecordCall("a");

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(FilePath));
        JsonElement calls = doc.RootElement.GetProperty("lastCallsUtc");
        Assert.Equal(["a", "b"], calls.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.True(doc.RootElement.TryGetProperty("blockedUntilUtc", out _));
    }

    [Fact]
    public void Zero_interval_is_never_blocked()
    {
        ApiThrottleGuard guard = NewGuard();
        guard.RecordCall("getfriends");

        Assert.Null(guard.MinutesUntilAllowed("getfriends", TimeSpan.Zero));
    }
}
