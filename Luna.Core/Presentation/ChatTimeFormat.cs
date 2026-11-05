using System.Globalization;

namespace Luna.Core.Presentation;

/// <summary>
/// Pure formatting of a conversation timestamp for the list: the time of day for today, the day and month for earlier
/// days of the same year, and the full date otherwise. It always uses the invariant culture and a 24 hour clock, and it
/// has no clock of its own: the caller passes "now" so the result is deterministic.
/// </summary>
public static class ChatTimeFormat
{
    /// <summary>
    /// Formats <paramref name="unixMilliseconds"/> in the offset of <paramref name="nowLocal"/>. "HH:mm" when it is on the
    /// same local day as now or later (a future timestamp from clock skew is shown like today); "dd/MM" when it is
    /// earlier in the same year; "dd/MM/yyyy" otherwise. A zero or negative value (no timestamp) and a value outside the
    /// range of a date give an empty string. Never throws.
    /// </summary>
    public static string Format(long unixMilliseconds, DateTimeOffset nowLocal)
    {
        if (unixMilliseconds <= 0)
        {
            return string.Empty;
        }

        DateTimeOffset moment;
        try
        {
            moment = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).ToOffset(nowLocal.Offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return string.Empty;
        }

        DateTime stamp = moment.DateTime;
        DateTime today = nowLocal.DateTime;
        CultureInfo culture = CultureInfo.InvariantCulture;

        if (stamp.Date >= today.Date)
        {
            return stamp.ToString("HH:mm", culture);
        }

        return stamp.Year == today.Year
            ? stamp.ToString("dd/MM", culture)
            : stamp.ToString("dd/MM/yyyy", culture);
    }
}
