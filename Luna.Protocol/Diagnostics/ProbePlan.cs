namespace Luna.Protocol.Diagnostics;

/// <summary>
/// Limits of the old-message probe (T5b) and the pure decision whether the next page may be requested. No I/O and no clock:
/// the caller passes elapsed seconds. Anything that cannot be measured (NaN) stops the probe.
/// </summary>
public static class ProbePlan
{
    /// <summary>Most pages requested per scope (private, then group).</summary>
    public const int MaxPagesPerScope = 3;

    /// <summary>Least time between two requests.</summary>
    public const int MinSecondsBetweenRequests = 5;

    /// <summary>How long to wait for the answer to one request.</summary>
    public const int ResponseTimeoutSeconds = 15;

    /// <summary>The whole probe stops after this long.</summary>
    public const int MaxRunSeconds = 90;

    /// <summary>Least time between two runs of the probe.</summary>
    public const int MinMinutesBetweenRuns = 30;

    /// <summary>Pause after the socket reports Connected, before the first request.</summary>
    public const int SettleSecondsAfterConnect = 2;

    /// <summary>The key under which a run is recorded in <c>ApiThrottleGuard</c>.</summary>
    public const string GuardKey = "history-probe";

    /// <summary>
    /// Seconds still to wait before the next request: <see cref="MinSecondsBetweenRequests"/> minus the time since the last
    /// one, clamped to [0, <see cref="MinSecondsBetweenRequests"/>] (a clock set backwards must not lengthen the wait).
    /// An unmeasurable value (NaN) gives the full spacing.
    /// </summary>
    public static double SecondsToWait(double secondsSinceLastRequest)
    {
        if (double.IsNaN(secondsSinceLastRequest))
        {
            return MinSecondsBetweenRequests;
        }

        return Math.Clamp(MinSecondsBetweenRequests - secondsSinceLastRequest, 0, MinSecondsBetweenRequests);
    }

    /// <summary>
    /// Whether another page may be requested. False on any error, on an empty last page (once at least one page was done),
    /// at the page limit, when the time limit would be reached (elapsed time plus the wait still needed for the spacing),
    /// or when a time is NaN. With <paramref name="pagesDone"/> of 0 only the error and time rules apply.
    /// </summary>
    public static bool ShouldRequestNext(
        int pagesDone, int lastPageMessageCount, bool hasError, double secondsSinceLastRequest, double secondsSinceStart)
    {
        if (hasError || double.IsNaN(secondsSinceLastRequest) || double.IsNaN(secondsSinceStart))
        {
            return false;
        }

        if (pagesDone > 0 && lastPageMessageCount <= 0)
        {
            return false;
        }

        if (pagesDone >= MaxPagesPerScope)
        {
            return false;
        }

        double elapsed = Math.Max(0, secondsSinceStart);
        return elapsed + SecondsToWait(secondsSinceLastRequest) < MaxRunSeconds;
    }
}
