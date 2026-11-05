using Luna.Core.Chat;
using Luna.Core.Friends;
using Luna.Core.Realtime;
using Luna.Core.Storage;
using Luna.Core.Tests.Support;
using Luna.Core.Throttling;
using Luna.Core.ViewModels;
using Luna.Protocol.Messaging;

namespace Luna.Core.Tests.ViewModels;

/// <summary>
/// The chat pane inside the home page: selecting a conversation opens it, the live sync keeps it fresh in the same refresh
/// loop as the list, and dispose reaches the chat. A real in-memory store; no network.
/// </summary>
public sealed class HomeChatTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private const string Thread = "fake-thread-111";

    private readonly TempFolder _folder = new();
    private readonly SqliteMessageStore _store = new(":memory:");
    private readonly FakeFriendListSource _friendSource = new();
    private readonly FakeRealtimeSource _realtime = new();
    private readonly ImmediateDispatcher _ui = new();
    private readonly List<HomeViewModel> _created = [];

    public void Dispose()
    {
        foreach (HomeViewModel home in _created)
        {
            home.Dispose();
        }

        _store.Dispose();
        _folder.Dispose();
    }

    private HomeViewModel Create()
    {
        var guard = new ApiThrottleGuard(
            Path.Combine(_folder.Path, "api-throttle.json"), () => new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero));
        var home = new HomeViewModel(
            new ConversationListViewModel(_store, _store, _ui),
            new FriendDirectory(_friendSource, guard, _store, _ui),
            new LiveSyncService(_realtime, _store, _ui),
            new ChatViewModel(_store, _store, _ui));
        _created.Add(home);
        return home;
    }

    private static ConversationItem Conv(string id = Thread) => new(ThreadType.User, id, null, 0, 0);

    private static ChatMessage Msg(string id, string ts) => FakeMessages.Text(ThreadType.User, Thread, id, id + "1", ts);

    private static async Task Eventually(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(Patience);
        while (!condition())
        {
            await Task.Delay(5, cts.Token);
        }
    }

    [Fact]
    public void The_constructor_requires_a_chat_view_model()
    {
        Assert.Throws<ArgumentNullException>(() => new HomeViewModel(
            new ConversationListViewModel(_store, _store, _ui),
            new FriendDirectory(_friendSource, new ApiThrottleGuard(Path.Combine(_folder.Path, "t.json")), _store, _ui),
            new LiveSyncService(_realtime, _store, _ui),
            null!));
    }

    [Fact]
    public async Task Selecting_a_conversation_opens_it_and_selecting_null_closes_it()
    {
        await _store.SaveAsync([Msg("5001", "1700000000001"), Msg("5002", "1700000000002")]);
        HomeViewModel home = Create();

        await home.SelectConversationAsync(Conv()).WaitAsync(Patience);

        Assert.Equal(ChatState.Loaded, home.Chat.State);
        Assert.Equal(2, home.Chat.Items.Count);

        await home.SelectConversationAsync(null).WaitAsync(Patience);

        Assert.Equal(ChatState.Idle, home.Chat.State);
        Assert.Empty(home.Chat.Items);
    }

    [Fact]
    public async Task Selecting_the_open_conversation_again_does_not_reload_it()
    {
        await _store.SaveAsync([Msg("5001", "1700000000001")]);
        HomeViewModel home = Create();
        await home.SelectConversationAsync(Conv()).WaitAsync(Patience);
        MessageItem shown = Assert.Single(home.Chat.Items);

        await home.SelectConversationAsync(Conv()).WaitAsync(Patience);

        Assert.Same(shown, Assert.Single(home.Chat.Items));
    }

    [Fact]
    public async Task A_stored_live_message_appears_in_the_open_chat()
    {
        await _store.SaveAsync([Msg("5001", "1700000000001")]);
        HomeViewModel home = Create();
        await home.StartAsync().WaitAsync(Patience);
        await _realtime.WhenRunCalls(1).WaitAsync(Patience);
        await home.SelectConversationAsync(Conv()).WaitAsync(Patience);
        Assert.Single(home.Chat.Items);

        _realtime.EmitMessages(Msg("5002", "1700000000002"));

        await Eventually(() => home.Chat.Items.Count == 2);
        Assert.Equal(ChatState.Loaded, home.Chat.State);
    }

    [Fact]
    public async Task A_message_for_a_closed_chat_does_not_open_anything()
    {
        HomeViewModel home = Create();
        await home.StartAsync().WaitAsync(Patience);
        await _realtime.WhenRunCalls(1).WaitAsync(Patience);

        _realtime.EmitMessages(Msg("5002", "1700000000002"));
        await Eventually(() => home.Conversations.Items.Count == 1);

        Assert.Equal(ChatState.Idle, home.Chat.State);
        Assert.Empty(home.Chat.Items);
    }

    [Fact]
    public async Task After_dispose_selecting_does_nothing_and_the_chat_is_disposed()
    {
        await _store.SaveAsync([Msg("5001", "1700000000001")]);
        HomeViewModel home = Create();

        home.Dispose();
        await home.SelectConversationAsync(Conv()).WaitAsync(Patience);
        await home.Chat.OpenAsync(Conv()).WaitAsync(Patience);

        Assert.Equal(ChatState.Idle, home.Chat.State);
        Assert.Empty(home.Chat.Items);
    }
}
