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

    /// <summary>The most messages one page may hold (the limit of the store).</summary>
    public const int MaxPageSize = 500;

    private readonly IMessageStore _store;
    private readonly IContactStore _contacts;
    private readonly IUiDispatcher _ui;
    private readonly int _pageSize;
    private readonly CancellationTokenSource _cts = new();

    // Everything below is guarded by _gate. The mirror fields (_cursor, _canLoadOlder) are written in the same UI step that
    // edits Items, so a background operation never reads a state the view has not applied.
    private readonly object _gate = new();
    private int _version;
    private int _epoch;
    private int _olderVersion = -1;
    private ConversationItem? _conversation;
    private bool _openFailed;
    private bool _disposed;
    private bool _canLoadOlder;

    // The key the next older read starts before. It moves when a page is FETCHED (not when it is shown), so a second page
    // can be read while the first one still waits in _pending. _exhausted is set at once when a short page comes back, while
    // CanLoadOlder only changes when the pages are shown. _holding and _pending belong to the hold (see HoldOlderInserts).
    private MessageKey? _cursor;
    private bool _exhausted;
    private bool _holding;
    private readonly List<PendingOlder> _pending = [];

    // A class, not a record: a record would print the message items in its ToString.
    private sealed class PendingOlder(List<MessageItem> items, bool full)
    {
        public List<MessageItem> Items { get; } = items;

        public bool Full { get; } = full;

        public override string ToString() => nameof(PendingOlder);
    }

    // UI-side mirrors, written only by UI steps: the last value of IsLoadingOlder (read by Dispose) and of ReachedStart.
    private bool _loadingOlderShown;
    private bool _reachedStart;

    private int _refreshing;
    private int _dirty;

    public ChatViewModel(IMessageStore store, IContactStore contacts, IUiDispatcher ui, int pageSize = DefaultPageSize)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(contacts);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaxPageSize);
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

    /// <summary>
    /// True from the moment an older page is claimed until it is applied, dropped or failed. Changed only through the
    /// dispatcher, and always reset when the chat is closed, another conversation is opened or the chat is disposed.
    /// </summary>
    [ObservableProperty]
    public partial bool IsLoadingOlder { get; private set; }

    /// <summary>
    /// True when the state is Loaded, something is shown and no older stored message is known, that is, the oldest shown
    /// message is the oldest one saved on this computer. Changed only through the dispatcher.
    /// </summary>
    public bool ReachedStart
    {
        get => _reachedStart;
        private set => SetProperty(ref _reachedStart, value);
    }

    /// <summary>Never prints messages, names or ids.</summary>
    public override string ToString() => nameof(ChatViewModel);

    /// <summary>The page size of every older read while inserts are held (fixed, no adaptive growth).</summary>
    public const int HeldPageSize = 200;

    /// <summary>How many fetched older pages may wait for <see cref="ReleaseOlderInserts"/>; more are not fetched.</summary>
    public const int MaxPendingPages = 2;

    /// <summary>True between <see cref="HoldOlderInserts"/> and <see cref="ReleaseOlderInserts"/>.</summary>
    public bool IsHoldingInserts
    {
        get
        {
            lock (_gate)
            {
                return _holding;
            }
        }
    }

    /// <summary>How many fetched older pages wait to be inserted.</summary>
    public int PendingOlderPages
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>
    /// Call while the user holds the pointer on the list or its scrollbar: older pages are still read, but kept aside (at
    /// most <see cref="MaxPendingPages"/> of <see cref="HeldPageSize"/> rows) instead of being inserted, so the scroll extent
    /// does not change under the user's hand. Safe to call twice. Ended by <see cref="ReleaseOlderInserts"/>, by closing or
    /// opening another conversation, by a reset of the newest page and by <see cref="Dispose"/>.
    /// </summary>
    public void HoldOlderInserts()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _holding = true;
            }
        }
    }

    /// <summary>
    /// Ends the hold and inserts the pages that were kept aside, in order, in one dispatcher step. Does nothing more when
    /// nothing is waiting. Safe to call twice, and when no hold is active.
    /// </summary>
    public void ReleaseOlderInserts()
    {
        PendingOlder[] pages;
        int version;
        int epoch;
        lock (_gate)
        {
            _holding = false;
            if (_disposed || _pending.Count == 0)
            {
                return;
            }

            pages = [.. _pending];
            _pending.Clear();
            version = _version;
            epoch = _epoch;
        }

        Post(version, () => ApplyPending(epoch, pages));
    }

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
            ResetOlderState();
            version = ++_version;
        }

        Post(version, () =>
        {
            Items.Clear();
            State = ChatState.Loading;
            CanLoadOlder = false;
            SetLoadingOlder(false);
            UpdateReachedStart();
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
            ResetOlderState();
            version = ++_version;
        }

        Post(version, () =>
        {
            Items.Clear();
            State = ChatState.Idle;
            CanLoadOlder = false;
            SetLoadingOlder(false);
            UpdateReachedStart();
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
    /// try again. <paramref name="pageSize"/> is how many messages to read (null keeps the page size of the constructor); it
    /// is clamped to 1..<see cref="MaxPageSize"/>, and a page shorter than the clamped size means no older message is left.
    /// </summary>
    public async Task LoadOlderAsync(int? pageSize = null, CancellationToken cancellationToken = default)
    {
        int size = pageSize is { } requested ? Math.Clamp(requested, 1, MaxPageSize) : _pageSize;
        ConversationItem item;
        MessageKey oldest;
        int version;
        int epoch;
        lock (_gate)
        {
            // A call that is refused here changes nothing and posts nothing (it is not a load, so it must not count as one).
            if (_disposed || _conversation is null || !_canLoadOlder || _exhausted || _cursor is not { } known
                || _olderVersion == _version || _pending.Count >= MaxPendingPages)
            {
                return;
            }

            if (_holding)
            {
                size = HeldPageSize;
            }

            item = _conversation;
            oldest = known;
            version = _version;
            epoch = _epoch;
            _olderVersion = version;
        }

        Post(version, () => SetLoadingOlder(true));
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
                    size,
                    includeRecalled: true,
                    includeRaw: false,
                    cancellationToken: token)
                .ConfigureAwait(false);
            List<MessageItem> older = await ToItemsAsync(item.ThreadType, page, token).ConfigureAwait(false);
            bool full = page.Count >= size;
            lock (_gate)
            {
                // Moves at fetch time. Only while the conversation and the list are still the ones the read began for.
                if (version == _version && epoch == _epoch)
                {
                    if (older.Count > 0)
                    {
                        _cursor = older[0].Key;
                    }

                    if (!full)
                    {
                        _exhausted = true;
                    }
                }
            }

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

                // Dropped by the version guard when the chat was closed or another conversation opened meanwhile; those
                // steps and Dispose reset the flag themselves.
                Post(version, () => SetLoadingOlder(false));
            }
        }
    }

    /// <summary>Cancels the work in progress and drops every result that has not been applied yet. Safe to call twice.</summary>
    public void Dispose()
    {
        bool resetFlag;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _version++;
            _holding = false;
            _pending.Clear();
            resetFlag = _loadingOlderShown;
        }

        _cts.Cancel();

        if (resetFlag)
        {
            // Not version guarded on purpose: the version was just bumped. Nothing is posted when no load was shown.
            _ui.Post(() => SetLoadingOlder(false));
        }
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
            UpdateReachedStart();
        }
    }

    // UI step helpers. IsLoadingOlder only changes here, so the mirror Dispose reads always follows the property.
    private void SetLoadingOlder(bool value)
    {
        IsLoadingOlder = value;
        lock (_gate)
        {
            _loadingOlderShown = value;
        }
    }

    private void UpdateReachedStart() =>
        ReachedStart = State == ChatState.Loaded && Items.Count > 0 && !CanLoadOlder;

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
            // Pages that were kept aside belong to the old list, so they go too (and so does the hold).
            lock (_gate)
            {
                _epoch++;
                _holding = false;
                _pending.Clear();
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

        SyncMirror(reset: wasEmpty || plan.IsReset);
        UpdateReachedStart();
    }

    // UI step: the older page has been read. It is shown at once, unless the user holds the pointer: then it is kept aside
    // (the loading flag ends when it is queued) and ReleaseOlderInserts shows it.
    private void ApplyOlder(int version, int epoch, List<MessageItem> older, bool full)
    {
        bool stale;
        bool queued = false;
        lock (_gate)
        {
            if (_olderVersion == version)
            {
                _olderVersion = -1;
            }

            stale = epoch != _epoch;
            if (!stale && _holding)
            {
                _pending.Add(new PendingOlder(older, full));
                queued = true;
            }
        }

        if (stale || queued)
        {
            SetLoadingOlder(false);
            return;
        }

        InsertOlder(older);
        CanLoadOlder = full;
        SyncMirror(reset: false);
        UpdateReachedStart();

        // Last, so a listener that reacts to the flag already sees the inserted items and the new CanLoadOlder.
        SetLoadingOlder(false);
    }

    // UI step of ReleaseOlderInserts: the kept pages, newest first, each inserted at the start, so the oldest ends on top.
    private void ApplyPending(int epoch, PendingOlder[] pages)
    {
        bool stale;
        lock (_gate)
        {
            stale = epoch != _epoch;
        }

        if (stale)
        {
            return;
        }

        foreach (PendingOlder page in pages)
        {
            InsertOlder(page.Items);
        }

        CanLoadOlder = pages[^1].Full;
        SyncMirror(reset: false);
        UpdateReachedStart();
    }

    // Inserts an older page (ascending) before the first shown message; rows that are not older than it are skipped.
    private void InsertOlder(List<MessageItem> older)
    {
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
    }

    // Called with _gate held: the older-page state of a conversation that was just opened or closed.
    private void ResetOlderState()
    {
        _cursor = null;
        _canLoadOlder = false;
        _exhausted = false;
        _holding = false;
        _pending.Clear();
    }

    // Writes the mirrors. After a reset (first rows, or the list was started again) the cursor is the first shown row and the
    // page is fetchable again; otherwise it can only move older, because pages that were fetched but are still kept aside are
    // older than the first shown row and a refresh of the newest page must not move the cursor back to a newer one.
    private void SyncMirror(bool reset)
    {
        lock (_gate)
        {
            MessageKey? first = Items.Count > 0 ? Items[0].Key : null;
            if (reset || _cursor is not { } current || first is not { } shown)
            {
                _cursor = first;
                if (reset)
                {
                    _exhausted = false;
                }
            }
            else if (shown < current)
            {
                _cursor = shown;
            }

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
