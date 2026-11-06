using System.Globalization;
using Luna.Protocol.Messaging;
using Microsoft.Data.Sqlite;

namespace Luna.Core.Storage;

/// <summary>
/// SQLite implementation of <see cref="IMessageStore"/>. One long-lived connection, serialized by a semaphore; every
/// <see cref="SaveAsync"/> is one transaction; every statement uses parameters. Data is stored in plain text at rest
/// (decision for T5a: no SQLCipher and no field encryption yet). Message text, uids and thread ids are sensitive:
/// they never appear in exception messages, logs or ToString.
/// </summary>
public sealed class SqliteMessageStore : IMessageStore, IContactStore
{
    /// <summary>The schema version this build creates and understands (PRAGMA user_version).</summary>
    public const int SchemaVersion = 2;

    private const string MemoryPath = ":memory:";

    private const string CreateSchemaSql = """
        CREATE TABLE IF NOT EXISTS messages (
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
        CREATE INDEX IF NOT EXISTS ix_messages_thread_ts ON messages (thread_type, thread_id, ts DESC, id DESC);
        CREATE TABLE IF NOT EXISTS contacts (
            user_id TEXT PRIMARY KEY,
            name TEXT NOT NULL
        );
        """;

    private const string DeleteContactsSql = "DELETE FROM contacts";

    private const string InsertContactSql = """
        INSERT INTO contacts (user_id, name) VALUES ($uid, $name)
        ON CONFLICT (user_id) DO UPDATE SET name = excluded.name
        """;

    private const string ContactsSql = "SELECT user_id, name FROM contacts";

    // ON CONFLICT DO NOTHING (not INSERT OR IGNORE): OR IGNORE would also swallow a NOT NULL violation silently,
    // and a corrupt message must fail the whole batch instead of being counted as a duplicate.
    private const string InsertSql = """
        INSERT INTO messages
            (thread_type, thread_id, dedupe_key, msg_id, cli_msg_id, ts, is_self, uid_from, id_to, display_name, msg_type, text, raw_json)
        VALUES
            ($tt, $tid, $key, $msgId, $cliMsgId, $ts, $self, $from, $to, $name, $type, $text, $raw)
        ON CONFLICT (thread_type, thread_id, dedupe_key) DO NOTHING
        """;

    private const string RecallSql = """
        UPDATE messages SET recalled = 1, text = NULL, raw_json = '{}'
        WHERE thread_type = $tt AND thread_id = $tid
          AND (($global IS NOT NULL AND msg_id = $global) OR ($cli IS NOT NULL AND cli_msg_id = $cli))
        """;

    /// <summary>Largest page or list size a caller may ask for.</summary>
    private const int MaxLimit = 500;

    // Keyset pagination: strictly older than the cursor in (ts, id) order. Two variants so that a NULL cursor is never
    // compared; the row-value comparison lets SQLite seek into ix_messages_thread_ts instead of scanning the thread.
    private const string PageSql = """
        SELECT id, thread_type, thread_id, msg_id, cli_msg_id, ts, is_self, uid_from, id_to, display_name, msg_type, text, recalled, raw_json
        FROM messages
        WHERE thread_type = $tt AND thread_id = $tid
          AND ($all = 1 OR recalled = 0)
        ORDER BY ts DESC, id DESC
        LIMIT $limit
        """;

    private const string PageAfterSql = """
        SELECT id, thread_type, thread_id, msg_id, cli_msg_id, ts, is_self, uid_from, id_to, display_name, msg_type, text, recalled, raw_json
        FROM messages
        WHERE thread_type = $tt AND thread_id = $tid
          AND ($all = 1 OR recalled = 0)
          AND (ts, id) < ($cts, $cid)
        ORDER BY ts DESC, id DESC
        LIMIT $limit
        """;

    // The same two queries without the raw_json column, for views that only show text (it is never read from the file).
    private const string PageNoRawSql = """
        SELECT id, thread_type, thread_id, msg_id, cli_msg_id, ts, is_self, uid_from, id_to, display_name, msg_type, text, recalled
        FROM messages
        WHERE thread_type = $tt AND thread_id = $tid
          AND ($all = 1 OR recalled = 0)
        ORDER BY ts DESC, id DESC
        LIMIT $limit
        """;

