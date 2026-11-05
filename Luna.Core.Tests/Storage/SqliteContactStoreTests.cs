using System.Globalization;
using Luna.Core.Storage;
using Luna.Core.Tests.Support;
using Luna.Protocol.Messaging;
using Microsoft.Data.Sqlite;

namespace Luna.Core.Tests.Storage;

/// <summary>Contacts side of <see cref="SqliteMessageStore"/>: schema v2 migration, replace, read, atomicity, redaction.</summary>
public class SqliteContactStoreTests
{
    private const string FakeUserA = "fake-user-1001";
    private const string FakeUserB = "fake-user-1002";
    private const string FakeNameA = "Fake Name Alpha";
    private const string FakeNameB = "Fake Name Beta";

    // An independent copy of the version 1 schema, as an older build created it (messages table and index only).
    private const string V1Schema = """
        CREATE TABLE messages (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            thread_type INTEGER NOT NULL,
            thread_id TEXT NOT NULL,
            dedupe_key TEXT NOT NULL,
            msg_id TEXT NOT NULL,
            cli_msg_id TEXT NOT NULL,
            ts INTEGER NOT NULL,
            is_self INTEGER NOT NULL,
            uid_from TEXT NOT NULL,
            id_to TEXT NOT NULL,
            display_name TEXT NOT NULL,
            msg_type TEXT NOT NULL,
            text TEXT NULL,
            recalled INTEGER NOT NULL DEFAULT 0,
            raw_json TEXT NOT NULL,
            UNIQUE (thread_type, thread_id, dedupe_key)
        );
        CREATE INDEX ix_messages_thread_ts ON messages (thread_type, thread_id, ts DESC, id DESC);
        INSERT INTO messages
            (thread_type, thread_id, dedupe_key, msg_id, cli_msg_id, ts, is_self, uid_from, id_to, display_name, msg_type, text, raw_json)
        VALUES
            (0, 'fake-thread-v1', '7001', '7001', '8001', 1700000000001, 0, 'fake-thread-v1', 'fake-self', 'Fake', 'webchat', 'fake-v1-text', '{}');
        PRAGMA user_version = 1;
        """;

    private static string DbPath(TempFolder folder) => Path.Combine(folder.Path, "history.db");

    private static void RawExec(string path, string sql)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(string path, string sql)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // ---- Migration ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_real_version_1_file_migrates_to_version_2_and_keeps_its_messages()
    {
        using var folder = new TempFolder();
        string path = DbPath(folder);
        RawExec(path, V1Schema);
        Assert.Equal(1, Scalar(path, "PRAGMA user_version"));

        using (var store = new SqliteMessageStore(path))
        {
            IReadOnlyList<StoredMessage> page = await store.GetPageAsync(ThreadType.User, "fake-thread-v1", null, 10);
            StoredMessage message = Assert.Single(page);
            Assert.Equal("fake-v1-text", message.Text);
            Assert.Equal(1700000000001, message.TimestampMs);
            Assert.Empty(await store.GetContactsAsync());
        }

        Assert.Equal(2, Scalar(path, "PRAGMA user_version"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM messages"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'contacts'"));
    }

    [Fact]
    public void A_new_file_is_created_at_version_2_with_both_tables()
    {
        using var folder = new TempFolder();
        string path = DbPath(folder);

        new SqliteMessageStore(path).Dispose();

        Assert.Equal(2, Scalar(path, "PRAGMA user_version"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'contacts'"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'messages'"));
        Assert.Equal(2, SqliteMessageStore.SchemaVersion);
    }

    [Fact]
    public void Version_3_is_refused_before_anything_changes()
    {
        using var folder = new TempFolder();
        string path = DbPath(folder);
        RawExec(path, "PRAGMA user_version = 3");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => new SqliteMessageStore(path));

