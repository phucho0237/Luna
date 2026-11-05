namespace Luna.Core.Chat;

/// <summary>
/// The sort key of a stored message: the same (timestamp, row id) order the store pages by. Numbers only, so printing it
/// leaks nothing. The row id is unique, so two equal keys are the same message.
/// </summary>
public readonly record struct MessageKey(long TimestampMs, long Id) : IComparable<MessageKey>
{
    /// <summary>Ascending store order: by timestamp, then by row id.</summary>
    public int CompareTo(MessageKey other)
    {
        int byTime = TimestampMs.CompareTo(other.TimestampMs);
        return byTime != 0 ? byTime : Id.CompareTo(other.Id);
    }

    public static bool operator <(MessageKey left, MessageKey right) => left.CompareTo(right) < 0;

    public static bool operator >(MessageKey left, MessageKey right) => left.CompareTo(right) > 0;

    public static bool operator <=(MessageKey left, MessageKey right) => left.CompareTo(right) <= 0;

    public static bool operator >=(MessageKey left, MessageKey right) => left.CompareTo(right) >= 0;
}
