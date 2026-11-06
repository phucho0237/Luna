using System.Text;
using System.Text.Json;
using Xunit;
using Luna.Protocol.Diagnostics;
using Luna.Protocol.Realtime;

namespace Luna.Protocol.Tests.Diagnostics;

/// <summary>HistoryProbeSummary and ProbePlan. Every payload here is FAKE; nothing touches a server.</summary>
public class HistoryProbeSummaryTests
{
    private const string Self = "1000000000001";
    private const string Other1 = "2000000000002";
    private const string Other2 = "2000000000003";
    private const string Group1 = "3000000000004";
    private const string Group2 = "3000000000005";

    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private static JsonElement Json(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static long DaysAgoMs(double days) => Now.AddDays(-days).ToUnixTimeMilliseconds();

    private static long DaysAgoSeconds(double days) => Now.AddDays(-days).ToUnixTimeSeconds();

    private static string Msg(
        string uidFrom, string idTo, string msgId, long ts, string msgType = "webchat", string text = "FAKE-TEXT-ALPHA") =>
        "{\"uidFrom\":\"" + uidFrom + "\",\"idTo\":\"" + idTo + "\",\"msgId\":\"" + msgId + "\",\"cliMsgId\":\"c" + msgId
        + "\",\"msgType\":\"" + msgType + "\",\"ts\":\"" + ts + "\",\"dName\":\"Fake Person Zed\",\"content\":\""
        + text + "\",\"status\":1}";

    private static JsonElement Page(string arrayName, string[] messages, string extra = "") =>
        Json("{\"error_code\":0,\"data\":{\"" + arrayName + "\":[" + string.Join(",", messages) + "]" + extra + "}}");

    private static JsonElement PrivatePage(params string[] messages) => Page("msgs", messages);

    private static HistoryProbeSummary Private(JsonElement payload, HistoryProbeSummary? previous = null) =>
        HistoryProbeSummary.From(payload, HistoryScope.Private, Self, Now, previous);

    [Fact]
    public void Private_page_counts_messages_self_others_and_threads_from_the_parser()
    {
        JsonElement payload = PrivatePage(
            Msg(Other1, Self, "11", DaysAgoMs(2)),
            Msg("0", Other1, "12", DaysAgoMs(5)),
            Msg(Self, Other2, "13", DaysAgoMs(10), "chat.photo"));

        HistoryProbeSummary summary = Private(payload);

        Assert.Equal(3, summary.Parsed);
        Assert.Equal(0, summary.Skipped);
        Assert.Equal(2, summary.SelfCount);
        Assert.Equal(1, summary.OthersCount);
        Assert.Equal(2, summary.ThreadCount);
        Assert.Equal(10, summary.OldestDaysAgo);
        Assert.Equal(2, summary.NewestDaysAgo);
        Assert.Null(summary.OverlapWithPrevious);
        Assert.Null(summary.ContinuesOlder);
    }

    [Fact]
    public void Self_is_decided_by_the_parser_for_both_the_real_uid_and_the_zero_marker()
    {
        HistoryProbeSummary realUid = Private(PrivatePage(Msg(Self, Other1, "21", DaysAgoMs(1))));
        HistoryProbeSummary zero = Private(PrivatePage(Msg("0", Other1, "22", DaysAgoMs(1))));
        HistoryProbeSummary other = Private(PrivatePage(Msg(Other1, Self, "23", DaysAgoMs(1))));
        HistoryProbeSummary noSelfUid = HistoryProbeSummary.From(
            PrivatePage(Msg(Self, Other1, "24", DaysAgoMs(1))), HistoryScope.Private, null, Now);

        Assert.Equal((1, 0), (realUid.SelfCount, realUid.OthersCount));
        Assert.Equal((1, 0), (zero.SelfCount, zero.OthersCount));
        Assert.Equal((0, 1), (other.SelfCount, other.OthersCount));
        Assert.Equal((0, 1), (noSelfUid.SelfCount, noSelfUid.OthersCount));
    }

    [Fact]
    public void Group_page_uses_groupMsgs_and_counts_groups_as_threads()
    {
        JsonElement payload = Page(
            "groupMsgs",
            [
                Msg(Other1, Group1, "31", DaysAgoMs(3)),
                Msg(Self, Group1, "32", DaysAgoMs(4)),
                Msg(Other2, Group2, "33", DaysAgoMs(6)),
            ]);

        HistoryProbeSummary summary = HistoryProbeSummary.From(payload, HistoryScope.Group, Self, Now);

        Assert.Equal(3, summary.Parsed);
        Assert.Equal(1, summary.SelfCount);
        Assert.Equal(2, summary.OthersCount);
        Assert.Equal(2, summary.ThreadCount);
        Assert.Equal(6, summary.OldestDaysAgo);
        Assert.Equal(3, summary.NewestDaysAgo);
    }

    [Fact]
    public void The_scope_decides_which_array_is_read()
    {
        JsonElement groupPayload = Page("groupMsgs", [Msg(Other1, Group1, "41", DaysAgoMs(1))]);

        HistoryProbeSummary asPrivate = Private(groupPayload);

        Assert.Equal(0, asPrivate.Parsed);
    }

    [Fact]
    public void Empty_page_has_zero_counts_no_days_and_no_next_id()
    {
        HistoryProbeSummary summary = Private(PrivatePage());

        Assert.Equal(0, summary.Parsed);
        Assert.Equal(0, summary.ThreadCount);
        Assert.Null(summary.OldestDaysAgo);
        Assert.Null(summary.NewestDaysAgo);
        Assert.False(summary.TryGetNextLastId(out string id));
        Assert.Equal(string.Empty, id);
        Assert.Contains("msgs", summary.Shape, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[1,\"a\",null]")]
    [InlineData("{}")]
    [InlineData("{\"data\":\"x\"}")]
    [InlineData("{\"data\":[1,2]}")]
    [InlineData("{\"data\":{\"msgs\":\"nope\"}}")]
    [InlineData("{\"data\":{\"msgs\":{\"a\":1}}}")]
    [InlineData("{\"msgs\":7}")]
    public void Wrong_types_do_not_throw_and_count_nothing(string json)
    {
        HistoryProbeSummary summary = Private(Json(json));

        Assert.Equal(0, summary.Parsed);
        Assert.Null(summary.OldestDaysAgo);
        Assert.False(summary.TryGetNextLastId(out _));
        Assert.False(string.IsNullOrEmpty(summary.Format()));
    }

    [Fact]
    public void Elements_that_are_not_objects_are_counted_as_skipped()
    {
        JsonElement payload = Json(
            "{\"data\":{\"msgs\":[1,\"x\",null," + Msg(Other1, Self, "51", DaysAgoMs(1)) + "]}}");

        HistoryProbeSummary summary = Private(payload);

        Assert.Equal(1, summary.Parsed);
        Assert.Equal(3, summary.Skipped);
    }

    [Fact]
    public void Payload_without_the_data_wrapper_is_read_from_the_top_level()
    {
        JsonElement payload = Json("{\"msgs\":[" + Msg(Other1, Self, "61", DaysAgoMs(7)) + "]}");

        HistoryProbeSummary summary = Private(payload);

        Assert.Equal(1, summary.Parsed);
        Assert.Equal(7, summary.OldestDaysAgo);
    }

    [Fact]
    public void Huge_pages_are_summarized_without_trouble()
    {
        string[] messages = new string[5000];
        for (int i = 0; i < messages.Length; i++)
        {
            messages[i] = Msg(i % 2 == 0 ? Other1 : Other2, Self, (1_000_000_000_000_000_000L + i).ToString(System.Globalization.CultureInfo.InvariantCulture), DaysAgoMs(1 + (i % 400)));
        }

        HistoryProbeSummary summary = Private(PrivatePage(messages));

        Assert.Equal(5000, summary.Parsed);
        Assert.Equal(2, summary.ThreadCount);
        Assert.Equal(400, summary.OldestDaysAgo);
        Assert.Equal(1, summary.NewestDaysAgo);
        Assert.Equal(5000, summary.KindCounts.Values.Sum());
        Assert.True(summary.TryGetNextLastId(out _));
    }

    [Fact]
    public void Timestamps_in_seconds_and_in_milliseconds_give_the_same_days()
    {
        HistoryProbeSummary milliseconds = Private(PrivatePage(
            Msg(Other1, Self, "71", DaysAgoMs(10)), Msg(Other1, Self, "72", DaysAgoMs(2))));
        HistoryProbeSummary seconds = Private(PrivatePage(
            Msg(Other1, Self, "71", DaysAgoSeconds(10)), Msg(Other1, Self, "72", DaysAgoSeconds(2))));

        Assert.Equal((10, 2), (milliseconds.OldestDaysAgo, milliseconds.NewestDaysAgo));
        Assert.Equal((10, 2), (seconds.OldestDaysAgo, seconds.NewestDaysAgo));
    }

    [Fact]
    public void Days_are_whole_days_rounded_down()
    {
        HistoryProbeSummary summary = Private(PrivatePage(
            Msg(Other1, Self, "81", DaysAgoMs(9.5)), Msg(Other1, Self, "82", DaysAgoMs(0.4))));

        Assert.Equal(9, summary.OldestDaysAgo);
        Assert.Equal(0, summary.NewestDaysAgo);
    }

    [Fact]
    public void Future_timestamps_count_as_zero_days_and_negative_or_unreadable_ones_are_ignored()
    {
        HistoryProbeSummary future = Private(PrivatePage(Msg(Other1, Self, "91", DaysAgoMs(-3))));
        HistoryProbeSummary hugeFuture = Private(PrivatePage(Msg(Other1, Self, "92", long.MaxValue)));
        HistoryProbeSummary negative = Private(PrivatePage(
            Msg(Other1, Self, "93", -5), Msg(Other1, Self, "94", 0)));
        HistoryProbeSummary unreadable = Private(Json(
            "{\"data\":{\"msgs\":[{\"uidFrom\":\"" + Other1 + "\",\"idTo\":\"" + Self
            + "\",\"msgId\":\"95\",\"ts\":1e30},{\"uidFrom\":\"" + Other1 + "\",\"idTo\":\"" + Self
            + "\",\"msgId\":\"96\",\"ts\":\"soon\"}]}}"));

        Assert.Equal((0, 0), (future.OldestDaysAgo, future.NewestDaysAgo));
        Assert.Equal((0, 0), (hugeFuture.OldestDaysAgo, hugeFuture.NewestDaysAgo));
        Assert.Equal(2, negative.Parsed);
        Assert.Null(negative.OldestDaysAgo);
        Assert.False(negative.TryGetNextLastId(out _));
        Assert.Equal(2, unreadable.Parsed);
        Assert.Null(unreadable.OldestDaysAgo);
    }

    [Fact]
    public void Kinds_are_counted_through_SafeName_with_dots_turned_into_underscores_and_capped()
    {
        HistoryProbeSummary summary = Private(PrivatePage(
            Msg(Other1, Self, "101", DaysAgoMs(1), "webchat"),
            Msg(Other1, Self, "102", DaysAgoMs(1), "chat.photo"),
            Msg(Other1, Self, "103", DaysAgoMs(1), "chat.photo"),
            Msg(Other1, Self, "104", DaysAgoMs(1), "AVeryLongMessageTypeNameThatIsNotSafeToPrint"),
            Msg(Other1, Self, "105", DaysAgoMs(1), "12345678")));

        Assert.Equal(1, summary.KindCounts["webchat"]);
        Assert.Equal(2, summary.KindCounts["chat_photo"]);
        Assert.Equal(2, summary.KindCounts["<key>"]);

        string[] many = Enumerable.Range(0, 20)
            .Select(i => Msg(Other1, Self, (200 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), DaysAgoMs(1), "kind" + (char)('a' + i)))
            .ToArray();
        HistoryProbeSummary capped = Private(PrivatePage(many));

        Assert.True(capped.KindCounts.Count <= 13);
        Assert.Equal(20, capped.KindCounts.Values.Sum());
    }

    [Fact]
    public void Extra_numeric_and_boolean_fields_are_reported_by_name_only()
    {
        JsonElement payload = Page(
            "msgs",
            [Msg(Other1, Self, "111", DaysAgoMs(1))],
            ",\"more\":1,\"hasMore\":true,\"label\":\"FAKE-LABEL\",\"nested\":{\"a\":1},\"total\":5");

        HistoryProbeSummary summary = Private(payload);

        Assert.Equal(["error_code", "hasMore", "more", "total"], summary.ExtraFieldNames.ToArray());
    }

    [Fact]
    public void Shape_describes_names_and_types_to_depth_two()
    {
        HistoryProbeSummary summary = Private(PrivatePage(Msg(Other1, Self, "121", DaysAgoMs(1))));

        Assert.Contains("error_code: number", summary.Shape, StringComparison.Ordinal);
        Assert.Contains("data:", summary.Shape, StringComparison.Ordinal);
        Assert.Contains("msgs: array", summary.Shape, StringComparison.Ordinal);
        Assert.DoesNotContain("uidFrom", summary.Shape, StringComparison.Ordinal);
    }

    [Fact]
    public void Error_indicator_is_the_numeric_code_and_never_the_message()
    {
        HistoryProbeSummary coded = Private(Json("{\"error_code\":114,\"error_message\":\"FAKE-ERR-TEXT\"}"));
        HistoryProbeSummary text = Private(Json("{\"error_code\":\"7\"}"));
        HistoryProbeSummary nested = Private(Json("{\"data\":{\"error_code\":9}}"));
        HistoryProbeSummary ok = Private(Json("{\"error_code\":0,\"data\":{\"msgs\":[]}}"));
        HistoryProbeSummary none = Private(Json("{\"data\":{\"msgs\":[]}}"));
        HistoryProbeSummary nullCode = Private(Json("{\"error_code\":null}"));
        HistoryProbeSummary garbage = Private(Json("{\"error_code\":\"abc\"}"));
        HistoryProbeSummary tooBig = Private(Json("{\"error_code\":99999999999}"));

        Assert.Equal(114, coded.ErrorCode);
        Assert.True(coded.HasError);
        Assert.Contains("error=114", coded.Format(), StringComparison.Ordinal);
        Assert.DoesNotContain("FAKE-ERR-TEXT", coded.Format() + coded + coded.Shape, StringComparison.Ordinal);
        Assert.Equal(7, text.ErrorCode);
        Assert.Equal(9, nested.ErrorCode);
        Assert.Equal(0, ok.ErrorCode);
        Assert.False(ok.HasError);
        Assert.Contains("error=none", ok.Format(), StringComparison.Ordinal);
        Assert.Null(none.ErrorCode);
        Assert.False(none.HasError);
        Assert.Null(nullCode.ErrorCode);
        Assert.True(garbage.HasError);
        Assert.True(tooBig.HasError);
    }

    [Fact]
    public void No_printed_form_contains_text_names_ids_uids_or_exact_times()
    {
        const string FakeText = "SECRET-TEXT-ZZZ";
        const string FakeName = "Fake Person Zed";
        const string FakeUid = "9990001112223";
        const string FakeMsgId = "7770001112223334445";
        const string FakeCliMsgId = "c7770001112223334445";
        long fakeTs = 1_767_225_600_123;
        string json = "{\"error_code\":0,\"error_message\":\"" + FakeText + "\",\"data\":{\"msgs\":[{\"uidFrom\":\""
            + FakeUid + "\",\"idTo\":\"" + Self + "\",\"msgId\":\"" + FakeMsgId + "\",\"cliMsgId\":\"" + FakeCliMsgId
            + "\",\"msgType\":\"chat.photo\",\"ts\":\"" + fakeTs + "\",\"dName\":\"" + FakeName + "\",\"content\":\""
            + FakeText + "\"}],\"k" + FakeUid + "\":1,\"label\":\"" + FakeText + "\"}}";
        var summary = HistoryProbeSummary.From(
            Json(json), HistoryScope.Private, Self, DateTimeOffset.FromUnixTimeMilliseconds(fakeTs + 86_400_000L * 3));

        var all = new StringBuilder()
            .Append(summary).Append('\n')
            .Append(summary.Format()).Append('\n')
            .Append(summary.Shape).Append('\n')
            .Append(string.Join(',', summary.ExtraFieldNames)).Append('\n')
            .Append(string.Join(',', summary.KindCounts.Select(p => p.Key + p.Value)))
            .ToString();

        Assert.Equal(1, summary.Parsed);
        Assert.Equal(3, summary.OldestDaysAgo);
        Assert.True(summary.TryGetNextLastId(out string next));
        Assert.Equal(FakeMsgId, next);
        foreach (string secret in new[]
        {
            FakeText, FakeName, FakeUid, FakeMsgId, FakeCliMsgId, fakeTs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "9990001112", "77700011", "1767225600",
        })
        {
            Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Format_is_one_line_of_key_value_pairs()
    {
        JsonElement payload = Page(
            "msgs",
            [Msg(Other1, Self, "131", DaysAgoMs(2)), Msg("0", Other1, "132", DaysAgoMs(10))],
            ",\"more\":1");

        string line = Private(payload).Format();

        Assert.Equal(
            "parsed=2 skipped=0 self=1 others=1 threads=1 oldest_days=10 newest_days=2 kinds=webchat:2 "
            + "extra=error_code,more error=none overlap=na continues_older=na",
            line);
        Assert.DoesNotContain('\n', line);
    }

    [Theory]
    [InlineData("123", true)]
    [InlineData("0", true)]
    [InlineData("12345678901234567890", true)]
    [InlineData("123456789012345678901", false)]
    [InlineData("12ab", false)]
    [InlineData("", false)]
    public void Next_id_is_kept_only_when_it_is_one_to_twenty_digits(string msgId, bool usable)
    {
        JsonElement payload = Json(
            "{\"data\":{\"msgs\":[{\"uidFrom\":\"" + Other1 + "\",\"idTo\":\"" + Self + "\",\"msgId\":\"" + msgId
            + "\",\"ts\":\"" + DaysAgoMs(1) + "\"}]}}");

        HistoryProbeSummary summary = Private(payload);

        Assert.Equal(1, summary.Parsed);
        Assert.Equal(usable, summary.TryGetNextLastId(out string id));
        Assert.Equal(usable ? msgId : string.Empty, id);
    }

    [Fact]
    public void Next_id_is_the_id_of_the_oldest_message_by_timestamp_not_by_position()
    {
        HistoryProbeSummary summary = Private(PrivatePage(
            Msg(Other1, Self, "301", DaysAgoMs(1)),
            Msg(Other1, Self, "302", DaysAgoMs(30)),
            Msg(Other1, Self, "303", DaysAgoMs(5))));

        Assert.True(summary.TryGetNextLastId(out string id));
        Assert.Equal("302", id);
    }

    private static HistoryProbeSummary PageOf(HistoryProbeSummary? previous, params (string Id, double Days)[] items) =>
        Private(PrivatePage(items.Select(i => Msg(Other1, Self, i.Id, DaysAgoMs(i.Days))).ToArray()), previous);

    [Fact]
    public void Second_page_with_no_overlap_continues_older()
    {
        HistoryProbeSummary first = PageOf(null, ("1", 1), ("2", 2), ("3", 3));

        HistoryProbeSummary second = PageOf(first, ("4", 4), ("5", 5), ("6", 6));

        Assert.Equal(0, second.OverlapWithPrevious);
        Assert.True(second.ContinuesOlder);
        Assert.Contains("overlap=0 continues_older=yes", second.Format(), StringComparison.Ordinal);
    }

    [Fact]
    public void Second_page_that_repeats_the_first_overlaps_fully_and_does_not_continue_older()
    {
        HistoryProbeSummary first = PageOf(null, ("1", 1), ("2", 2), ("3", 3));

        HistoryProbeSummary second = PageOf(first, ("1", 1), ("2", 2), ("3", 3));

        Assert.Equal(3, second.OverlapWithPrevious);
        Assert.False(second.ContinuesOlder);
        Assert.Contains("overlap=3 continues_older=no", second.Format(), StringComparison.Ordinal);
    }

    [Fact]
    public void Second_page_sharing_the_boundary_message_overlaps_partially_and_still_continues_older()
    {
        HistoryProbeSummary first = PageOf(null, ("1", 1), ("2", 2), ("3", 3));

        HistoryProbeSummary second = PageOf(first, ("3", 3), ("4", 4));

        Assert.Equal(1, second.OverlapWithPrevious);
        Assert.True(second.ContinuesOlder);
    }

    [Fact]
    public void Second_page_that_goes_newer_does_not_continue_older()
    {
        HistoryProbeSummary first = PageOf(null, ("2", 2), ("3", 3));

        HistoryProbeSummary second = PageOf(first, ("9", 0.5), ("8", 1));

        Assert.Equal(0, second.OverlapWithPrevious);
        Assert.False(second.ContinuesOlder);
    }

    [Fact]
    public void Empty_second_page_has_zero_overlap_and_no_verdict_on_direction()
    {
        HistoryProbeSummary first = PageOf(null, ("1", 1), ("2", 2));

        HistoryProbeSummary second = PageOf(first);

        Assert.Equal(0, second.OverlapWithPrevious);
        Assert.Null(second.ContinuesOlder);
        Assert.Contains("overlap=0 continues_older=na", second.Format(), StringComparison.Ordinal);
    }

    [Fact]
    public void Overlap_counts_distinct_ids_once()
    {
        HistoryProbeSummary first = PageOf(null, ("1", 1), ("2", 2));

        HistoryProbeSummary second = PageOf(first, ("1", 1), ("1", 1), ("7", 7));

        Assert.Equal(1, second.OverlapWithPrevious);
    }

    [Fact]
    public void Plan_constants_match_the_agreed_limits()
    {
        Assert.Equal(3, ProbePlan.MaxPagesPerScope);
        Assert.Equal(5, ProbePlan.MinSecondsBetweenRequests);
        Assert.Equal(15, ProbePlan.ResponseTimeoutSeconds);
        Assert.Equal(90, ProbePlan.MaxRunSeconds);
        Assert.Equal(30, ProbePlan.MinMinutesBetweenRuns);
    }

    [Theory]
    [InlineData(1, 20, false, 6.0, 10.0, true)]
    [InlineData(2, 1, false, 5.0, 10.0, true)]
    [InlineData(1, 20, true, 6.0, 10.0, false)]
    [InlineData(1, 0, false, 6.0, 10.0, false)]
    [InlineData(1, -1, false, 6.0, 10.0, false)]
    [InlineData(3, 20, false, 6.0, 10.0, false)]
    [InlineData(4, 20, false, 6.0, 10.0, false)]
    [InlineData(1, 20, false, 6.0, 90.0, false)]
    [InlineData(1, 20, false, 6.0, 89.9, true)]
    [InlineData(1, 20, false, 0.0, 84.0, true)]
    [InlineData(1, 20, false, 0.0, 86.0, false)]
    [InlineData(1, 20, false, -1.0, 84.0, true)]
    [InlineData(1, 20, false, -1.0, 86.0, false)]
    [InlineData(1, 20, false, 6.0, -50.0, true)]
    [InlineData(1, 20, false, double.NaN, 10.0, false)]
    [InlineData(1, 20, false, 6.0, double.NaN, false)]
    [InlineData(1, 20, false, double.PositiveInfinity, 10.0, true)]
    [InlineData(1, 20, false, 6.0, double.PositiveInfinity, false)]
    [InlineData(0, 0, false, 6.0, 10.0, true)]
    public void ShouldRequestNext_covers_every_branch(
        int pagesDone, int lastCount, bool hasError, double sinceLast, double sinceStart, bool expected)
    {
        Assert.Equal(expected, ProbePlan.ShouldRequestNext(pagesDone, lastCount, hasError, sinceLast, sinceStart));
    }

    [Theory]
    [InlineData(0.0, 5.0)]
    [InlineData(2.0, 3.0)]
    [InlineData(5.0, 0.0)]
    [InlineData(60.0, 0.0)]
    [InlineData(-30.0, 5.0)]
    [InlineData(double.NaN, 5.0)]
    [InlineData(double.PositiveInfinity, 0.0)]
    [InlineData(double.NegativeInfinity, 5.0)]
    public void SecondsToWait_is_clamped_between_zero_and_the_minimum_spacing(double sinceLast, double expected)
    {
        Assert.Equal(expected, ProbePlan.SecondsToWait(sinceLast));
    }
}
