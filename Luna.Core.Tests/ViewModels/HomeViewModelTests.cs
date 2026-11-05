using Luna.Core.Friends;
using Luna.Core.Realtime;
using Luna.Core.Storage;
using Luna.Core.Tests.Support;
using Luna.Core.Throttling;
using Luna.Core.ViewModels;
using Luna.Protocol.Messaging;

namespace Luna.Core.Tests.ViewModels;

/// <summary>
/// The home view model: the stored list comes first and is never blocked by the friend load or the live feed, the live
/// store events refresh the list (coalesced), and dispose is safe in every interleaving. No test touches the network.
/// </summary>
public sealed class HomeViewModelTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private const string FakeUserA = "fake-user-1001";
    private const string FakeOldName = "Fake Old Name";
    private const string FakeNewName = "Fake New Name";

    private readonly TempFolder _folder = new();
    private readonly FakeMessageStore _store = new();
    private readonly FakeContactStore _contacts = new();
    private readonly FakeFriendListSource _friendSource = new();
    private readonly FakeRealtimeSource _realtime = new();
    private readonly List<HomeViewModel> _created = [];

    public void Dispose()
    {
        foreach (HomeViewModel home in _created)
        {
            home.Dispose();
        }

        _folder.Dispose();
    }

    private HomeViewModel Create(IReadOnlyList<IDisposable>? ownedResources = null)
    {
        var ui = new ImmediateDispatcher();
        var guard = new ApiThrottleGuard(
            Path.Combine(_folder.Path, "api-throttle.json"), () => new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero));
        var home = new HomeViewModel(
            new ConversationListViewModel(_store, _contacts, ui),
            new FriendDirectory(_friendSource, guard, _contacts, ui),
            new LiveSyncService(_realtime, _store, ui),
            ownedResources);
        _created.Add(home);
        return home;
    }

    // The feed is started on a pool thread, so its first run may begin a moment after StartAsync returns.
    private async Task StartAndWaitForFeedAsync(HomeViewModel home)
    {
        await home.StartAsync().WaitAsync(Patience);
        await _realtime.WhenRunCalls(1).WaitAsync(Patience);
    }

    private static ConversationSummary Summary(string id, long ts) => new(ThreadType.User, id, ts, 1);

    private static ChatMessage Msg(string id) => FakeMessages.Text(ThreadType.User, FakeUserA, id, id + "1", "1700000000" + id);

    private static async Task Eventually(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(Patience);
        while (!condition())
        {
            await Task.Delay(5, cts.Token);
        }
    }

    // ---- Start order ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Start_loads_the_stored_list_BEFORE_the_live_feed_and_the_friend_load()
    {
        _contacts.Seed((FakeUserA, FakeOldName));
        _store.Conversations = [Summary(FakeUserA, 1)];
        _friendSource.Result = FakeFriends.List(FakeFriends.Create(FakeUserA, displayName: FakeOldName));
        TaskCompletionSource gate = _store.GateNextList();
        HomeViewModel home = Create();

        Task start = home.StartAsync();
        await Eventually(() => _store.ListCalls == 1);
        for (int i = 0; i < 20; i++)
        {
            await Task.Yield();
        }

        Assert.Equal(0, _realtime.RunCalls);
        Assert.Equal(0, _friendSource.Calls);

        gate.SetResult();
        await start.WaitAsync(Patience);
        await _realtime.WhenRunCalls(1).WaitAsync(Patience);

        Assert.Equal(FakeOldName, Assert.Single(home.Conversations.Items).Name);
        Assert.Equal(1, _realtime.RunCalls);
        Assert.Equal(1, _friendSource.Calls);
    }

    [Fact]
    public async Task The_stored_list_is_populated_even_when_the_friend_load_throws_and_the_live_feed_never_connects()
    {
        _store.Conversations = [Summary(FakeUserA, 1)];
        _friendSource.Exception = new InvalidOperationException("fake unexpected friend failure");
        HomeViewModel home = Create();

        await StartAndWaitForFeedAsync(home);

        Assert.Single(home.Conversations.Items);
        Assert.True(home.Conversations.IsLoaded);
        Assert.Equal(1, _friendSource.Calls);
        Assert.Equal(1, _realtime.RunCalls);
        Assert.Equal(LiveSyncState.Connecting, home.LiveSync.State);
    }

    [Fact]
    public async Task A_friend_load_that_fails_in_the_known_way_leaves_the_stored_list_alone()
    {
        _store.Conversations = [Summary(FakeUserA, 1)];
        _friendSource.Exception = new HttpRequestException("fake network failure");
        HomeViewModel home = Create();

        await StartAndWaitForFeedAsync(home);

        Assert.Single(home.Conversations.Items);
        Assert.Equal(FriendsState.Failed, home.Friends.State);
    }

    [Fact]
    public async Task A_stored_list_failure_does_not_stop_the_live_feed_or_the_friend_load()
    {
        _store.ListException = new IOException("fake store failure");
        HomeViewModel home = Create();

        await StartAndWaitForFeedAsync(home);

        Assert.True(home.Conversations.HasError);
        Assert.Equal(1, _realtime.RunCalls);
        Assert.Equal(1, _friendSource.Calls);
    }

    [Fact]
    public async Task After_the_friend_load_the_list_is_refreshed_with_the_new_names()
    {
        _contacts.Seed((FakeUserA, FakeOldName));
        _store.Conversations = [Summary(FakeUserA, 1)];
        _friendSource.Result = FakeFriends.List(FakeFriends.Create(FakeUserA, displayName: FakeNewName));
        HomeViewModel home = Create();

        await StartAndWaitForFeedAsync(home);

        Assert.Equal(FriendsState.Loaded, home.Friends.State);
        Assert.Equal(FakeNewName, Assert.Single(home.Conversations.Items).Name);
    }

    [Fact]
    public async Task A_second_start_does_nothing_while_the_first_has_run()
    {
        HomeViewModel home = Create();

        await StartAndWaitForFeedAsync(home);
        await home.StartAsync().WaitAsync(Patience);

        Assert.Equal(1, _realtime.RunCalls);
        Assert.Equal(1, _friendSource.Calls);
    }

    // ---- Live refresh --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stored_live_batch_refreshes_the_list()
    {
        HomeViewModel home = Create();
        home.LiveSync.Start();
        await _realtime.WhenRunCalls(1).WaitAsync(Patience);
        _store.Conversations = [Summary(FakeUserA, 5)];

        _realtime.EmitMessages(Msg("1001"));

        await Eventually(() => home.Conversations.Items.Count == 1);
    }

    [Fact]
    public async Task Many_batches_while_a_refresh_is_running_cause_one_more_refresh_not_one_each()
    {
        HomeViewModel home = Create();
        home.LiveSync.Start();
        await _realtime.WhenRunCalls(1).WaitAsync(Patience);
        TaskCompletionSource gate = _store.GateNextList();

        _realtime.EmitMessages(Msg("1001"));
        await Eventually(() => _store.ListCalls == 1);
        _realtime.EmitMessages(Msg("1002"));
        _realtime.EmitMessages(Msg("1003"));
        _realtime.EmitMessages(Msg("1004"));
        await Eventually(() => _store.Saved.Count == 4);
        gate.SetResult();
        await Eventually(() => _store.ListCalls == 2);
        for (int i = 0; i < 50; i++)
        {
            await Task.Yield();
        }

        Assert.Equal(2, _store.ListCalls);
    }

    [Fact]
    public async Task The_reconnect_command_reconnects_only_after_the_run_has_ended()
    {
        HomeViewModel home = Create();
        home.LiveSync.Start();
        await _realtime.WhenRunCalls(1).WaitAsync(Patience);

        home.ReconnectCommand.Execute(null);
        Assert.Equal(1, _realtime.RunCalls);

        _realtime.EmitState(LiveSyncState.DuplicateConnection);
        _realtime.EndRun();
        await Eventually(() => home.LiveSync.CanReconnect);
        home.ReconnectCommand.Execute(null);
        await _realtime.WhenRunCalls(2).WaitAsync(Patience);

        Assert.Equal(1, _realtime.MaxAlive);
    }

    [Fact]
    public async Task The_friend_refresh_command_is_still_guarded_by_the_throttle()
    {
        HomeViewModel home = Create();
        await StartAndWaitForFeedAsync(home);
        Assert.Equal(1, _friendSource.Calls);

        await home.RefreshFriendsCommand.ExecuteAsync(null);

        Assert.Equal(1, _friendSource.Calls);
        Assert.Equal(FriendsState.Throttled, home.Friends.State);
    }

    // ---- Shutdown ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Dispose_twice_is_safe_and_start_after_dispose_touches_nothing()
    {
        HomeViewModel home = Create();

        home.Dispose();
        home.Dispose();
        await home.StartAsync().WaitAsync(Patience);

        Assert.Equal(0, _realtime.RunCalls);
        Assert.Equal(0, _friendSource.Calls);
        Assert.Equal(0, _store.ListCalls);
    }

    // ---- Owned resources -----------------------------------------------------------------------------------------

    private sealed class CountingDisposable(Action? onDispose = null) : IDisposable
    {
        public int Count { get; private set; }

        public void Dispose()
        {
            Count++;
            onDispose?.Invoke();
        }
    }

    [Fact]
    public void Owned_resources_are_disposed_once_even_when_dispose_is_called_twice()
    {
        var first = new CountingDisposable();
        var second = new CountingDisposable();
        HomeViewModel home = Create([first, second]);

        home.Dispose();
        home.Dispose();

        Assert.Equal(1, first.Count);
        Assert.Equal(1, second.Count);
    }

    [Fact]
    public void Owned_resources_stay_alive_until_dispose()
    {
        var resource = new CountingDisposable();
        HomeViewModel home = Create([resource]);

        Assert.Equal(0, resource.Count);
        home.Dispose();
        Assert.Equal(1, resource.Count);
    }

    [Fact]
    public void Null_owned_resources_are_accepted()
    {
        HomeViewModel home = Create(null);

        home.Dispose();
        home.Dispose();
    }

    [Fact]
    public void A_failing_resource_does_not_block_the_others_and_does_not_throw()
    {
        var before = new CountingDisposable();
        var failing = new CountingDisposable(() => throw new InvalidOperationException("fake failure"));
        var after = new CountingDisposable();
        HomeViewModel home = Create([before, failing, after]);

        home.Dispose();

        Assert.Equal(1, before.Count);
        Assert.Equal(1, failing.Count);
        Assert.Equal(1, after.Count);
    }

    [Fact]
    public async Task Owned_resources_are_disposed_after_the_live_sync_was_told_to_stop()
    {
        bool? sourceCancelledWhenDisposed = null;
        var resource = new CountingDisposable(() =>
            sourceCancelledWhenDisposed = _realtime.Tokens.Count > 0 && _realtime.Tokens[0].IsCancellationRequested);
        HomeViewModel home = Create([resource]);
        await StartAndWaitForFeedAsync(home);

        home.Dispose();

        Assert.Equal(1, resource.Count);
        Assert.True(sourceCancelledWhenDisposed);
    }

    [Fact]
    public async Task Dispose_while_the_stored_list_is_loading_ends_the_start_without_the_live_feed()
    {
        TaskCompletionSource gate = _store.GateNextList();
        HomeViewModel home = Create();
        Task start = home.StartAsync();
        await Eventually(() => _store.ListCalls == 1);

        home.Dispose();
        await start.WaitAsync(Patience);

        Assert.Equal(0, _realtime.RunCalls);
        Assert.Equal(0, _friendSource.Calls);
        gate.TrySetResult();
    }

    [Fact]
    public async Task Dispose_while_a_save_is_blocked_cancels_the_feed_and_does_not_throw()
    {
        _store.BlockSave = true;
        HomeViewModel home = Create();
        await StartAndWaitForFeedAsync(home);
        _realtime.EmitMessages(Msg("2001"));
        await _store.SaveEntered.WaitAsync(Patience);

        home.Dispose();

        await Eventually(() => _realtime.Tokens[0].IsCancellationRequested);
        _store.ReleaseSave();
    }

    [Fact]
    public async Task A_save_that_finishes_after_dispose_does_not_refresh_the_list()
    {
        _store.BlockSave = true;
        HomeViewModel home = Create();
        await StartAndWaitForFeedAsync(home);
        int listCallsBefore = _store.ListCalls;
        _realtime.EmitMessages(Msg("3001"));
        await _store.SaveEntered.WaitAsync(Patience);

        home.Dispose();
        _store.ReleaseSave();
        await Eventually(() => _store.Saved.Count == 1);
        for (int i = 0; i < 50; i++)
        {
            await Task.Yield();
        }

        Assert.Equal(listCallsBefore, _store.ListCalls);
    }

    [Fact]
    public void ToString_prints_the_type_name_only()
    {
        HomeViewModel home = Create();

        Assert.Equal(nameof(HomeViewModel), home.ToString());
    }
}
