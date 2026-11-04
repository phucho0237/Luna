using Luna.Core.Storage;
using Luna.Core.Tests.Support;
using Luna.Protocol.Messaging;
using Microsoft.Data.Sqlite;

namespace Luna.Core.Tests.Storage;

/// <summary>Read side of <see cref="SqliteMessageStore"/> plus the tests that need reads to prove a write.</summary>
public class SqliteMessageStoreQueryTests
{
    private const string FakeThread = "fake-thread-111";
    private const long BaseTs = 1700000000000;

    private static SqliteMessageStore NewMemoryStore() => new(":memory:");

    private static ChatMessage Msg(int index, long timestampMs, ThreadType type = ThreadType.User, string thread = FakeThread) =>
        FakeMessages.Text(
            type,
            thread,
            "5000" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "6000" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            timestampMs.ToString(System.Globalization.CultureInfo.InvariantCulture));

    // ---- Paging ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Paging_by_ten_over_25_messages_with_equal_timestamps_has_no_overlap_and_no_gap()
    {
        using SqliteMessageStore store = NewMemoryStore();
        // 25 messages, five distinct timestamps, five messages each: the row id must break the ties.
        ChatMessage[] batch = Enumerable.Range(0, 25).Select(i => Msg(i, BaseTs + (i / 5))).ToArray();
        await store.SaveAsync(batch);

        var seen = new List<string>();
        var pageSizes = new List<int>();
        MessageCursor? cursor = null;
        while (true)
        {
            IReadOnlyList<StoredMessage> page = await store.GetPageAsync(ThreadType.User, FakeThread, cursor, 10);
            if (page.Count == 0)
            {
                break;
            }

            pageSizes.Add(page.Count);
            seen.AddRange(page.Select(m => m.MsgId));
            cursor = page[^1].ToCursor();
        }

        Assert.Equal([10, 10, 5], pageSizes);
        // Newest first: highest timestamp first, and inside one timestamp the highest row id (the later insert) first.
        string[] expected = Enumerable.Range(0, 25)
            .OrderByDescending(i => i / 5)
            .ThenByDescending(i => i)
            .Select(i => "5000" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        Assert.Equal(expected, seen);
        Assert.Equal(25, seen.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task A_page_maps_every_field_and_keeps_64_bit_ids_intact()
    {
        using SqliteMessageStore store = NewMemoryStore();
        ChatMessage message = FakeMessages.Text(
            ThreadType.Group, FakeThread, "7123456789012345678", "7123456789012345679", "1700000000123",
            text: "fake café text", sender: "fake-sender-1", self: false);
        await store.SaveAsync([message]);

        StoredMessage stored = Assert.Single(await store.GetPageAsync(ThreadType.Group, FakeThread, null, 10));

        Assert.Equal(ThreadType.Group, stored.ThreadType);
        Assert.Equal(FakeThread, stored.ThreadId);
        Assert.Equal("7123456789012345678", stored.MsgId);
        Assert.Equal("7123456789012345679", stored.CliMsgId);
        Assert.Equal(1700000000123, stored.TimestampMs);
        Assert.False(stored.IsSelf);
        Assert.Equal("fake-sender-1", stored.UidFrom);
        Assert.Equal(FakeThread, stored.IdTo);
        Assert.Equal(FakeMessages.FakeDisplayName, stored.DisplayName);
        Assert.Equal("webchat", stored.MsgType);
        Assert.Equal("fake café text", stored.Text);
        Assert.False(stored.Recalled);
        Assert.Equal(message.Raw.GetRawText(), stored.RawJson);
    }

    [Fact]
    public async Task A_self_message_is_flagged_as_self()
    {
        using SqliteMessageStore store = NewMemoryStore();
        await store.SaveAsync([FakeMessages.Text(ThreadType.User, FakeThread, "5001", "6001", "1700000000001", self: true)]);

        StoredMessage stored = Assert.Single(await store.GetPageAsync(ThreadType.User, FakeThread, null, 10));

        Assert.True(stored.IsSelf);
        Assert.NotNull(stored.Text);
    }

    [Fact]
    public async Task Two_threads_and_two_thread_types_with_the_same_ids_stay_separate()
    {
        using SqliteMessageStore store = NewMemoryStore();
        await store.SaveAsync(
        [
            FakeMessages.Text(ThreadType.User, FakeThread, "5001", "6001", "1700000000001", text: "user-a"),
            FakeMessages.Text(ThreadType.Group, FakeThread, "5001", "6001", "1700000000001", text: "group-a"),
            FakeMessages.Text(ThreadType.User, "fake-thread-222", "5001", "6001", "1700000000001", text: "user-b"),
        ]);

        StoredMessage user = Assert.Single(await store.GetPageAsync(ThreadType.User, FakeThread, null, 10));
        StoredMessage group = Assert.Single(await store.GetPageAsync(ThreadType.Group, FakeThread, null, 10));
        StoredMessage other = Assert.Single(await store.GetPageAsync(ThreadType.User, "fake-thread-222", null, 10));

        Assert.Equal("user-a", user.Text);
        Assert.Equal("group-a", group.Text);
        Assert.Equal("user-b", other.Text);
        Assert.Empty(await store.GetPageAsync(ThreadType.User, "fake-thread-missing", null, 10));
    }

    [Fact]
    public async Task A_thread_id_with_quotes_and_sql_text_round_trips_intact()
    {
        using SqliteMessageStore store = NewMemoryStore();
        const string nasty = "x'; DROP TABLE messages; --\" OR '1'='1";
        await store.SaveAsync([FakeMessages.Text(ThreadType.User, nasty, "5001", "6001", "1700000000001")]);

        StoredMessage stored = Assert.Single(await store.GetPageAsync(ThreadType.User, nasty, null, 10));
        ConversationSummary summary = Assert.Single(await store.ListConversationsAsync(10));

        Assert.Equal(nasty, stored.ThreadId);
        Assert.Equal(nasty, summary.ThreadId);
        Assert.Empty(await store.GetPageAsync(ThreadType.User, "x", null, 10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(501)]
    public async Task A_limit_outside_1_to_500_is_rejected(int limit)
    {
        using SqliteMessageStore store = NewMemoryStore();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.GetPageAsync(ThreadType.User, FakeThread, null, limit));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListConversationsAsync(limit));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(500)]
    public async Task The_limit_bounds_1_and_500_are_accepted(int limit)
    {
        using SqliteMessageStore store = NewMemoryStore();

        Assert.Empty(await store.GetPageAsync(ThreadType.User, FakeThread, null, limit));
        Assert.Empty(await store.ListConversationsAsync(limit));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_missing_thread_id_is_rejected(string? threadId)
    {
        using SqliteMessageStore store = NewMemoryStore();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => store.GetPageAsync(ThreadType.User, threadId!, null, 10));
    }

    // ---- Recall ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_recalled_message_is_hidden_by_default_and_shown_without_text_on_request()
    {
        using SqliteMessageStore store = NewMemoryStore();
        await store.SaveAsync(
        [
            FakeMessages.Text(ThreadType.User, FakeThread, "5001", "6001", "1700000000001"),
            FakeMessages.Text(ThreadType.User, FakeThread, "5002", "6002", "1700000000002"),
        ]);
        StoreResult recall = await store.SaveAsync([FakeMessages.Undo(ThreadType.User, FakeThread, "5002", "6002")]);

        IReadOnlyList<StoredMessage> visible = await store.GetPageAsync(ThreadType.User, FakeThread, null, 10);
        IReadOnlyList<StoredMessage> all = await store.GetPageAsync(ThreadType.User, FakeThread, null, 10, includeRecalled: true);

        Assert.Equal(1, recall.Recalled);
        Assert.Equal(["5001"], visible.Select(m => m.MsgId));
        Assert.Equal(["5002", "5001"], all.Select(m => m.MsgId));
        StoredMessage recalled = all[0];
        Assert.True(recalled.Recalled);
        Assert.Null(recalled.Text);
        Assert.False(all[1].Recalled);
        Assert.NotNull(all[1].Text);
    }

    [Fact]
    public async Task A_stored_recall_notice_leaves_no_row_of_its_own()
    {
        using SqliteMessageStore store = NewMemoryStore();
        await store.SaveAsync([FakeMessages.Text(ThreadType.User, FakeThread, "5001", "6001", "1700000000001")]);
        await store.SaveAsync([FakeMessages.Undo(ThreadType.User, FakeThread, "5001", "6001", noticeMsgId: "9000777")]);

        IReadOnlyList<StoredMessage> all = await store.GetPageAsync(ThreadType.User, FakeThread, null, 10, includeRecalled: true);

        Assert.Equal(["5001"], all.Select(m => m.MsgId));
    }

    // ---- Conversations -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Conversations_are_ordered_by_last_activity_with_counts_of_non_recalled_messages()
    {
        using SqliteMessageStore store = NewMemoryStore();
        await store.SaveAsync(
        [
            Msg(1, BaseTs + 10, ThreadType.User, "fake-thread-a"),
            Msg(2, BaseTs + 20, ThreadType.User, "fake-thread-a"),
            Msg(3, BaseTs + 30, ThreadType.Group, "fake-thread-b"),
            Msg(4, BaseTs + 5, ThreadType.User, "fake-thread-c"),
            Msg(5, BaseTs + 6, ThreadType.User, "fake-thread-c"),
            Msg(6, BaseTs + 7, ThreadType.User, "fake-thread-c"),
            // Same thread id as thread-a but another type: a separate conversation.
            Msg(7, BaseTs + 1, ThreadType.Group, "fake-thread-a"),
        ]);
        await store.SaveAsync([FakeMessages.Undo(ThreadType.User, "fake-thread-c", "50006", "60006")]);

        IReadOnlyList<ConversationSummary> list = await store.ListConversationsAsync(10);

        Assert.Equal(
            [
                (ThreadType.Group, "fake-thread-b", BaseTs + 30, 1),
                (ThreadType.User, "fake-thread-a", BaseTs + 20, 2),
                (ThreadType.User, "fake-thread-c", BaseTs + 6, 2),
                (ThreadType.Group, "fake-thread-a", BaseTs + 1, 1),
            ],
            list.Select(c => (c.ThreadType, c.ThreadId, c.LastTimestampMs, c.MessageCount)));
    }

    [Fact]
    public async Task The_conversation_limit_keeps_the_most_recent_ones()
    {
        using SqliteMessageStore store = NewMemoryStore();
        await store.SaveAsync(
        [
            Msg(1, BaseTs + 1, ThreadType.User, "fake-thread-a"),
            Msg(2, BaseTs + 2, ThreadType.User, "fake-thread-b"),
            Msg(3, BaseTs + 3, ThreadType.User, "fake-thread-c"),
        ]);

        IReadOnlyList<ConversationSummary> list = await store.ListConversationsAsync(2);

        Assert.Equal(["fake-thread-c", "fake-thread-b"], list.Select(c => c.ThreadId));
    }

    [Fact]
    public async Task A_conversation_whose_messages_are_all_recalled_is_not_listed()
    {
        using SqliteMessageStore store = NewMemoryStore();
        await store.SaveAsync([FakeMessages.Text(ThreadType.User, FakeThread, "5001", "6001", "1700000000001")]);
        await store.SaveAsync([FakeMessages.Undo(ThreadType.User, FakeThread, "5001", "6001")]);

        Assert.Empty(await store.ListConversationsAsync(10));
    }

    // ---- Atomicity, cancellation, lifetime ---------------------------------------------------------------------

    [Fact]
    public async Task A_batch_with_a_corrupt_message_throws_and_leaves_nothing_behind()
    {
        using SqliteMessageStore store = NewMemoryStore();
        const string goodText = "fake-good-text-marker";
        ChatMessage good = FakeMessages.Text(ThreadType.User, FakeThread, "5001", "6001", "1700000000001", text: goodText);
        ChatMessage bad = FakeMessages.Text(ThreadType.User, FakeThread, "5002", "6002", "1700000000002", text: FakeMessages.FakeText)
            with
        { UidFrom = null! };

        SqliteException error = await Assert.ThrowsAsync<SqliteException>(() => store.SaveAsync([good, bad]));

        // Nothing of the batch is visible, not even the good message that came first.
        Assert.Empty(await store.GetPageAsync(ThreadType.User, FakeThread, null, 10, includeRecalled: true));
        Assert.Empty(await store.ListConversationsAsync(10));
        // SQLite names the table and column only; prove that none of the fake input values leak into the message.
        foreach (string fake in new[] { goodText, FakeMessages.FakeText, FakeMessages.FakeSender, FakeMessages.FakeDisplayName, FakeThread, "5001", "6001" })
        {
            Assert.DoesNotContain(fake, error.Message, StringComparison.Ordinal);
        }

        // The store is still usable and the good message was not half-saved: it is inserted now, not a duplicate.
        Assert.Equal(new StoreResult(1, 0, 0, 0), await store.SaveAsync([good]));
    }

    [Fact]
    public async Task A_pre_cancelled_token_writes_nothing()
    {
        using SqliteMessageStore store = NewMemoryStore();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.SaveAsync([FakeMessages.Text(ThreadType.User, FakeThread, "5001", "6001", "1700000000001")], cts.Token));

        Assert.Empty(await store.ListConversationsAsync(10));
        Assert.Empty(await store.GetPageAsync(ThreadType.User, FakeThread, null, 10, includeRecalled: true));
    }

    [Fact]
    public async Task A_pre_cancelled_token_stops_the_queries()
    {
        using SqliteMessageStore store = NewMemoryStore();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.GetPageAsync(ThreadType.User, FakeThread, null, 10, cancellationToken: cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ListConversationsAsync(10, cts.Token));
    }

    [Fact]
    public async Task The_queries_after_Dispose_throw_ObjectDisposedException_without_the_thread_id()
    {
        SqliteMessageStore store = NewMemoryStore();
        store.Dispose();

        ObjectDisposedException page = await Assert.ThrowsAsync<ObjectDisposedException>(
            () => store.GetPageAsync(ThreadType.User, FakeThread, null, 10));
        ObjectDisposedException list = await Assert.ThrowsAsync<ObjectDisposedException>(() => store.ListConversationsAsync(10));

        Assert.DoesNotContain(FakeThread, page.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeThread, list.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_database_returns_the_saved_messages_after_a_reopen()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "history.db");
        using (var first = new SqliteMessageStore(path))
        {
            await first.SaveAsync([FakeMessages.Text(ThreadType.User, FakeThread, "5001", "6001", "1700000000001")]);
        }

        using var second = new SqliteMessageStore(path);
        StoredMessage stored = Assert.Single(await second.GetPageAsync(ThreadType.User, FakeThread, null, 10));

        Assert.Equal("5001", stored.MsgId);
    }
}