        Assert.DoesNotContain("history.db", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, Scalar(path, "PRAGMA user_version"));
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'"));
    }

    [Fact]
    public async Task Reopening_a_version_2_file_keeps_the_contacts()
    {
        using var folder = new TempFolder();
        string path = DbPath(folder);
        using (var first = new SqliteMessageStore(path))
        {
            await first.ReplaceContactsAsync([new ContactName(FakeUserA, FakeNameA)]);
        }

        using var second = new SqliteMessageStore(path);
        IReadOnlyDictionary<string, string> contacts = await second.GetContactsAsync();

        Assert.Equal(FakeNameA, Assert.Single(contacts).Value);
        Assert.Equal(FakeUserA, contacts.Keys.Single());
    }

    // ---- Replace and read --------------------------------------------------------------------------------------

    [Fact]
    public async Task Replace_then_read_returns_the_stored_names()
    {
        using var store = new SqliteMessageStore(":memory:");

        await store.ReplaceContactsAsync([new ContactName(FakeUserA, FakeNameA), new ContactName(FakeUserB, FakeNameB)]);
        IReadOnlyDictionary<string, string> contacts = await store.GetContactsAsync();

        Assert.Equal(2, contacts.Count);
        Assert.Equal(FakeNameA, contacts[FakeUserA]);
        Assert.Equal(FakeNameB, contacts[FakeUserB]);
    }

    [Fact]
    public async Task Replace_removes_contacts_that_are_not_in_the_new_set()
    {
        using var store = new SqliteMessageStore(":memory:");
        await store.ReplaceContactsAsync([new ContactName(FakeUserA, FakeNameA), new ContactName(FakeUserB, FakeNameB)]);

        await store.ReplaceContactsAsync([new ContactName(FakeUserB, "Fake Renamed")]);
        IReadOnlyDictionary<string, string> contacts = await store.GetContactsAsync();

        KeyValuePair<string, string> only = Assert.Single(contacts);
        Assert.Equal(FakeUserB, only.Key);
        Assert.Equal("Fake Renamed", only.Value);
    }

    [Fact]
    public async Task Replace_with_an_empty_list_clears_the_cache()
    {
        using var store = new SqliteMessageStore(":memory:");
        await store.ReplaceContactsAsync([new ContactName(FakeUserA, FakeNameA)]);

        await store.ReplaceContactsAsync([]);

        Assert.Empty(await store.GetContactsAsync());
    }

    [Fact]
    public async Task A_later_entry_with_the_same_user_id_wins()
    {
        using var store = new SqliteMessageStore(":memory:");

        await store.ReplaceContactsAsync([new ContactName(FakeUserA, FakeNameA), new ContactName(FakeUserA, FakeNameB)]);

        Assert.Equal(FakeNameB, Assert.Single(await store.GetContactsAsync()).Value);
    }

    [Fact]
    public async Task Replace_is_atomic_when_one_row_is_invalid()
    {
        using var store = new SqliteMessageStore(":memory:");
        await store.ReplaceContactsAsync([new ContactName(FakeUserA, FakeNameA)]);

        // A null name violates NOT NULL: the whole replace must roll back and leave the old set in place.
        ContactName bad = new(FakeUserB, null!);
        SqliteException error = await Assert.ThrowsAsync<SqliteException>(
            () => store.ReplaceContactsAsync([new ContactName("fake-user-1003", "Fake Name Gamma"), bad]));

        Assert.DoesNotContain(FakeUserB, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Fake Name Gamma", error.Message, StringComparison.Ordinal);
        IReadOnlyDictionary<string, string> contacts = await store.GetContactsAsync();
        Assert.Equal(FakeNameA, Assert.Single(contacts).Value);
    }

    [Fact]
    public async Task Replace_does_not_touch_the_messages()
    {
        using var store = new SqliteMessageStore(":memory:");
        await store.SaveAsync([FakeMessages.Text(ThreadType.User, "fake-thread-111", "5001", "6001", "1700000000001")]);

        await store.ReplaceContactsAsync([new ContactName(FakeUserA, FakeNameA)]);
        await store.ReplaceContactsAsync([]);

        ConversationSummary conversation = Assert.Single(await store.ListConversationsAsync(10));
        Assert.Equal(1, conversation.MessageCount);
    }

    [Fact]
    public async Task A_cancelled_token_stops_replace_and_read_without_changing_the_cache()
    {
        using var store = new SqliteMessageStore(":memory:");
        await store.ReplaceContactsAsync([new ContactName(FakeUserA, FakeNameA)]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.ReplaceContactsAsync([new ContactName(FakeUserB, FakeNameB)], cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetContactsAsync(cts.Token));

        Assert.Equal(FakeUserA, Assert.Single(await store.GetContactsAsync()).Key);
    }

    [Fact]
    public async Task A_disposed_store_throws_ObjectDisposedException_for_contacts()
    {
        var store = new SqliteMessageStore(":memory:");
        store.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.GetContactsAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.ReplaceContactsAsync([]));
    }

    [Fact]
    public async Task A_null_list_is_rejected()
    {
        using var store = new SqliteMessageStore(":memory:");

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.ReplaceContactsAsync(null!));
    }

    // ---- Redaction ---------------------------------------------------------------------------------------------

    [Fact]
    public void ContactName_ToString_hides_the_user_id_and_the_name()
    {
        var contact = new ContactName(FakeUserA, FakeNameA);
        Assert.Equal(FakeUserA, contact.UserId);
        Assert.Equal(FakeNameA, contact.Name);

        string text = contact.ToString();

        Assert.Equal(nameof(ContactName), text);
        Assert.DoesNotContain(FakeUserA, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeNameA, text, StringComparison.Ordinal);
    }
}
