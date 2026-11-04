namespace Luna.Protocol.Api;

/// <summary>Options for <see cref="ApiClient"/>. Every property has a safe default.</summary>
public sealed class ApiClientOptions
{
    /// <summary>Minimum interval between two STARTS of an HTTP request (including attempts on a fallback host).</summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Minimum interval between two CALLS to the same endpoint (key: exact path, case-insensitive).
    /// Default: getfriends 10 minutes. This is a client-side choice, NOT a value measured on the server.
    /// A call that arrives too early throws <see cref="RateLimitedException"/> without sending anything; it does not count as a 429.
    /// </summary>
    public IReadOnlyDictionary<string, TimeSpan> EndpointMinIntervals { get; init; } =
        new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase)
        {
            ["/api/social/friend/getfriends"] = TimeSpan.FromMinutes(10),
            // Sending a message is a write operation: 60 seconds is a conservative client-side choice, not verified on the live server.
            ["/api/message/sms"] = TimeSpan.FromSeconds(60),
        };

    /// <summary>Maximum number of hosts tried for one call (never more than the number of hosts of the service).</summary>
    public int MaxHostAttempts { get; init; } = 3;

    /// <summary>Delay function. Default <see cref="Task.Delay(TimeSpan, CancellationToken)"/>; tests inject a fake so nothing really waits.</summary>
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }

    /// <summary>Clock. Default <see cref="DateTimeOffset.UtcNow"/>; tests inject a fake.</summary>
    public Func<DateTimeOffset>? Clock { get; init; }

    /// <summary>
    /// Receives diagnostic lines. Writes only the service name, path, host index and attempt number;
    /// never writes a URL, query, params, cookie, key or response content.
    /// </summary>
    public Action<string>? Trace { get; init; }
}
