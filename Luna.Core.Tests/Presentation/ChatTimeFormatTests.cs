using System.Globalization;
using Luna.Core.Presentation;

namespace Luna.Core.Tests.Presentation;

public class ChatTimeFormatTests
{
    private static readonly TimeSpan Utc7 = TimeSpan.FromHours(7);

    private static long Ms(int year, int month, int day, int hour, int minute, TimeSpan offset) =>
        new DateTimeOffset(year, month, day, hour, minute, 0, offset).ToUnixTimeMilliseconds();

    private static DateTimeOffset Now(int year, int month, int day, int hour, int minute, TimeSpan offset) =>
        new(year, month, day, hour, minute, 0, offset);

    [Fact]
    public void The_same_day_shows_the_time_on_a_24_hour_clock()
    {
        string text = ChatTimeFormat.Format(Ms(2026, 11, 5, 15, 7, Utc7), Now(2026, 11, 5, 21, 30, Utc7));

        Assert.Equal("15:07", text);
    }

    [Fact]
    public void Midnight_and_the_last_minute_of_the_day_are_the_same_day()
    {
        DateTimeOffset now = Now(2026, 11, 5, 23, 59, Utc7);

        Assert.Equal("00:00", ChatTimeFormat.Format(Ms(2026, 11, 5, 0, 0, Utc7), now));
        Assert.Equal("23:59", ChatTimeFormat.Format(Ms(2026, 11, 5, 23, 59, Utc7), now));
    }

    [Fact]
    public void One_minute_before_midnight_is_the_previous_day()
    {
        string text = ChatTimeFormat.Format(Ms(2026, 11, 4, 23, 59, Utc7), Now(2026, 11, 5, 0, 0, Utc7));

        Assert.Equal("04/11", text);
    }

    [Fact]
    public void An_earlier_day_of_the_same_year_shows_day_and_month()
    {
        string text = ChatTimeFormat.Format(Ms(2026, 1, 2, 8, 0, Utc7), Now(2026, 11, 5, 9, 0, Utc7));

        Assert.Equal("02/01", text);
    }

    [Fact]
    public void The_first_instant_of_the_year_is_in_the_same_year()
    {
        string text = ChatTimeFormat.Format(Ms(2026, 1, 1, 0, 0, Utc7), Now(2026, 1, 1, 12, 0, Utc7));

        Assert.Equal("00:00", text);

        string later = ChatTimeFormat.Format(Ms(2026, 1, 1, 0, 0, Utc7), Now(2026, 1, 2, 0, 0, Utc7));

        Assert.Equal("01/01", later);
    }

    [Fact]
    public void A_timestamp_from_the_previous_year_shows_the_year()
    {
        string text = ChatTimeFormat.Format(Ms(2025, 12, 31, 23, 59, Utc7), Now(2026, 1, 1, 0, 1, Utc7));

        Assert.Equal("31/12/2025", text);
    }

    [Fact]
    public void A_timestamp_in_the_future_is_shown_like_today()
    {
        DateTimeOffset now = Now(2026, 11, 5, 10, 0, Utc7);

        Assert.Equal("10:05", ChatTimeFormat.Format(Ms(2026, 11, 5, 10, 5, Utc7), now));
        Assert.Equal("09:00", ChatTimeFormat.Format(Ms(2026, 11, 9, 9, 0, Utc7), now));
        Assert.Equal("09:00", ChatTimeFormat.Format(Ms(2027, 3, 1, 9, 0, Utc7), now));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void A_zero_or_negative_timestamp_gives_an_empty_string(long ms)
    {
        Assert.Equal(string.Empty, ChatTimeFormat.Format(ms, Now(2026, 11, 5, 10, 0, Utc7)));
    }

    [Fact]
    public void A_timestamp_too_large_for_a_date_gives_an_empty_string()
    {
        Assert.Equal(string.Empty, ChatTimeFormat.Format(long.MaxValue, Now(2026, 11, 5, 10, 0, Utc7)));
    }

    [Fact]
    public void The_same_instant_is_shown_in_the_offset_of_now()
    {
        // 2026-11-05 18:30 UTC is 01:30 on the 6th in UTC+7 and 13:30 on the 5th in UTC-5.
        long ms = Ms(2026, 11, 5, 18, 30, TimeSpan.Zero);

        Assert.Equal("01:30", ChatTimeFormat.Format(ms, Now(2026, 11, 6, 9, 0, Utc7)));
        Assert.Equal("13:30", ChatTimeFormat.Format(ms, Now(2026, 11, 5, 20, 0, TimeSpan.FromHours(-5))));
    }

    [Fact]
    public void The_same_instant_can_be_today_in_one_offset_and_yesterday_in_another()
    {
        long ms = Ms(2026, 11, 5, 18, 30, TimeSpan.Zero);

        // Now is 2026-11-06 02:00 in UTC+7 (= 19:00 UTC on the 5th): the stamp is on the 6th locally, so today.
        Assert.Equal("01:30", ChatTimeFormat.Format(ms, Now(2026, 11, 6, 2, 0, Utc7)));

        // Now is 2026-11-07 00:30 in UTC+7: the stamp (the 6th locally) is the previous day.
        Assert.Equal("06/11", ChatTimeFormat.Format(ms, Now(2026, 11, 7, 0, 30, Utc7)));
    }

    [Fact]
    public void The_year_is_compared_in_the_offset_of_now()
    {
        // 2025-12-31 18:00 UTC is already 2026-01-01 01:00 in UTC+7, so it is the same year as now.
        long ms = Ms(2025, 12, 31, 18, 0, TimeSpan.Zero);

        Assert.Equal("01/01", ChatTimeFormat.Format(ms, Now(2026, 1, 3, 9, 0, Utc7)));
    }

    [Fact]
    public void The_result_ignores_the_current_culture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            string text = ChatTimeFormat.Format(Ms(2025, 3, 4, 15, 7, Utc7), Now(2026, 11, 5, 9, 0, Utc7));

            Assert.Equal("04/03/2025", text);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
