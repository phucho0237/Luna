using System.Collections;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Luna.Core.Storage;
using Luna.Core.ViewModels;
using Luna.Protocol.Messaging;

namespace Luna.Core.Chat;

/// <summary>
/// The messages of the open conversation, read from the local store only (no server call, no sending). It shows what is
/// stored, newest at the bottom. Every change of <see cref="Items"/>, <see cref="State"/> and <see cref="CanLoadOlder"/>
/// goes through <see cref="IUiDispatcher.Post"/> as one step. <see cref="Items"/> is the SAME collection for the whole
/// life of the view model and is only edited with Replace, Insert and Add (plus a Clear when another conversation is
/// opened or after a gap that cannot be merged), so a bound list keeps its scroll position. Every result carries the
/// version of the conversation it was read for and is dropped when another conversation was opened, or the chat was
/// closed or disposed, meanwhile. Nothing here throws for storage problems and no exception is kept: it could carry
/// message content, so a failure only sets <see cref="ChatState.Error"/>.
/// </summary>
public sealed partial class ChatViewModel : ObservableObject, IDisposable
{
    /// <summary>How many messages one page holds (the store allows at most 500).</summary>
    public const int DefaultPageSize = 50;

    private readonly IMessageStore _store;
    private readonly IContactStore _contacts;
    private readonly IUiDispatcher _ui;
    private readonly int _pageSize;
    private readonly CancellationTokenSource _cts = new();

    // Everything below is guarded by _gate. The mirror fields (_oldest, _canLoadOlder) are written in the same UI step that
    // edits Items, so a background operation never reads a state the view has not applied.
    private readonly object _gate = new();
    private int _version;
    private int _epoch;
    private int _olderVersion = -1;
    private ConversationItem? _conversation;
    private bool _openFailed;
    private bool _disposed;
    private MessageKey? _oldest;
    private bool _canLoadOlder;

    private int _refreshing;
    private int _dirty;

