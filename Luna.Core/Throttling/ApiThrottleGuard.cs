using System.Globalization;
using System.Text.Json;

namespace Luna.Core.Throttling;

/// <summary>
/// Remembered between runs: the time the cooldown after HTTP 429 ends (<c>blockedUntilUtc</c>) and the most recent call time of each
/// heavy call point (<c>lastCallsUtc</c>, the key is a short name such as "getfriends"). Every value is ISO 8601 UTC.
/// The default file lives OUTSIDE the repo (<c>%LOCALAPPDATA%\Luna\api-throttle.json</c>); it records only timestamps, no
/// secrets or other data. A missing or corrupt file is treated as empty; no method throws.
/// </summary>
public sealed class ApiThrottleGuard
{
    /// <summary>Minimum wait after a 429 before a real re-run.</summary>
    public static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(60);

    private const string BlockedKey = "blockedUntilUtc";
    private const string CallsKey = "lastCallsUtc";

    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();

    public ApiThrottleGuard(string? path = null, Func<DateTimeOffset>? clock = null)
    {
        _path = path ?? DefaultPath;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Luna", "api-throttle.json");

    /// <summary>Minutes remaining (rounded up) if blocked after a 429, otherwise null. Clamped to [0, <see cref="BlockDuration"/>].</summary>
    public int? MinutesRemaining()
    {
        lock (_lock)
        {
            return RoundedMinutes(ParseTime(ReadState().Blocked), BlockDuration);
        }
    }

    /// <summary>
    /// Minutes (rounded up) to wait before <paramref name="key"/> may be called again, or null if allowed.
    /// Clamped to [0, <paramref name="interval"/>]: a hand-edited file or a clock set backwards must not block longer than the set interval.
    /// </summary>
    public int? MinutesUntilAllowed(string key, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            return null;
        }

        lock (_lock)
        {
            State state = ReadState();
            return state.Calls.TryGetValue(key, out string? raw)
                ? RoundedMinutes(ParseTime(raw) + interval, interval)
                : null;
        }
    }

    /// <summary>Writes blockedUntilUtc = now + <see cref="BlockDuration"/>, leaving lastCallsUtc as is. Write errors are swallowed.</summary>
    public void RecordBlock()
    {
        lock (_lock)
        {
            State state = ReadState();
            Write(state with { Blocked = Iso(_clock() + BlockDuration) });
        }
    }

    /// <summary>Writes lastCallsUtc[key] = now, leaving blockedUntilUtc as is. Write errors are swallowed.</summary>
    public void RecordCall(string key)
    {
        lock (_lock)
        {
            State state = ReadState();
            var calls = new Dictionary<string, string>(state.Calls, StringComparer.Ordinal) { [key] = Iso(_clock()) };
            Write(state with { Calls = calls });
        }
    }

    private sealed record State(string? Blocked, IReadOnlyDictionary<string, string> Calls)
    {
        /// <summary>Does not print endpoint names or timestamps.</summary>
        public override string ToString() => nameof(State);
    }

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseTime(string? raw) =>
        raw is not null
        && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset value)
            ? value
            : null;

    /// <summary>Minutes remaining until <paramref name="until"/>, clamped to [0, <paramref name="max"/>] and rounded up; null if already past.</summary>
    private int? RoundedMinutes(DateTimeOffset? until, TimeSpan max)
    {
        if (until is not { } target)
        {
            return null;
        }

        TimeSpan remaining = target - _clock();
        if (remaining <= TimeSpan.Zero)
        {
            return null;
        }

        if (remaining > max)
        {
            remaining = max;
        }

        return (int)Math.Ceiling(remaining.TotalMinutes);
    }

    /// <summary>Reads the state; any error (missing, corrupt, wrong type) gives an empty state. Keeps only strings and ignores everything else.</summary>
    private State ReadState()
    {
        var empty = new State(null, new Dictionary<string, string>());
        try
        {
            if (!File.Exists(_path))
            {
                return empty;
            }

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(_path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return empty;
            }

            string? blocked = doc.RootElement.TryGetProperty(BlockedKey, out JsonElement b) && b.ValueKind == JsonValueKind.String
                ? b.GetString()
                : null;

            var calls = new Dictionary<string, string>(StringComparer.Ordinal);
            if (doc.RootElement.TryGetProperty(CallsKey, out JsonElement c) && c.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in c.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } value)
                    {
                        calls[property.Name] = value;
                    }
                }
            }

            return new State(blocked, calls);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return empty;
        }
    }

    private void Write(State state)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var root = new Dictionary<string, object>(StringComparer.Ordinal);
            if (state.Blocked is not null)
            {
                root[BlockedKey] = state.Blocked;
            }

            if (state.Calls.Count > 0)
            {
                root[CallsKey] = state.Calls;
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(root));
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            // If the write fails there is no mark next time; the runner has still seen the message.
        }
    }

    private static bool IsExpected(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or ArgumentException or NotSupportedException;
}