    private const string PageAfterNoRawSql = """
        SELECT id, thread_type, thread_id, msg_id, cli_msg_id, ts, is_self, uid_from, id_to, display_name, msg_type, text, recalled
        FROM messages
        WHERE thread_type = $tt AND thread_id = $tid
          AND ($all = 1 OR recalled = 0)
          AND (ts, id) < ($cts, $cid)
        ORDER BY ts DESC, id DESC
        LIMIT $limit
        """;

    private const string ConversationsSql = """
        SELECT thread_type, thread_id, MAX(ts), COUNT(*)
        FROM messages
        WHERE recalled = 0
        GROUP BY thread_type, thread_id
        ORDER BY MAX(ts) DESC, thread_type, thread_id
        LIMIT $limit
        """;

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _disposed;

    /// <summary>Opens (creating when needed) the database at <paramref name="databasePath"/>; ":memory:" is allowed for tests.</summary>
    public SqliteMessageStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        bool inMemory = string.Equals(databasePath, MemoryPath, StringComparison.Ordinal);
        var builder = new SqliteConnectionStringBuilder { Pooling = false };
        if (inMemory)
        {
            builder.DataSource = MemoryPath;
            builder.Mode = SqliteOpenMode.Memory;
        }
        else
        {
            string fullPath = Path.GetFullPath(databasePath);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            builder.DataSource = fullPath;
            builder.Mode = SqliteOpenMode.ReadWriteCreate;
        }

