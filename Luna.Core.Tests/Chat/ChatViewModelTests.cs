using System.Collections.Specialized;
using System.Globalization;
using Luna.Core.Chat;
using Luna.Core.Storage;
using Luna.Core.Tests.Support;
using Luna.Core.ViewModels;
using Luna.Protocol.Messaging;

namespace Luna.Core.Tests.Chat;

/// <summary>
/// The chat view model against a real in-memory store and a queueing dispatcher: nothing changes on the view side until
/// the test runs the queue. Gates are asynchronous (no thread waits for another), so the tests also pass with two cores.
/// </summary>
public sealed class ChatViewModelTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private const string ThreadA = "fake-thread-aaa";
    private const string ThreadB = "fake-thread-bbb";
    private const string GroupThread = "fake-group-ccc";

    private readonly SqliteMessageStore _inner = new(":memory:");
    private readonly GatedMessageStore _store;
    private readonly QueueingDispatcher _ui = new();
    private readonly List<ChatViewModel> _created = [];

    public ChatViewModelTests()
    {
        _store = new GatedMessageStore(_inner);
    }

    public void Dispose()
    {
        foreach (ChatViewModel vm in _created)
        {
            vm.Dispose();
        }

        _inner.Dispose();
    }

    private ChatViewModel Create(int pageSize = ChatViewModel.DefaultPageSize, IContactStore? contacts = null)
    {
        var vm = new ChatViewModel(_store, contacts ?? _inner, _ui, pageSize);
        _created.Add(vm);
        return vm;
    }

    private static ConversationItem Conv(string threadId, ThreadType type = ThreadType.User) => new(type, threadId, null, 0, 0);

    private static string N(long n) => n.ToString(CultureInfo.InvariantCulture);

    // Message number n has msgId 7000n, text "fake-text-n" and the given timestamp.
    private static ChatMessage Msg(string thread, int n, long ts, ThreadType type = ThreadType.User, string sender = FakeMessages.FakeSender,
        bool self = false, string displayName = FakeMessages.FakeDisplayName) =>
        FakeMessages.Text(type, thread, "7000" + N(n), "8000" + N(n), N(ts), "fake-text-" + N(n), sender, self, displayName);

    private async Task Save(params ChatMessage[] messages) => await _inner.SaveAsync(messages);

    // Messages 1..count in one thread with timestamps 100 * n.
    private async Task SaveRange(string thread, int from, int to, ThreadType type = ThreadType.User)
    {
        await Save(Enumerable.Range(from, to - from + 1).Select(n => Msg(thread, n, 100L * n, type)).ToArray());
    }

    private static string[] Texts(ChatViewModel vm) => [.. vm.Items.Select(i => i.Text ?? "<none>")];

    private static string[] Expected(params int[] numbers) => [.. numbers.Select(n => "fake-text-" + N(n))];

    private async Task OpenAndShow(ChatViewModel vm, ConversationItem item)
    {
        await vm.OpenAsync(item).WaitAsync(Patience);
        _ui.RunAll();
    }

    private async Task RefreshAndShow(ChatViewModel vm)
    {
        await vm.RefreshLatestAsync().WaitAsync(Patience);
        _ui.RunAll();
    }

    // ---- Open ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Open_shows_the_newest_page_ascending_and_only_through_the_dispatcher()
    {
        await Save(Msg(ThreadA, 1, 300), Msg(ThreadA, 2, 100), Msg(ThreadA, 3, 200));
        ChatViewModel vm = Create();

        await vm.OpenAsync(Conv(ThreadA)).WaitAsync(Patience);

        Assert.Empty(vm.Items);
        Assert.Equal(ChatState.Idle, vm.State);
        Assert.True(_ui.Pending > 0);

        _ui.RunAll();

        Assert.Equal(Expected(2, 3, 1), Texts(vm));
        Assert.Equal(ChatState.Loaded, vm.State);
        Assert.False(vm.CanLoadOlder);
    }

    [Fact]
    public async Task Open_asks_the_store_for_recalled_rows_without_raw_json_and_one_page()
    {
        await SaveRange(ThreadA, 1, 2);
        ChatViewModel vm = Create(pageSize: 7);

        await OpenAndShow(vm, Conv(ThreadA));

        PageCall call = Assert.Single(_store.Calls);
        Assert.Null(call.Before);
        Assert.Equal(7, call.Limit);
        Assert.True(call.IncludeRecalled);
        Assert.False(call.IncludeRaw);
    }

    [Fact]
    public async Task A_full_first_page_allows_loading_older_messages()
    {
        await SaveRange(ThreadA, 1, 5);
        ChatViewModel vm = Create(pageSize: 3);

        await OpenAndShow(vm, Conv(ThreadA));

        Assert.Equal(Expected(3, 4, 5), Texts(vm));
        Assert.True(vm.CanLoadOlder);
    }

    [Fact]
    public async Task An_empty_conversation_is_Empty_and_a_message_that_arrives_later_makes_it_Loaded()
    {
        ChatViewModel vm = Create();

        await OpenAndShow(vm, Conv(ThreadA));

        Assert.Equal(ChatState.Empty, vm.State);
        Assert.Empty(vm.Items);
        Assert.False(vm.CanLoadOlder);

        await SaveRange(ThreadA, 1, 1);
        await RefreshAndShow(vm);

        Assert.Equal(ChatState.Loaded, vm.State);
        Assert.Equal(Expected(1), Texts(vm));
    }

    [Fact]
    public async Task Opening_marks_the_view_as_Loading_and_clears_the_previous_conversation()
    {
        await SaveRange(ThreadA, 1, 2);
        await SaveRange(ThreadB, 3, 3);
        ChatViewModel vm = Create();
        await OpenAndShow(vm, Conv(ThreadA));
        TaskCompletionSource gate = _store.GateNextPage();

        Task open = vm.OpenAsync(Conv(ThreadB));
        await _store.WhenPageCalls(2).WaitAsync(Patience);
        _ui.RunAll();

        Assert.Equal(ChatState.Loading, vm.State);
        Assert.Empty(vm.Items);

        gate.SetResult();
        await open.WaitAsync(Patience);
        _ui.RunAll();

        Assert.Equal(Expected(3), Texts(vm));
    }

    // ---- Same conversation, switching, closing -----------------------------------------------------------------------

    [Fact]
    public async Task Opening_the_open_conversation_again_is_a_cheap_no_op()
    {
        await SaveRange(ThreadA, 1, 3);
        ChatViewModel vm = Create();
        await OpenAndShow(vm, Conv(ThreadA));
        MessageItem[] before = [.. vm.Items];
        var changes = new List<NotifyCollectionChangedAction>();
        vm.Items.CollectionChanged += (_, e) => changes.Add(e.Action);

        await OpenAndShow(vm, Conv(ThreadA) with { Name = "A different name", MessageCount = 99 });

        Assert.Equal(1, _store.PageCalls);
        Assert.Empty(changes);
        Assert.Equal(0, _ui.Pending);
        Assert.Equal(before.Length, vm.Items.Count);
        for (int i = 0; i < before.Length; i++)
        {
            Assert.Same(before[i], vm.Items[i]);
        }

        Assert.Equal(ChatState.Loaded, vm.State);
    }

    [Fact]
    public async Task A_user_thread_and_a_group_with_the_same_id_are_different_conversations()
    {
        await SaveRange("fake-same-id", 1, 1);
        await Save(Msg("fake-same-id", 2, 200, ThreadType.Group));
        ChatViewModel vm = Create();

        await OpenAndShow(vm, Conv("fake-same-id"));
        Assert.Equal(Expected(1), Texts(vm));
        await OpenAndShow(vm, Conv("fake-same-id", ThreadType.Group));

        Assert.Equal(Expected(2), Texts(vm));
        Assert.Equal(2, _store.PageCalls);
    }

    [Fact]
    public async Task Switching_A_to_B_to_A_clears_and_reloads_each_time()
    {
        await SaveRange(ThreadA, 1, 2);
        await SaveRange(ThreadB, 3, 4);
        ChatViewModel vm = Create();

        await OpenAndShow(vm, Conv(ThreadA));
        MessageItem[] firstA = [.. vm.Items];
        await OpenAndShow(vm, Conv(ThreadB));
        Assert.Equal(Expected(3, 4), Texts(vm));
        await OpenAndShow(vm, Conv(ThreadA));

        Assert.Equal(Expected(1, 2), Texts(vm));
        Assert.Equal(3, _store.PageCalls);
        Assert.All(vm.Items, item => Assert.DoesNotContain(item, firstA));
    }

    [Fact]
    public async Task A_late_result_for_a_conversation_that_is_no_longer_open_is_dropped()
    {
        await SaveRange(ThreadA, 1, 2);
        await SaveRange(ThreadB, 3, 4);
        ChatViewModel vm = Create();
        TaskCompletionSource gate = _store.GateNextPage();

        Task openA = vm.OpenAsync(Conv(ThreadA));
        await _store.WhenPageCalls(1).WaitAsync(Patience);
        await OpenAndShow(vm, Conv(ThreadB));
        Assert.Equal(Expected(3, 4), Texts(vm));

        gate.SetResult();
        await openA.WaitAsync(Patience);
        _ui.RunAll();

        Assert.Equal(Expected(3, 4), Texts(vm));
        Assert.Equal(ChatState.Loaded, vm.State);
    }

    [Fact]
    public async Task Closing_clears_the_items_and_returns_to_Idle_and_later_results_are_dropped()
    {
        await SaveRange(ThreadA, 1, 5);
        ChatViewModel vm = Create(pageSize: 3);
        await OpenAndShow(vm, Conv(ThreadA));
        Assert.True(vm.CanLoadOlder);

        vm.Close();
        _ui.RunAll();

        Assert.Empty(vm.Items);
        Assert.Equal(ChatState.Idle, vm.State);
        Assert.False(vm.CanLoadOlder);

        int calls = _store.PageCalls;
        await RefreshAndShow(vm);
        await vm.LoadOlderAsync().WaitAsync(Patience);
        _ui.RunAll();
        Assert.Equal(calls, _store.PageCalls);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task Closing_while_the_first_page_is_loading_drops_the_result()
    {
        await SaveRange(ThreadA, 1, 2);
        ChatViewModel vm = Create();
        TaskCompletionSource gate = _store.GateNextPage();

        Task open = vm.OpenAsync(Conv(ThreadA));
        await _store.WhenPageCalls(1).WaitAsync(Patience);
        vm.Close();
        _ui.RunAll();
        gate.SetResult();
        await open.WaitAsync(Patience);
        _ui.RunAll();

        Assert.Empty(vm.Items);
        Assert.Equal(ChatState.Idle, vm.State);
    }

    [Fact]
    public async Task The_Items_collection_is_the_same_instance_through_open_switch_reset_close_and_dispose()
    {
        await SaveRange(ThreadA, 1, 3);
        await SaveRange(ThreadB, 4, 5);
        ChatViewModel vm = Create(pageSize: 3);
        var items = vm.Items;

        await OpenAndShow(vm, Conv(ThreadA));
        Assert.Same(items, vm.Items);
        await Save(Enumerable.Range(6, 5).Select(n => Msg(ThreadA, n, 100L * n)).ToArray());
        await RefreshAndShow(vm);
        Assert.Same(items, vm.Items);
        await OpenAndShow(vm, Conv(ThreadB));
        Assert.Same(items, vm.Items);
        vm.Close();
        _ui.RunAll();
        Assert.Same(items, vm.Items);
        vm.Dispose();
        Assert.Same(items, vm.Items);
    }

    // ---- Live changes: in-place updates ------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_message_is_appended_and_no_existing_row_is_recreated()
    {
        await SaveRange(ThreadA, 1, 3);
        ChatViewModel vm = Create();
        await OpenAndShow(vm, Conv(ThreadA));
        MessageItem[] before = [.. vm.Items];
        var changes = new List<NotifyCollectionChangedEventArgs>();
        vm.Items.CollectionChanged += (_, e) => changes.Add(e);

        await Save(Msg(ThreadA, 4, 400));
        await RefreshAndShow(vm);

        Assert.Equal(Expected(1, 2, 3, 4), Texts(vm));
        for (int i = 0; i < before.Length; i++)
        {
            Assert.Same(before[i], vm.Items[i]);
        }

        NotifyCollectionChangedEventArgs only = Assert.Single(changes);
        Assert.Equal(NotifyCollectionChangedAction.Add, only.Action);
        Assert.Equal(3, only.NewStartingIndex);
    }

    [Fact]
    public async Task A_recalled_message_is_replaced_in_place_without_reordering()
    {
        await SaveRange(ThreadA, 1, 3);
        ChatViewModel vm = Create();
        await OpenAndShow(vm, Conv(ThreadA));
        MessageItem[] before = [.. vm.Items];
        var changes = new List<NotifyCollectionChangedEventArgs>();
        vm.Items.CollectionChanged += (_, e) => changes.Add(e);

        await _inner.SaveAsync([FakeMessages.Undo(ThreadType.User, ThreadA, "70002", "80002")]);
        await RefreshAndShow(vm);

        Assert.Equal(3, vm.Items.Count);
        Assert.Equal(before.Select(i => i.Id), vm.Items.Select(i => i.Id));
        Assert.Same(before[0], vm.Items[0]);
        Assert.Same(before[2], vm.Items[2]);
        Assert.NotSame(before[1], vm.Items[1]);
        Assert.Equal(MessageKind.Recalled, vm.Items[1].Kind);
        Assert.Null(vm.Items[1].Text);
        NotifyCollectionChangedEventArgs only = Assert.Single(changes);
        Assert.Equal(NotifyCollectionChangedAction.Replace, only.Action);
        Assert.Equal(1, only.NewStartingIndex);
    }

    [Fact]
    public async Task A_late_message_with_an_older_timestamp_is_inserted_in_its_sorted_place()
    {
        await Save(Msg(ThreadA, 1, 100), Msg(ThreadA, 2, 300));
        ChatViewModel vm = Create();
        await OpenAndShow(vm, Conv(ThreadA));
        MessageItem[] before = [.. vm.Items];
        var changes = new List<NotifyCollectionChangedEventArgs>();
        vm.Items.CollectionChanged += (_, e) => changes.Add(e);

        await Save(Msg(ThreadA, 3, 200));
        await RefreshAndShow(vm);

        Assert.Equal(Expected(1, 3, 2), Texts(vm));
        Assert.Same(before[0], vm.Items[0]);
        Assert.Same(before[1], vm.Items[2]);
        NotifyCollectionChangedEventArgs only = Assert.Single(changes);
        Assert.Equal(NotifyCollectionChangedAction.Add, only.Action);
        Assert.Equal(1, only.NewStartingIndex);
    }

    [Fact]
    public async Task A_refresh_with_nothing_new_changes_nothing()
    {
        await SaveRange(ThreadA, 1, 3);
        ChatViewModel vm = Create();
        await OpenAndShow(vm, Conv(ThreadA));
        var changes = new List<NotifyCollectionChangedAction>();
        vm.Items.CollectionChanged += (_, e) => changes.Add(e.Action);

        await RefreshAndShow(vm);

        Assert.Empty(changes);
        Assert.Equal(2, _store.PageCalls);
    }

    [Fact]
    public async Task A_burst_larger_than_one_page_resets_the_rows_in_the_same_collection()
    {
        await SaveRange(ThreadA, 1, 3);
        ChatViewModel vm = Create(pageSize: 3);
        await OpenAndShow(vm, Conv(ThreadA));
        var items = vm.Items;

        await SaveRange(ThreadA, 4, 8);
        await RefreshAndShow(vm);

        Assert.Same(items, vm.Items);
        Assert.Equal(Expected(6, 7, 8), Texts(vm));
        Assert.Equal(ChatState.Loaded, vm.State);
        Assert.True(vm.CanLoadOlder);

        await vm.LoadOlderAsync().WaitAsync(Patience);
        _ui.RunAll();
        Assert.Equal(Expected(3, 4, 5, 6, 7, 8), Texts(vm));
    }

    [Fact]
    public async Task Two_overlapping_refreshes_cost_one_extra_pass_and_never_duplicate_a_row()
    {
        await SaveRange(ThreadA, 1, 2);
        ChatViewModel vm = Create();
        await OpenAndShow(vm, Conv(ThreadA));
        TaskCompletionSource gate = _store.GateNextPage();

        Task first = vm.RefreshLatestAsync();
        await _store.WhenPageCalls(2).WaitAsync(Patience);
        Task second = vm.RefreshLatestAsync();
        await second.WaitAsync(Patience);
        Task third = vm.RefreshLatestAsync();
        await third.WaitAsync(Patience);
        await Save(Msg(ThreadA, 3, 300));
        gate.SetResult();
        await first.WaitAsync(Patience);
        _ui.RunAll();

        Assert.Equal(Expected(1, 2, 3), Texts(vm));
        // The open, the first refresh pass and ONE more pass for the two requests that arrived meanwhile.
        Assert.Equal(3, _store.PageCalls);
    }

    // ---- Older messages ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Loading_older_inserts_at_the_start_and_stops_when_exhausted()
    {
        await SaveRange(ThreadA, 1, 7);
        ChatViewModel vm = Create(pageSize: 3);
        await OpenAndShow(vm, Conv(ThreadA));
        MessageItem[] newest = [.. vm.Items];
        var changes = new List<NotifyCollectionChangedEventArgs>();
        vm.Items.CollectionChanged += (_, e) => changes.Add(e);

        await vm.LoadOlderAsync().WaitAsync(Patience);
        _ui.RunAll();

        Assert.Equal(Expected(2, 3, 4, 5, 6, 7), Texts(vm));
        Assert.True(vm.CanLoadOlder);
        Assert.All(changes, e => Assert.Equal(NotifyCollectionChangedAction.Add, e.Action));
        Assert.Equal([0, 1, 2], changes.Select(e => e.NewStartingIndex));
        for (int i = 0; i < newest.Length; i++)
        {
            Assert.Same(newest[i], vm.Items[3 + i]);
        }

        await vm.LoadOlderAsync().WaitAsync(Patience);
        _ui.RunAll();

        Assert.Equal(Expected(1, 2, 3, 4, 5, 6, 7), Texts(vm));
        Assert.False(vm.CanLoadOlder);

        int calls = _store.PageCalls;
        await vm.LoadOlderAsync().WaitAsync(Patience);
        _ui.RunAll();
        Assert.Equal(calls, _store.PageCalls);
        Assert.Equal(Expected(1, 2, 3, 4, 5, 6, 7), Texts(vm));
    }

    [Fact]
    public async Task Loading_older_uses_the_cursor_of_the_oldest_shown_message()
    {
        await SaveRange(ThreadA, 1, 5);
        ChatViewModel vm = Create(pageSize: 3);
        await OpenAndShow(vm, Conv(ThreadA));

        await vm.LoadOlderAsync().WaitAsync(Patience);

        PageCall older = _store.Calls[1];
        Assert.Equal(new MessageCursor(300, vm.Items.Single(i => i.Text == "fake-text-3").Id), older.Before);
        Assert.True(older.IncludeRecalled);
        Assert.False(older.IncludeRaw);
        Assert.Equal(3, older.Limit);
    }

    [Fact]
    public async Task Two_overlapping_older_loads_make_one_store_read()
    {
        await SaveRange(ThreadA, 1, 7);
        ChatViewModel vm = Create(pageSize: 3);
        await OpenAndShow(vm, Conv(ThreadA));
        TaskCompletionSource gate = _store.GateNextPage();

        Task first = vm.LoadOlderAsync();
        await _store.WhenPageCalls(2).WaitAsync(Patience);
        await vm.LoadOlderAsync().WaitAsync(Patience);
        gate.SetResult();
        await first.WaitAsync(Patience);
        _ui.RunAll();

        Assert.Equal(2, _store.PageCalls);
        Assert.Equal(Expected(2, 3, 4, 5, 6, 7), Texts(vm));
    }

    [Fact]
    public async Task An_older_page_that_arrives_after_another_conversation_was_opened_is_dropped()
    {
        await SaveRange(ThreadA, 1, 7);
        await SaveRange(ThreadB, 8, 8);
        ChatViewModel vm = Create(pageSize: 3);
        await OpenAndShow(vm, Conv(ThreadA));
        TaskCompletionSource gate = _store.GateNextPage();

        Task older = vm.LoadOlderAsync();
        await _store.WhenPageCalls(2).WaitAsync(Patience);
        await OpenAndShow(vm, Conv(ThreadB));
        gate.SetResult();
        await older.WaitAsync(Patience);
        _ui.RunAll();

        Assert.Equal(Expected(8), Texts(vm));
    }

    [Fact]
    public async Task An_older_page_that_arrives_after_a_reset_is_dropped()
    {
        await SaveRange(ThreadA, 1, 5);
        ChatViewModel vm = Create(pageSize: 3);
        await OpenAndShow(vm, Conv(ThreadA));
        TaskCompletionSource gate = _store.GateNextPage();

        Task older = vm.LoadOlderAsync();
        await _store.WhenPageCalls(2).WaitAsync(Patience);
        await SaveRange(ThreadA, 6, 10);
        await RefreshAndShow(vm);
        Assert.Equal(Expected(8, 9, 10), Texts(vm));
        gate.SetResult();
        await older.WaitAsync(Patience);
        _ui.RunAll();

        Assert.Equal(Expected(8, 9, 10), Texts(vm));
    }

    // ---- Failures --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failing_store_sets_Error_without_throwing_and_opening_again_retries()
    {
        await SaveRange(ThreadA, 1, 2);
        ChatViewModel vm = Create();
        _store.PageException = new InvalidOperationException("fake-secret-text-in-an-exception");

        await OpenAndShow(vm, Conv(ThreadA));

        Assert.Equal(ChatState.Error, vm.State);
        Assert.Empty(vm.Items);
        Assert.False(vm.CanLoadOlder);

        _store.PageException = null;
        await OpenAndShow(vm, Conv(ThreadA));

        Assert.Equal(ChatState.Loaded, vm.State);
        Assert.Equal(Expected(1, 2), Texts(vm));
        Assert.Equal(2, _store.PageCalls);
    }

    [Fact]
    public async Task A_refresh_after_a_failed_open_heals_the_view()
    {
        await SaveRange(ThreadA, 1, 2);
        ChatViewModel vm = Create();
        _store.PageException = new InvalidOperationException("fake failure");
        await OpenAndShow(vm, Conv(ThreadA));
        Assert.Equal(ChatState.Error, vm.State);

        _store.PageException = null;
        await RefreshAndShow(vm);

        Assert.Equal(ChatState.Loaded, vm.State);
        Assert.Equal(Expected(1, 2), Texts(vm));
    }

    [Fact]
    public async Task A_failing_refresh_keeps_the_shown_messages_and_the_state()
    {
        await SaveRange(ThreadA, 1, 2);
        ChatViewModel vm = Create();
        await OpenAndShow(vm, Conv(ThreadA));
        _store.PageException = new InvalidOperationException("fake failure");

        await RefreshAndShow(vm);

        Assert.Equal(ChatState.Loaded, vm.State);
        Assert.Equal(Expected(1, 2), Texts(vm));
    }

    [Fact]
    public async Task A_failing_older_load_keeps_everything_and_can_be_tried_again()
    {
        await SaveRange(ThreadA, 1, 5);
        ChatViewModel vm = Create(pageSize: 3);
        await OpenAndShow(vm, Conv(ThreadA));
        _store.PageException = new InvalidOperationException("fake failure");

        await vm.LoadOlderAsync().WaitAsync(Patience);
        _ui.RunAll();

        Assert.Equal(ChatState.Loaded, vm.State);
        Assert.True(vm.CanLoadOlder);
        Assert.Equal(Expected(3, 4, 5), Texts(vm));

        _store.PageException = null;
        await vm.LoadOlderAsync().WaitAsync(Patience);
        _ui.RunAll();
        Assert.Equal(Expected(1, 2, 3, 4, 5), Texts(vm));
    }

    // ---- Dispose ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task After_dispose_every_operation_does_nothing()
    {
        await SaveRange(ThreadA, 1, 5);
        ChatViewModel vm = Create(pageSize: 3);
        await OpenAndShow(vm, Conv(ThreadA));
        int calls = _store.PageCalls;

        vm.Dispose();
        vm.Dispose();
        await vm.OpenAsync(Conv(ThreadB)).WaitAsync(Patience);
        await vm.RefreshLatestAsync().WaitAsync(Patience);
        await vm.LoadOlderAsync().WaitAsync(Patience);
        vm.Close();
        _ui.RunAll();

        Assert.Equal(calls, _store.PageCalls);
        Assert.Equal(Expected(3, 4, 5), Texts(vm));
    }

    [Fact]
    public async Task A_result_that_arrives_after_dispose_is_dropped()
    {
        await SaveRange(ThreadA, 1, 2);
        ChatViewModel vm = Create();
        TaskCompletionSource gate = _store.GateNextPage();

        Task open = vm.OpenAsync(Conv(ThreadA));
        await _store.WhenPageCalls(1).WaitAsync(Patience);
        vm.Dispose();
        gate.SetResult();
        await open.WaitAsync(Patience);
        _ui.RunAll();

        Assert.Empty(vm.Items);
        Assert.Equal(ChatState.Idle, vm.State);
    }

    // ---- Sender names ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task In_a_group_the_sender_name_is_the_message_name_then_the_contact_name_then_null()
    {
        await _inner.ReplaceContactsAsync([new ContactName("fake-uid-bbbb2222", "Fake Contact Two")]);
        await Save(
            Msg(GroupThread, 1, 100, ThreadType.Group, sender: "fake-uid-aaaa1111", displayName: "Fake Display One"),
            Msg(GroupThread, 2, 200, ThreadType.Group, sender: "fake-uid-bbbb2222", displayName: string.Empty),
            Msg(GroupThread, 3, 300, ThreadType.Group, sender: "fake-uid-cccc3333", displayName: string.Empty),
            Msg(GroupThread, 4, 400, ThreadType.Group, self: true));
        ChatViewModel vm = Create();

        await OpenAndShow(vm, Conv(GroupThread, ThreadType.Group));

        Assert.Equal(["Fake Display One", "Fake Contact Two", null], vm.Items.Take(3).Select(i => i.SenderName));
        Assert.Equal(["1111", "2222", "3333"], vm.Items.Take(3).Select(i => i.SenderShortId));
        Assert.True(vm.Items[3].IsSelf);
        Assert.Equal([false, false, false, true], vm.Items.Select(i => i.IsSelf));
    }

    [Fact]
    public async Task In_a_private_chat_the_sender_name_is_always_null()
    {
        await _inner.ReplaceContactsAsync([new ContactName(FakeMessages.FakeSender, "Fake Contact")]);
        await Save(Msg(ThreadA, 1, 100, sender: FakeMessages.FakeSender, displayName: "Fake Display Name"));
        ChatViewModel vm = Create();

        await OpenAndShow(vm, Conv(ThreadA));

        Assert.Null(Assert.Single(vm.Items).SenderName);
    }

    [Fact]
    public async Task A_failing_contact_store_only_loses_the_fallback_names()
    {
        await Save(
            Msg(GroupThread, 1, 100, ThreadType.Group, sender: "fake-uid-aaaa1111", displayName: "Fake Display One"),
            Msg(GroupThread, 2, 200, ThreadType.Group, sender: "fake-uid-bbbb2222", displayName: string.Empty));
        ChatViewModel vm = Create(contacts: new ThrowingContactStore());

        await OpenAndShow(vm, Conv(GroupThread, ThreadType.Group));

        Assert.Equal(ChatState.Loaded, vm.State);
        Assert.Equal(["Fake Display One", null], vm.Items.Select(i => i.SenderName));
    }

    [Fact]
    public async Task A_recalled_message_has_no_text_and_the_Recalled_kind()
    {
        await SaveRange(ThreadA, 1, 2);
        await _inner.SaveAsync([FakeMessages.Undo(ThreadType.User, ThreadA, "70001", "80001")]);
        ChatViewModel vm = Create();

        await OpenAndShow(vm, Conv(ThreadA));

        Assert.Equal(MessageKind.Recalled, vm.Items[0].Kind);
        Assert.Null(vm.Items[0].Text);
        Assert.Equal(MessageKind.Text, vm.Items[1].Kind);
    }

    // ---- IsLoadingOlder and ReachedStart -----------------------------------------------------------------------------

    // Seven messages, pages of three: the open shows 5..7 and 4 and below are older.
    private async Task<ChatViewModel> OpenWithOlder()
    {
        await SaveRange(ThreadA, 1, 7);
        ChatViewModel vm = Create(pageSize: 3);
        await OpenAndShow(vm, Conv(ThreadA));
        return vm;
    }

    // Starts an older load that waits at the store and shows the flag on the view side. Returns the load and its gate.
    private async Task<(Task Load, TaskCompletionSource Gate)> StartGatedOlderLoad(ChatViewModel vm)
    {
        TaskCompletionSource gate = _store.GateNextPage();
        int calls = _store.PageCalls;
        Task load = vm.LoadOlderAsync();
        await _store.WhenPageCalls(calls + 1).WaitAsync(Patience);
        _ui.RunAll();
        return (load, gate);
    }

    [Fact]
    public async Task IsLoadingOlder_is_false_at_first_and_after_an_open()
    {
        ChatViewModel vm = await OpenWithOlder();

        Assert.False(vm.IsLoadingOlder);
    }

    [Fact]
    public async Task IsLoadingOlder_turns_on_only_through_the_dispatcher_and_off_when_the_page_is_applied()
    {
        ChatViewModel vm = await OpenWithOlder();
        TaskCompletionSource gate = _store.GateNextPage();

        Task load = vm.LoadOlderAsync();
        await _store.WhenPageCalls(2).WaitAsync(Patience);
        Assert.False(vm.IsLoadingOlder);

        _ui.RunAll();
        Assert.True(vm.IsLoadingOlder);

        gate.SetResult();
        await load.WaitAsync(Patience);
        Assert.True(vm.IsLoadingOlder);

        _ui.RunAll();
        Assert.False(vm.IsLoadingOlder);
        Assert.Equal(Expected(2, 3, 4, 5, 6, 7), Texts(vm));
    }

    [Fact]
    public async Task IsLoadingOlder_is_reset_after_an_empty_older_page()
    {
        await SaveRange(ThreadA, 1, 3);
        ChatViewModel vm = Create(pageSize: 3);
        await OpenAndShow(vm, Conv(ThreadA));
        Assert.True(vm.CanLoadOlder);

        (Task load, TaskCompletionSource gate) = await StartGatedOlderLoad(vm);
        Assert.True(vm.IsLoadingOlder);
        gate.SetResult();
        await load.WaitAsync(Patience);
        _ui.RunAll();

        Assert.False(vm.IsLoadingOlder);
        Assert.False(vm.CanLoadOlder);
        Assert.True(vm.ReachedStart);
    }

    [Fact]
    public async Task IsLoadingOlder_is_reset_after_a_failing_read_and_a_new_load_is_possible()
    {
        ChatViewModel vm = await OpenWithOlder();
        _store.PageException = new InvalidOperationException("fake failure");

        await vm.LoadOlderAsync().WaitAsync(Patience);
        _ui.RunAll();

        Assert.False(vm.IsLoadingOlder);
        Assert.True(vm.CanLoadOlder);
        Assert.False(vm.ReachedStart);

        _store.PageException = null;
        await vm.LoadOlderAsync().WaitAsync(Patience);
        _ui.RunAll();
        Assert.False(vm.IsLoadingOlder);
        Assert.Equal(Expected(2, 3, 4, 5, 6, 7), Texts(vm));
    }

    [Fact]
    public async Task IsLoadingOlder_is_reset_when_the_caller_cancels_the_load()
    {
        ChatViewModel vm = await OpenWithOlder();
        using var cts = new CancellationTokenSource();
        TaskCompletionSource gate = _store.GateNextPage();

        Task load = vm.LoadOlderAsync(cts.Token);
        await _store.WhenPageCalls(2).WaitAsync(Patience);
        _ui.RunAll();
        Assert.True(vm.IsLoadingOlder);

        await cts.CancelAsync();
        await load.WaitAsync(Patience);
        _ui.RunAll();

        Assert.False(vm.IsLoadingOlder);
        Assert.Equal(Expected(5, 6, 7), Texts(vm));
        gate.TrySetResult();
    }

    [Fact]
    public async Task IsLoadingOlder_is_reset_when_the_chat_is_closed_meanwhile()
    {
        ChatViewModel vm = await OpenWithOlder();
        (Task load, TaskCompletionSource gate) = await StartGatedOlderLoad(vm);
        Assert.True(vm.IsLoadingOlder);

        vm.Close();
        _ui.RunAll();
        Assert.False(vm.IsLoadingOlder);

        gate.SetResult();
        await load.WaitAsync(Patience);
        _ui.RunAll();

        Assert.False(vm.IsLoadingOlder);
        Assert.Empty(vm.Items);
        Assert.Equal(ChatState.Idle, vm.State);
    }

    [Fact]
    public async Task IsLoadingOlder_is_reset_when_another_conversation_is_opened_meanwhile()
    {
        await SaveRange(ThreadB, 20, 20);
        ChatViewModel vm = await OpenWithOlder();
        (Task load, TaskCompletionSource gate) = await StartGatedOlderLoad(vm);
        Assert.True(vm.IsLoadingOlder);

        await OpenAndShow(vm, Conv(ThreadB));
        Assert.False(vm.IsLoadingOlder);

        gate.SetResult();
        await load.WaitAsync(Patience);
        _ui.RunAll();

        Assert.False(vm.IsLoadingOlder);
        Assert.Equal(Expected(20), Texts(vm));
    }

    [Fact]
    public async Task IsLoadingOlder_is_reset_when_a_gap_reset_makes_the_older_page_stale()
    {
        await SaveRange(ThreadA, 1, 5);
        ChatViewModel vm = Create(pageSize: 3);
        await OpenAndShow(vm, Conv(ThreadA));
        (Task load, TaskCompletionSource gate) = await StartGatedOlderLoad(vm);
        Assert.True(vm.IsLoadingOlder);

        await SaveRange(ThreadA, 6, 10);
        await RefreshAndShow(vm);
        gate.SetResult();
        await load.WaitAsync(Patience);
        _ui.RunAll();

        Assert.False(vm.IsLoadingOlder);
        Assert.Equal(Expected(8, 9, 10), Texts(vm));
    }

    [Fact]
    public async Task IsLoadingOlder_is_reset_when_the_chat_is_disposed_meanwhile()
    {
        ChatViewModel vm = await OpenWithOlder();
        (Task load, TaskCompletionSource gate) = await StartGatedOlderLoad(vm);
        Assert.True(vm.IsLoadingOlder);

        vm.Dispose();
        _ui.RunAll();
        Assert.False(vm.IsLoadingOlder);

        gate.TrySetResult();
        await load.WaitAsync(Patience);
        _ui.RunAll();

        Assert.False(vm.IsLoadingOlder);
        Assert.Equal(Expected(5, 6, 7), Texts(vm));
    }

    [Fact]
    public async Task Disposing_while_nothing_loads_posts_nothing()
    {
        ChatViewModel vm = await OpenWithOlder();

        vm.Dispose();

        Assert.Equal(0, _ui.Pending);
    }

    [Fact]
    public async Task While_loading_a_second_call_makes_no_read_and_keeps_the_flag()
    {
        ChatViewModel vm = await OpenWithOlder();
        (Task load, TaskCompletionSource gate) = await StartGatedOlderLoad(vm);
        int calls = _store.PageCalls;

        await vm.LoadOlderAsync().WaitAsync(Patience);
        _ui.RunAll();

        Assert.Equal(calls, _store.PageCalls);
        Assert.True(vm.IsLoadingOlder);

        gate.SetResult();
        await load.WaitAsync(Patience);
        _ui.RunAll();
        Assert.False(vm.IsLoadingOlder);
    }

    [Fact]
    public async Task IsLoadingOlder_changes_are_announced()
    {
        ChatViewModel vm = await OpenWithOlder();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        (Task load, TaskCompletionSource gate) = await StartGatedOlderLoad(vm);
        Assert.Contains(nameof(ChatViewModel.IsLoadingOlder), raised);
        raised.Clear();

        gate.SetResult();
        await load.WaitAsync(Patience);
        _ui.RunAll();

        Assert.Contains(nameof(ChatViewModel.IsLoadingOlder), raised);
    }

    [Fact]
    public async Task ReachedStart_is_false_before_anything_is_open_while_loading_and_when_empty_or_failed()
    {
        await SaveRange(ThreadA, 1, 2);
        ChatViewModel vm = Create();
        Assert.False(vm.ReachedStart);

        await vm.OpenAsync(Conv(ThreadA)).WaitAsync(Patience);
        Assert.False(vm.ReachedStart);

        _ui.RunAll();
        Assert.True(vm.ReachedStart);

        await OpenAndShow(vm, Conv("fake-thread-empty"));
        Assert.Equal(ChatState.Empty, vm.State);
        Assert.False(vm.ReachedStart);

        _store.PageException = new InvalidOperationException("fake failure");
        await OpenAndShow(vm, Conv("fake-thread-broken"));
        Assert.Equal(ChatState.Error, vm.State);
        Assert.False(vm.ReachedStart);
    }

    [Fact]
    public async Task ReachedStart_is_false_while_older_messages_may_exist_and_true_once_exhausted()
    {
        ChatViewModel vm = await OpenWithOlder();
        Assert.True(vm.CanLoadOlder);
        Assert.False(vm.ReachedStart);

        await vm.LoadOlderAsync().WaitAsync(Patience);
        _ui.RunAll();
        Assert.True(vm.CanLoadOlder);
        Assert.False(vm.ReachedStart);

        await vm.LoadOlderAsync().WaitAsync(Patience);
        _ui.RunAll();
        Assert.False(vm.CanLoadOlder);
        Assert.True(vm.ReachedStart);
        Assert.Equal(Expected(1, 2, 3, 4, 5, 6, 7), Texts(vm));
    }

    [Fact]
    public async Task ReachedStart_is_true_when_the_first_page_is_not_full_and_is_announced_and_cleared_by_Close()
    {
        await SaveRange(ThreadA, 1, 2);
        ChatViewModel vm = Create(pageSize: 3);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await OpenAndShow(vm, Conv(ThreadA));
        Assert.True(vm.ReachedStart);
        Assert.Contains(nameof(ChatViewModel.ReachedStart), raised);

        raised.Clear();
        vm.Close();
        _ui.RunAll();
        Assert.False(vm.ReachedStart);
        Assert.Contains(nameof(ChatViewModel.ReachedStart), raised);
    }

    // ---- Construction and hygiene ------------------------------------------------------------------------------------

    [Fact]
    public void The_constructor_checks_its_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => new ChatViewModel(null!, _inner, _ui));
        Assert.Throws<ArgumentNullException>(() => new ChatViewModel(_store, null!, _ui));
        Assert.Throws<ArgumentNullException>(() => new ChatViewModel(_store, _inner, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChatViewModel(_store, _inner, _ui, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChatViewModel(_store, _inner, _ui, 501));
    }

    [Fact]
    public async Task ToString_prints_nothing_about_the_conversation()
    {
        await SaveRange(ThreadA, 1, 1);
        ChatViewModel vm = Create();
        await OpenAndShow(vm, Conv(ThreadA));

        string text = vm.ToString();

        Assert.Equal(nameof(ChatViewModel), text);
        Assert.DoesNotContain("fake-text", text, StringComparison.Ordinal);
        Assert.DoesNotContain(ThreadA, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task State_changes_raise_property_changed_only_through_the_dispatcher()
    {
        await SaveRange(ThreadA, 1, 4);
        ChatViewModel vm = Create(pageSize: 3);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await vm.OpenAsync(Conv(ThreadA)).WaitAsync(Patience);
        Assert.Empty(raised);

        _ui.RunAll();
        Assert.Contains(nameof(ChatViewModel.State), raised);
        Assert.Contains(nameof(ChatViewModel.CanLoadOlder), raised);
    }
}