    public ChatViewModel(IMessageStore store, IContactStore contacts, IUiDispatcher ui, int pageSize = DefaultPageSize)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(contacts);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 500);
        _store = store;
        _contacts = contacts;
        _ui = ui;
        _pageSize = pageSize;
    }

    /// <summary>
    /// The shown messages, oldest first. One instance for the whole life of the view model, edited in place. Older pages
    /// loaded with <see cref="LoadOlderAsync"/> stay in memory until the conversation is closed or another one is opened
    /// (a trimming policy is T7c-2).
    /// </summary>
    public ObservableCollection<MessageItem> Items { get; } = [];

    /// <summary>What the pane shows for the open conversation.</summary>
    [ObservableProperty]
    public partial ChatState State { get; private set; }

    /// <summary>True while the last page that was read came back full, so there may be older stored messages.</summary>
    [ObservableProperty]
    public partial bool CanLoadOlder { get; private set; }

    /// <summary>Never prints messages, names or ids.</summary>
    public override string ToString() => nameof(ChatViewModel);

    /// <summary>
    /// Opens a conversation: the previous one is cleared and the newest page of this one is read. Opening the conversation
    /// that is already open (same thread type and id) does nothing, unless its first load failed, which retries it. Never
    /// throws for storage problems.
    /// </summary>
    public async Task OpenAsync(ConversationItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        int version;
        lock (_gate)
        {
            if (_disposed || (_conversation is not null && SameConversation(_conversation, item) && !_openFailed))
            {
                return;
            }

            _conversation = item;
            _openFailed = false;
            _oldest = null;
            _canLoadOlder = false;
            version = ++_version;
        }

        Post(version, () =>
        {
            Items.Clear();
            State = ChatState.Loading;
            CanLoadOlder = false;
        });

        await LoadNewestAsync(version, item, initial: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes the open conversation: the items are cleared and the state is <see cref="ChatState.Idle"/>.</summary>
    public void Close()
    {
        int version;
        lock (_gate)
        {
            if (_disposed || _conversation is null)
            {
                return;
            }

            _conversation = null;
            _openFailed = false;
            _oldest = null;
            _canLoadOlder = false;
            version = ++_version;
        }

        Post(version, () =>
        {
            Items.Clear();
            State = ChatState.Idle;
            CanLoadOlder = false;
        });
    }

    /// <summary>
    /// Reads the newest page again and merges it into <see cref="Items"/> in place (new messages are inserted at their sorted
    /// place, recalled ones are replaced where they are). Single flight: calls that arrive while a pass runs are folded into
    /// one more pass of the running call and return at once. Does nothing when no conversation is open. Never throws.
    /// </summary>
    public async Task RefreshLatestAsync(CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _dirty, 1);
        while (true)
        {
            if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
            {
                return;
            }

            try
            {
                while (Interlocked.Exchange(ref _dirty, 0) == 1)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    ConversationItem? item;
                    int version;
                    lock (_gate)
                    {
                        if (_disposed)
                        {
                            return;
                        }

                        item = _conversation;
                        version = _version;
                    }

                    if (item is not null)
                    {
                        await LoadNewestAsync(version, item, initial: false, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception)
            {
                // LoadNewestAsync does not throw; this only keeps an unexpected error out of the caller.
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }

            // A request that arrived between the last dirty check and the flag reset must not be lost.
            if (Volatile.Read(ref _dirty) == 0 || IsDisposed())
            {
                return;
            }
        }
    }

    /// <summary>
    /// Reads the page right before the oldest shown message and inserts it at the start of <see cref="Items"/>. Single
    /// flight, and only while <see cref="CanLoadOlder"/> is true. Never throws; a failure changes nothing, so the user can
    /// try again.
    /// </summary>
    public async Task LoadOlderAsync(CancellationToken cancellationToken = default)
    {
        ConversationItem item;
        MessageKey oldest;
        int version;
        int epoch;
        lock (_gate)
        {
            if (_disposed || _conversation is null || !_canLoadOlder || _oldest is not { } known || _olderVersion == _version)
            {
                return;
            }

            item = _conversation;
            oldest = known;
            version = _version;
            epoch = _epoch;
            _olderVersion = version;
        }

        bool posted = false;
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
            CancellationToken token = linked.Token;
            IReadOnlyList<StoredMessage> page = await _store
                .GetPageAsync(
                    item.ThreadType,
                    item.ThreadId,
                    new MessageCursor(oldest.TimestampMs, oldest.Id),
                    _pageSize,
                    includeRecalled: true,
                    includeRaw: false,
                    cancellationToken: token)
                .ConfigureAwait(false);
            List<MessageItem> older = await ToItemsAsync(item.ThreadType, page, token).ConfigureAwait(false);
            bool full = page.Count >= _pageSize;
            posted = true;
            Post(version, () => ApplyOlder(version, epoch, older, full));
        }
        catch (Exception)
        {
            // Cancelled, disposed or a storage failure: nothing changes. The exception is dropped on purpose.
        }
        finally
        {
            if (!posted)
            {
                lock (_gate)
                {
                    if (_olderVersion == version)
                    {
                        _olderVersion = -1;
                    }
                }
            }
        }
    }

    /// <summary>Cancels the work in progress and drops every result that has not been applied yet. Safe to call twice.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _version++;
        }

        _cts.Cancel();
    }

    private bool IsDisposed()
    {
        lock (_gate)
        {
            return _disposed;
        }
    }

    private static bool SameConversation(ConversationItem a, ConversationItem b) =>
        a.ThreadType == b.ThreadType && string.Equals(a.ThreadId, b.ThreadId, StringComparison.Ordinal);

    // One dispatcher step; dropped when the conversation changed (or the chat was closed or disposed) since the read began.
    private void Post(int version, Action apply) =>
        _ui.Post(() =>
        {
            if (version == Volatile.Read(ref _version))
            {
                apply();
            }
        });

    private async Task LoadNewestAsync(int version, ConversationItem item, bool initial, CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
            CancellationToken token = linked.Token;
            IReadOnlyList<StoredMessage> page = await _store
                .GetPageAsync(
                    item.ThreadType,
                    item.ThreadId,
                    null,
                    _pageSize,
                    includeRecalled: true,
                    includeRaw: false,
                    cancellationToken: token)
                .ConfigureAwait(false);
            List<MessageItem> items = await ToItemsAsync(item.ThreadType, page, token).ConfigureAwait(false);
            bool full = page.Count >= _pageSize;
            Post(version, () => ApplyNewest(items, full));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _cts.IsCancellationRequested)
        {
            // The caller cancelled or the chat was disposed: nothing to show.
        }
        catch (Exception)
        {
            // The exception is dropped on purpose: it could carry file paths or message content.
            if (initial)
            {
                lock (_gate)
                {
                    if (version == _version)
                    {
                        _openFailed = true;
                    }
                }
            }

            Post(version, OnLoadFailed);
        }
    }

    // UI step. A failure only matters while there is nothing to show: shown messages stay when a refresh fails.
    private void OnLoadFailed()
    {
        if (Items.Count == 0 && State != ChatState.Empty)
        {
            State = ChatState.Error;
            CanLoadOlder = false;
        }
    }

    // UI step: merges the newest page (ascending) into Items in place, using the plan of ChatMerge.
    private void ApplyNewest(List<MessageItem> page, bool pageFull)
    {
        bool wasEmpty = Items.Count == 0;
        var current = new ItemKeys(Items);

        var pageKeys = new MessageKey[page.Count];
        var byKey = new Dictionary<MessageKey, MessageItem>(page.Count);
        var changed = new HashSet<MessageKey>();
        for (int i = 0; i < page.Count; i++)
        {
            MessageItem item = page[i];
            MessageKey key = item.Key;
            pageKeys[i] = key;
            byKey[key] = item;

            int at = ChatMerge.LowerBound(current, key);
            if (at < Items.Count && current[at] == key && !Items[at].HasSameContentAs(item))
            {
                changed.Add(key);
            }
        }

        ChatMergePlan plan = ChatMerge.Plan(current, pageKeys, changed);
        if (plan.IsReset)
        {
            // A gap that cannot be merged (more new messages than one page): start again from this page, in the same collection.
            lock (_gate)
            {
                _epoch++;
            }

            Items.Clear();
            foreach (MessageItem item in page)
            {
                Items.Add(item);
            }
        }
        else
        {
            foreach (ChatMergeOperation operation in plan.Operations)
            {
                if (operation.Kind == ChatMergeKind.Replace)
                {
                    Items[operation.Index] = byKey[operation.Key];
                }
                else
                {
                    Items.Insert(operation.Index, byKey[operation.Key]);
                }
            }
        }

        State = Items.Count == 0 ? ChatState.Empty : ChatState.Loaded;
        if (wasEmpty || plan.IsReset)
        {
            CanLoadOlder = pageFull;
        }

        lock (_gate)
        {
            _openFailed = false;
        }

        SyncMirror();
    }

    // UI step: inserts an older page (ascending) before the first shown message.
    private void ApplyOlder(int version, int epoch, List<MessageItem> older, bool full)
    {
        bool stale;
        lock (_gate)
        {
            if (_olderVersion == version)
            {
                _olderVersion = -1;
            }

            stale = epoch != _epoch;
        }

        if (stale)
        {
            return;
        }

        MessageKey? first = Items.Count > 0 ? Items[0].Key : null;
        int at = 0;
        foreach (MessageItem item in older)
        {
            if (first is { } limit && item.Key >= limit)
            {
                continue;
            }

            Items.Insert(at++, item);
        }

        CanLoadOlder = full;
        SyncMirror();
    }

    private void SyncMirror()
    {
        lock (_gate)
        {
            _oldest = Items.Count > 0 ? Items[0].Key : null;
            _canLoadOlder = CanLoadOlder;
        }
    }

    // Maps a page (newest first, as the store returns it) to items in ascending order. Names are only looked up for a group
    // that has a row without a message name, and a failure of that lookup only loses the fallback names.
    private async Task<List<MessageItem>> ToItemsAsync(ThreadType threadType, IReadOnlyList<StoredMessage> page, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string>? names = null;
        if (threadType == ThreadType.Group && page.Any(m => string.IsNullOrWhiteSpace(m.DisplayName)))
        {
            names = await ReadNamesAsync(cancellationToken).ConfigureAwait(false);
        }

        var items = new List<MessageItem>(page.Count);
        for (int i = page.Count - 1; i >= 0; i--)
        {
            items.Add(ToItem(threadType, page[i], names));
        }

        return items;
    }

    private static MessageItem ToItem(ThreadType threadType, StoredMessage message, IReadOnlyDictionary<string, string>? names)
    {
        string? senderName = null;
        if (threadType == ThreadType.Group)
        {
            if (!string.IsNullOrWhiteSpace(message.DisplayName))
            {
                senderName = message.DisplayName.Trim();
            }
            else if (names is not null && names.TryGetValue(message.UidFrom, out string? known) && !string.IsNullOrWhiteSpace(known))
            {
                senderName = known.Trim();
            }
        }

        MessageKind kind = MessageKinds.Classify(message.MsgType, message.Recalled, !string.IsNullOrEmpty(message.Text));
        return new MessageItem(
            message.Id,
            message.IsSelf,
            senderName,
            ChatIds.ShortId(message.UidFrom),
            message.TimestampMs,
            kind,
            kind == MessageKind.Text ? message.Text : null);
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadNamesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _contacts.GetContactsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new Dictionary<string, string>();
        }
    }

    // A read-only view of the keys of Items, so the merge needs no copy of a list that can hold tens of thousands of rows.
    private sealed class ItemKeys(ObservableCollection<MessageItem> items) : IReadOnlyList<MessageKey>
    {
        public int Count => items.Count;

        public MessageKey this[int index] => items[index].Key;

        public IEnumerator<MessageKey> GetEnumerator()
        {
            for (int i = 0; i < items.Count; i++)
            {
                yield return items[i].Key;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
