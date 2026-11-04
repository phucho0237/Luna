namespace Luna.Protocol.Api;

/// <summary>
/// The server returned HTTP 429, or the client is in its cooldown after a 429 (in which case no request is sent).
/// The client does not retry by itself: the caller must stop and wait at least <see cref="RetryAfter"/>.
/// </summary>
public sealed class RateLimitedException : Exception
{
    public RateLimitedException()
        : this(TimeSpan.Zero)
    {
    }

    /// <param name="retryAfter">Time remaining in the cooldown; a negative value is clamped to 0.</param>
    public RateLimitedException(TimeSpan retryAfter)
        : base(BuildMessage(retryAfter))
    {
        RetryAfter = retryAfter < TimeSpan.Zero ? TimeSpan.Zero : retryAfter;
    }

    /// <summary>Time remaining in the client's cooldown. Not the server's penalty time (unknown).</summary>
    public TimeSpan RetryAfter { get; }

    // The message contains only the number of seconds, no URL or data.
    private static string BuildMessage(TimeSpan retryAfter) =>
        retryAfter > TimeSpan.Zero
            ? $"The server returned HTTP 429 (too many requests). Stop, do not retry by yourself; wait at least {(int)Math.Ceiling(retryAfter.TotalSeconds)} seconds."
            : "The server returned HTTP 429 (too many requests). Stop, do not retry by yourself.";
}
