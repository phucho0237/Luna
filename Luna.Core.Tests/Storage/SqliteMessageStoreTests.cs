using Luna.Core.Storage;
using Luna.Core.Tests.Support;
using Luna.Protocol.Messaging;
using Microsoft.Data.Sqlite;

namespace Luna.Core.Tests.Storage;

/// <summary>Write side of <see cref="SqliteMessageStore"/>: schema, de-duplication, recall notices, cancellation, lifetime.</summary>
public class SqliteMessageStoreTests
{
    private const string FakeThread = "fake-thread-111";
    private const string BaseTs = "1700000000000";

    private static SqliteMessageStore NewMemoryStore() => new(":memory:");

    private static string DbPath(TempFolder folder) => Path.Combine(folder.Path, "history.db");

    private static SqliteConnection OpenRaw(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static long Scalar(string path, string sql)
    {
        using SqliteConnection connection = OpenRaw(path);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    // ---- Schema ------------------------------------------------------------------------------------------------

    [Fact]
    public void Schema_is_created_at_version_1_and_reopening_is_idempotent()
    {
        using var folder = new TempFolder();
        string path = DbPath(folder);

        new SqliteMessageStore(path).Dispose();
        Assert.Equal(1, Scalar(path, "PRAGMA user_version"));

        new SqliteMessageStore(path).Dispose();
        Assert.Equal(1, Scalar(path, "PRAGMA user_version"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'messages'"));
    }

    [Fact]
    public void A_database_from_a_newer_version_is_rejected_and_the_message_has_no_path()
    {
        using var folder = new TempFolder();
        string path = DbPath(folder);
        using (SqliteConnection raw = OpenRaw(path))
        {
            using SqliteCommand command = raw.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99";
            command.ExecuteNonQuery();
        }

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => new SqliteMessageStore(path));

        Assert.DoesNotContain(folder.Path, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("history.db", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("luna-core-tests", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_file_database_uses_wal_and_its_parent_folder_is_created()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "nested", "deeper", "history.db");

        new SqliteMessageStore(path).Dispose();

        Assert.True(File.Exists(path));
        using SqliteConnection raw = OpenRaw(path);
        using SqliteCommand command = raw.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", command.ExecuteScalar());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_database_path_is_rejected(string path)
    {
        Assert.ThrowsAny<ArgumentException>(() => new SqliteMessageStore(path));
    }

    // ---- Save and de-duplication -------------------------------------------------------------------------------

    [Fact]
    public async Task Saving_three_messages_inserts_them_and_saving_again_reports_duplicates()
    {
        using SqliteMessageStore store = NewMemoryStore();
        ChatMessage[] batch =
        [
            FakeMessages.Text(ThreadType.User, FakeThread, "5001", "6001", "1700000000001"),
            FakeMessages.Text(ThreadType.User, FakeThread, "5002", "6002", "1700000000002"),
            FakeMessages.Text(ThreadType.User, FakeThread, "5003", "6003", "1700000000003"),
        ];

        StoreResult first = await store.SaveAsync(batch);
        StoreResult second = await store.SaveAsync(batch);

        Assert.Equal(new StoreResult(3, 0, 0, 0), first);
        Assert.Equal(new StoreResult(0, 3, 0, 0), second);
    }

    [Fact]
    public async Task The_first_copy_wins_when_the_same_msg_id_arrives_with_another_client_id()
    {
        using SqliteMessageStore store = NewMemoryStore();
        ChatMessage original = FakeMessages.Text(ThreadType.User, FakeThread, "5001", "6001", BaseTs);
        ChatMessage again = FakeMessages.Text(ThreadType.User, FakeThread, "5001", "6999", BaseTs);

        StoreResult result = await store.SaveAsync([original, again]);

        Assert.Equal(new StoreResult(1, 1, 0, 0), result);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("")]
    public async Task A_missing_msg_id_falls_back_to_the_client_id_for_de_duplication(string msgId)
    {
        using SqliteMessageStore store = NewMemoryStore();

        StoreResult distinct = await store.SaveAsync(
        [
            FakeMessages.Text(ThreadType.User, FakeThread, msgId, "6001", BaseTs),
            FakeMessages.Text(ThreadType.User, FakeThread, msgId, "6002", BaseTs),
        ]);
        StoreResult repeated = await store.SaveAsync(
            [FakeMessages.Text(ThreadType.User, FakeThread, msgId, "6001", BaseTs)]);

        Assert.Equal(new StoreResult(2, 0, 0, 0), distinct);
        Assert.Equal(new StoreResult(0, 1, 0, 0), repeated);
    }

    [Fact]
    public async Task The_same_ids_under_another_thread_or_thread_type_are_not_duplicates()
    {
        using SqliteMessageStore store = NewMemoryStore();

        StoreResult result = await store.SaveAsync(
        [
            FakeMessages.Text(ThreadType.User, FakeThread, "5001", "6001", BaseTs),
            FakeMessages.Text(ThreadType.Group, FakeThread, "5001", "6001", BaseTs),
            FakeMessages.Text(ThreadType.User, "fake-thread-222", "5001", "6001", BaseTs),
        ]);

        Assert.Equal(new StoreResult(3, 0, 0, 0), result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1.5")]
    [InlineData("99999999999999999999")]
    public async Task A_message_with_an_unusable_timestamp_is_skipped(string timestamp)
    {
        using SqliteMessageStore store = NewMemoryStore();

        StoreResult result = await store.SaveAsync(
            [FakeMessages.Text(ThreadType.User, FakeThread, "5001", "6001", timestamp)]);

        Assert.Equal(new StoreResult(0, 0, 0, 1), result);
    }

    [Fact]
    public async Task An_empty_batch_is_a_no_op()
    {
        using SqliteMessageStore store = NewMemoryStore();

        StoreResult result = await store.SaveAsync([]);

        Assert.Equal(new StoreResult(0, 0, 0, 0), result);
    }

    [Fact]
    public async Task A_null_batch_is_rejected()
    {
        using SqliteMessageStore store = NewMemoryStore();

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SaveAsync(null!));
    }

    // ---- Recall notices ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_recall_notice_marks_the_target_by_global_id_and_is_never_stored()
    {
        using SqliteMessageStore store = NewMemoryStore();
        await store.SaveAsync([FakeMessages.Text(ThreadType.User, FakeThread, "5001", "6001", BaseTs)]);
        ChatMessage notice = FakeMessages.Undo(ThreadType.User, FakeThread, "5001", "no-such-cli");

        StoreResult result = await store.SaveAsync([notice]);

        Assert.Equal(new StoreResult(0, 0, 1, 0), result);
    }

    [Fact]
    public async Task A_recall_notice_marks_the_target_by_client_id()
    {
        using SqliteMessageStore store = NewMemoryStore();
        await store.SaveAsync([FakeMessages.Text(ThreadType.Group, FakeThread, "5001", "6001", BaseTs)]);
        ChatMessage notice = FakeMessages.Undo(ThreadType.Group, FakeThread, "no-such-global", "6001");

        StoreResult result = await store.SaveAsync([notice]);

        Assert.Equal(new StoreResult(0, 0, 1, 0), result);
    }

    [Fact]
    public async Task An_unmatched_recall_notice_is_skipped_and_is_not_stored()
    {
        using SqliteMessageStore store = NewMemoryStore();
        ChatMessage notice = FakeMessages.Undo(ThreadType.User, FakeThread, "5001", "6001");

        StoreResult first = await store.SaveAsync([notice]);
        // The original arrives afterwards: it is stored normally (the earlier notice is lost, which is documented).
        StoreResult original = await store.SaveAsync([FakeMessages.Text(ThreadType.User, FakeThread, "5001", "6001", BaseTs)]);

        Assert.Equal(new StoreResult(0, 0, 0, 1), first);
        Assert.Equal(new StoreResult(1, 0, 0, 0), original);
    }

    [Fact]
    public async Task A_recall_notice_does_not_cross_threads_or_thread_types()
    {
        using SqliteMessageStore store = NewMemoryStore();
        await store.SaveAsync([FakeMessages.Text(ThreadType.User, FakeThread, "5001", "6001", BaseTs)]);

        StoreResult otherThread = await store.SaveAsync([FakeMessages.Undo(ThreadType.User, "fake-thread-222", "5001", "6001")]);
        StoreResult otherType = await store.SaveAsync([FakeMessages.Undo(ThreadType.Group, FakeThread, "5001", "6001")]);

        Assert.Equal(new StoreResult(0, 0, 0, 1), otherThread);
        Assert.Equal(new StoreResult(0, 0, 0, 1), otherType);
    }

    [Fact]
    public async Task A_recall_notice_with_blank_target_ids_matches_nothing()
    {
        using SqliteMessageStore store = NewMemoryStore();
        await store.SaveAsync([FakeMessages.Text(ThreadType.User, FakeThread, "0", "", BaseTs)]);

        StoreResult result = await store.SaveAsync([FakeMessages.Undo(ThreadType.User, FakeThread, "", "")]);
        StoreResult zero = await store.SaveAsync([FakeMessages.Undo(ThreadType.User, FakeThread, "0", "")]);

        Assert.Equal(new StoreResult(0, 0, 0, 1), result);
        Assert.Equal(new StoreResult(0, 0, 0, 1), zero);
    }

    // ---- Cancellation and lifetime -----------------------------------------------------------------------------

    [Fact]
    public async Task A_cancelled_token_throws_before_saving()
    {
        using SqliteMessageStore store = NewMemoryStore();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.SaveAsync([FakeMessages.Text(ThreadType.User, FakeThread, "5001", "6001", BaseTs)], cts.Token));
    }

    [Fact]
    public async Task Using_the_store_after_Dispose_throws_ObjectDisposedException_without_the_thread_id()
    {
        SqliteMessageStore store = NewMemoryStore();
        store.Dispose();
        store.Dispose();

        ObjectDisposedException error = await Assert.ThrowsAsync<ObjectDisposedException>(
            () => store.SaveAsync([FakeMessages.Text(ThreadType.User, FakeThread, "5001", "6001", BaseTs)]));

        Assert.DoesNotContain(FakeThread, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_database_survives_a_reopen_and_the_folder_can_be_deleted_after_Dispose()
    {
        string folderPath;
        using (var folder = new TempFolder())
        {
            folderPath = folder.Path;
            string path = DbPath(folder);
            ChatMessage message = FakeMessages.Text(ThreadType.User, FakeThread, "5001", "6001", BaseTs);

            using (var first = new SqliteMessageStore(path))
            {
                Assert.Equal(new StoreResult(1, 0, 0, 0), await first.SaveAsync([message]));
            }

            // The reopened database already holds the row, so saving the same message is a duplicate.
            using (var second = new SqliteMessageStore(path))
            {
                Assert.Equal(new StoreResult(0, 1, 0, 0), await second.SaveAsync([message]));
            }

            // With pooling on, the file would stay locked here and the delete would throw on Windows.
            Directory.Delete(folder.Path, recursive: true);
        }

        Assert.False(Directory.Exists(folderPath));
    }

    // ---- Redaction and cursor ----------------------------------------------------------------------------------

    [Fact]
    public void StoredMessage_ToString_hides_text_uid_and_thread_id()
    {
        var stored = new StoredMessage(
            42, ThreadType.Group, FakeThread, "5001", "6001", 1700000000000, false,
            FakeMessages.FakeSender, "fake-id-to", FakeMessages.FakeDisplayName, "webchat",
            FakeMessages.FakeText, true, "{\"content\":\"" + FakeMessages.FakeText + "\"}");
        Assert.Equal(FakeMessages.FakeText, stored.Text);
        Assert.Equal(FakeThread, stored.ThreadId);

        string text = stored.ToString();

        Assert.Equal("StoredMessage { Id = 42, ThreadType = Group, Recalled = True }", text);
        Assert.DoesNotContain(FakeMessages.FakeText, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeMessages.FakeSender, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeThread, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeMessages.FakeDisplayName, text, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversationSummary_ToString_hides_the_thread_id()
    {
        var summary = new ConversationSummary(ThreadType.User, FakeThread, 1700000000000, 7);
        Assert.Equal(FakeThread, summary.ThreadId);

        string text = summary.ToString();

        Assert.Equal("ConversationSummary { ThreadType = User, MessageCount = 7 }", text);
        Assert.DoesNotContain(FakeThread, text, StringComparison.Ordinal);
    }

    [Fact]
    public void StoredMessage_ToCursor_carries_the_timestamp_and_the_row_id()
    {
        var stored = new StoredMessage(
            42, ThreadType.User, FakeThread, "5001", "6001", 1700000000123, true,
            "u", "t", "n", "webchat", null, false, "{}");

        Assert.Equal(new MessageCursor(1700000000123, 42), stored.ToCursor());
    }
}