        _connection = new SqliteConnection(builder.ToString());
        try
        {
            _connection.Open();
            Initialize(inMemory);
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    private void Initialize(bool inMemory)
    {
        // Check the version before changing anything in the file.
        long version = Convert.ToInt64(ExecuteScalar("PRAGMA user_version"), CultureInfo.InvariantCulture);
        if (version > SchemaVersion)
        {
            throw new InvalidDataException(
                $"The message database uses schema version {version}, newer than the supported version {SchemaVersion}.");
        }

        ExecuteNonQuery("PRAGMA foreign_keys = ON");
        if (!inMemory)
        {
            ExecuteNonQuery("PRAGMA journal_mode = WAL");
            ExecuteNonQuery("PRAGMA synchronous = NORMAL");
        }

        if (version < SchemaVersion)
        {
            using SqliteTransaction transaction = _connection.BeginTransaction();
            ExecuteNonQuery(CreateSchemaSql, transaction);
            ExecuteNonQuery("PRAGMA user_version = " + SchemaVersion.ToString(CultureInfo.InvariantCulture), transaction);
            transaction.Commit();
        }
    }

    private object? ExecuteScalar(string sql)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private void ExecuteNonQuery(string sql, SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public async Task<StoreResult> SaveAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Off the caller thread: a large batch must never block a UI thread. The gate stays held until it is done.
            return await Task.Run(
                () =>
                {
                    ThrowIfDisposed();
                    cancellationToken.ThrowIfCancellationRequested();
                    return SaveBatch(messages, cancellationToken);
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private StoreResult SaveBatch(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        using SqliteTransaction transaction = _connection.BeginTransaction();
        using SqliteCommand insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = InsertSql;
        SqliteParameter pType = insert.Parameters.Add("$tt", SqliteType.Integer);
        SqliteParameter pThread = insert.Parameters.Add("$tid", SqliteType.Text);
        SqliteParameter pKey = insert.Parameters.Add("$key", SqliteType.Text);
        SqliteParameter pMsgId = insert.Parameters.Add("$msgId", SqliteType.Text);
        SqliteParameter pCliMsgId = insert.Parameters.Add("$cliMsgId", SqliteType.Text);
        SqliteParameter pTs = insert.Parameters.Add("$ts", SqliteType.Integer);
        SqliteParameter pSelf = insert.Parameters.Add("$self", SqliteType.Integer);
        SqliteParameter pFrom = insert.Parameters.Add("$from", SqliteType.Text);
        SqliteParameter pTo = insert.Parameters.Add("$to", SqliteType.Text);
        SqliteParameter pName = insert.Parameters.Add("$name", SqliteType.Text);
        SqliteParameter pMsgType = insert.Parameters.Add("$type", SqliteType.Text);
        SqliteParameter pText = insert.Parameters.Add("$text", SqliteType.Text);
        SqliteParameter pRaw = insert.Parameters.Add("$raw", SqliteType.Text);

        using SqliteCommand recall = _connection.CreateCommand();
        recall.Transaction = transaction;
        recall.CommandText = RecallSql;
        SqliteParameter rType = recall.Parameters.Add("$tt", SqliteType.Integer);
        SqliteParameter rThread = recall.Parameters.Add("$tid", SqliteType.Text);
        SqliteParameter rGlobal = recall.Parameters.Add("$global", SqliteType.Text);
        SqliteParameter rCli = recall.Parameters.Add("$cli", SqliteType.Text);

        int inserted = 0;
        int duplicates = 0;
        int recalled = 0;
        int skipped = 0;

        foreach (ChatMessage? message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (message is null)
            {
                skipped++;
                continue;
            }

            if (message.IsUndo)
            {
                // A recall notice is never stored. It marks the target in the same thread. Limitation: a notice that
                // arrives before the original message is lost. The matching rule is [Source]-level, not verified live.
                string? globalId = message.Content is UndoContent undo ? UsableId(undo.GlobalMsgId) : null;
                string? cliId = message.Content is UndoContent undoCli ? UsableId(undoCli.CliMsgId) : null;
                if (globalId is null && cliId is null)
                {
                    skipped++;
                    continue;
                }

                rType.Value = (int)message.ThreadType;
                rThread.Value = Db(message.ThreadId);
                rGlobal.Value = Db(globalId);
                rCli.Value = Db(cliId);
                int affected = recall.ExecuteNonQuery();
                if (affected > 0)
                {
                    recalled += affected;
                }
                else
                {
                    skipped++;
                }

                continue;
            }

            if (!long.TryParse(message.Timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out long timestampMs)
                || timestampMs <= 0
                || message.Raw.ValueKind == System.Text.Json.JsonValueKind.Undefined)
            {
                skipped++;
                continue;
            }

            // A message with neither a global nor a client id has no identity: it cannot be de-duplicated or recalled.
            string? key = DedupeKey(message.MsgId, message.CliMsgId);
            if (key is null)
            {
                skipped++;
                continue;
            }

            pType.Value = (int)message.ThreadType;
            pThread.Value = Db(message.ThreadId);
            pKey.Value = key;
            pMsgId.Value = Db(message.MsgId);
            pCliMsgId.Value = Db(message.CliMsgId);
            pTs.Value = timestampMs;
            pSelf.Value = message.IsSelf ? 1 : 0;
            pFrom.Value = Db(message.UidFrom);
            pTo.Value = Db(message.IdTo);
            pName.Value = Db(message.DisplayName);
            pMsgType.Value = Db(message.MsgType);
            pText.Value = Db(message.Text);
            pRaw.Value = message.Raw.GetRawText();

            if (insert.ExecuteNonQuery() > 0)
            {
                inserted++;
            }
            else
            {
                duplicates++;
            }
        }

        transaction.Commit();
        return new StoreResult(inserted, duplicates, recalled, skipped);
    }

    /// <summary>
    /// The global message id when it is usable, otherwise the client id prefixed with "c:", or null when neither id is
    /// usable (the message has no identity).
    /// </summary>
    private static string? DedupeKey(string? msgId, string? cliMsgId)
    {
        string? global = UsableId(msgId);
        if (global is not null)
        {
            return global;
        }

        string? client = UsableId(cliMsgId);
        return client is null ? null : "c:" + client;
    }

    /// <summary>Null for an empty id or "0" (the server uses 0 for "no id"), otherwise the id itself.</summary>
    private static string? UsableId(string? id) =>
        string.IsNullOrEmpty(id) || string.Equals(id, "0", StringComparison.Ordinal) ? null : id;

    private static object Db(string? value) => value is null ? DBNull.Value : value;

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredMessage>> GetPageAsync(
        ThreadType threadType,
        string threadId,
        MessageCursor? before,
        int limit,
        bool includeRecalled = false,
        bool includeRaw = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        CheckLimit(limit);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () =>
                {
                    ThrowIfDisposed();
                    cancellationToken.ThrowIfCancellationRequested();
                    return ReadPage(threadType, threadId, before, limit, includeRecalled, includeRaw);
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private List<StoredMessage> ReadPage(
        ThreadType threadType, string threadId, MessageCursor? before, int limit, bool includeRecalled, bool includeRaw)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = (before is null, includeRaw) switch
        {
            (true, true) => PageSql,
            (false, true) => PageAfterSql,
            (true, false) => PageNoRawSql,
            (false, false) => PageAfterNoRawSql,
        };
        command.Parameters.Add("$tt", SqliteType.Integer).Value = (int)threadType;
        command.Parameters.Add("$tid", SqliteType.Text).Value = threadId;
        command.Parameters.Add("$all", SqliteType.Integer).Value = includeRecalled ? 1 : 0;
        if (before is not null)
        {
            command.Parameters.Add("$cts", SqliteType.Integer).Value = before.TimestampMs;
            command.Parameters.Add("$cid", SqliteType.Integer).Value = before.Id;
        }

        command.Parameters.Add("$limit", SqliteType.Integer).Value = limit;

        var page = new List<StoredMessage>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            page.Add(new StoredMessage(
                reader.GetInt64(0),
                (ThreadType)reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetInt32(6) != 0,
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.GetInt32(12) != 0,
                includeRaw ? reader.GetString(13) : string.Empty));
        }

        return page;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConversationSummary>> ListConversationsAsync(int limit, CancellationToken cancellationToken = default)
    {
        CheckLimit(limit);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () =>
                {
                    ThrowIfDisposed();
                    cancellationToken.ThrowIfCancellationRequested();
                    return ReadConversations(limit);
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private List<ConversationSummary> ReadConversations(int limit)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = ConversationsSql;
        command.Parameters.Add("$limit", SqliteType.Integer).Value = limit;

        var conversations = new List<ConversationSummary>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            conversations.Add(new ConversationSummary(
                (ThreadType)reader.GetInt32(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt32(3)));
        }

        return conversations;
    }

    /// <inheritdoc />
    public async Task ReplaceContactsAsync(IReadOnlyList<ContactName> contacts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contacts);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(
                () =>
                {
                    ThrowIfDisposed();
                    cancellationToken.ThrowIfCancellationRequested();
                    ReplaceContacts(contacts, cancellationToken);
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ReplaceContacts(IReadOnlyList<ContactName> contacts, CancellationToken cancellationToken)
    {
        // Disposing the transaction without Commit rolls everything back, including the DELETE.
        using SqliteTransaction transaction = _connection.BeginTransaction();
        ExecuteNonQuery(DeleteContactsSql, transaction);

        using SqliteCommand insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = InsertContactSql;
        SqliteParameter pUser = insert.Parameters.Add("$uid", SqliteType.Text);
        SqliteParameter pName = insert.Parameters.Add("$name", SqliteType.Text);

        foreach (ContactName? contact in contacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (contact is null)
            {
                continue;
            }

            pUser.Value = Db(contact.UserId);
            pName.Value = Db(contact.Name);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> GetContactsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () =>
                {
                    ThrowIfDisposed();
                    cancellationToken.ThrowIfCancellationRequested();
                    return ReadContacts();
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Dictionary<string, string> ReadContacts()
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = ContactsSql;

        var contacts = new Dictionary<string, string>(StringComparer.Ordinal);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            contacts[reader.GetString(0)] = reader.GetString(1);
        }

        return contacts;
    }

    private static void CheckLimit(int limit)
    {
        if (limit is < 1 or > MaxLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "The limit must be between 1 and 500.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>Waits for a running operation, then closes the connection. Safe to call more than once.</summary>
    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _connection.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }
}
