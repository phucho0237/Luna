using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Luna.Core.Chat;
using Luna.Core.Friends;
using Luna.Core.Realtime;

namespace Luna.Core.ViewModels;

/// <summary>
/// The home page: the conversation list, the friend directory and the live sync. Start order is fixed: (1) the stored
/// list is shown FIRST (stored conversations and cached friend names); (2) then the live feed is started; (3) then the
/// guarded friend load runs and the list is refreshed with the new names. Nothing after step 1 can block or empty the
/// stored list: their failures only change their own state. Live store events refresh the list, coalesced so a burst
/// of batches causes at most one extra refresh. <see cref="Dispose"/> cancels everything and starts the safe shutdown
/// of the live sync, then disposes the optional owned resources; it does not own the store.
/// </summary>
public sealed partial class HomeViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    private int _started;
    private int _disposed;
    private int _refreshing;
    private int _dirty;

    private readonly IDisposable[] _ownedResources;

    /// <param name="ownedResources">
    /// Optional resources this page owns (for example the friend list source). They are disposed once, after the live
    /// sync has been told to stop; a failure of one never blocks the others. Null means none.
    /// </param>
    public HomeViewModel(
        ConversationListViewModel conversations,
        FriendDirectory friends,
        LiveSyncService liveSync,
        ChatViewModel chat,
        IReadOnlyList<IDisposable>? ownedResources = null)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(friends);
        ArgumentNullException.ThrowIfNull(liveSync);
        ArgumentNullException.ThrowIfNull(chat);
        Conversations = conversations;
        Friends = friends;
        LiveSync = liveSync;
        Chat = chat;
        _ownedResources = ownedResources is null ? [] : [.. ownedResources.Where(r => r is not null)];
        StartCommand = new AsyncRelayCommand(() => StartAsync());
        RefreshFriendsCommand = new AsyncRelayCommand(RefreshFriendsAsync);
        ReconnectCommand = new RelayCommand(Reconnect);
        liveSync.MessagesStored += OnMessagesStored;
    }

    public ConversationListViewModel Conversations { get; }

    public FriendDirectory Friends { get; }

    public LiveSyncService LiveSync { get; }

    /// <summary>The messages of the selected conversation. Owned by this page: <see cref="Dispose"/> disposes it.</summary>
    public ChatViewModel Chat { get; }

    /// <summary>
    /// Opens <paramref name="item"/> in the chat pane, or closes the chat pane for null. Opening the conversation that is
    /// already open does nothing. Never throws; does nothing after dispose.
    /// </summary>
    public async Task SelectConversationAsync(ConversationItem? item, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        try
        {
            if (item is null)
            {
                Chat.Close();
                return;
            }

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
            await Chat.OpenAsync(item, linked.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // ChatViewModel handles storage failures itself; anything unexpected must not surface from a selection.
        }
    }

    /// <summary>Starts the page (see the class summary); ignored after the first run and after dispose.</summary>
    public IAsyncRelayCommand StartCommand { get; }

    /// <summary>Asks for the friend list again; the throttle guard still decides whether a request is sent.</summary>
    public IAsyncRelayCommand RefreshFriendsCommand { get; }

    /// <summary>Explicit reconnect of the live feed; it only acts after the previous run has ended.</summary>
    public IRelayCommand ReconnectCommand { get; }

    /// <summary>Never prints conversations, names or ids.</summary>
    public override string ToString() => nameof(HomeViewModel);

    /// <summary>Runs the start order once. Never throws; a second call and a call after dispose do nothing.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) == 1 || Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return;
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        CancellationToken token = linked.Token;
        try
        {
            // 1. The stored list first. It never throws.
            await Conversations.RefreshAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            // 2. The live feed. Start never blocks and never throws.
            LiveSync.Start();

            // 3. The guarded friend load, then the list again with the new names.
            await LoadFriendsAndRefreshAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Dispose or the caller cancelled: nothing more to do.
        }
        catch (Exception)
        {
            // FriendDirectory handles the known failures itself; anything unexpected must not surface from a start
            // that already showed the stored list.
        }
    }

    /// <summary>The friend refresh behind <see cref="RefreshFriendsCommand"/>.</summary>
    public async Task RefreshFriendsAsync()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        try
        {
            await LoadFriendsAndRefreshAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Disposed meanwhile.
        }
        catch (Exception)
        {
            // See StartAsync.
        }
    }

    /// <summary>Explicit reconnect; does nothing after dispose.</summary>
    public void Reconnect()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            LiveSync.Reconnect();
        }
    }

    /// <summary>
    /// Safe shutdown order: stop accepting work, cancel the page's own work, then begin the live sync shutdown (cancel
    /// the source, drain and complete the writer). Safe to call twice and while a save is blocked; never blocks.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        LiveSync.MessagesStored -= OnMessagesStored;
        _cts.Cancel();
        LiveSync.Dispose();
        Chat.Dispose();

        foreach (IDisposable resource in _ownedResources)
        {
            try
            {
                resource.Dispose();
            }
            catch (Exception)
            {
                // A failing resource must not block the others; the message is dropped because it could carry data.
            }
        }
    }

    private async Task LoadFriendsAndRefreshAsync(CancellationToken token)
    {
        await Friends.LoadAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        await Conversations.RefreshAsync(token).ConfigureAwait(false);
    }

    private void OnMessagesStored(object? sender, EventArgs e) => ScheduleRefresh();

    // Single-flight with a dirty flag: a burst of events during a running refresh costs one more refresh, not one each.
    private void ScheduleRefresh()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        Volatile.Write(ref _dirty, 1);
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0)
        {
            _ = Task.Run(RefreshLoopAsync);
        }
    }

    private async Task RefreshLoopAsync()
    {
        try
        {
            while (Volatile.Read(ref _disposed) == 0 && Interlocked.Exchange(ref _dirty, 0) == 1)
            {
                await Conversations.RefreshAsync(_cts.Token).ConfigureAwait(false);

                // The open conversation reads the same stored batch; it does nothing when no conversation is open.
                await Chat.RefreshLatestAsync(_cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // RefreshAsync does not throw; this only keeps an unexpected error from faulting a fire-and-forget task.
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }

        // A request that arrived between the last dirty check and the flag reset must not be lost.
        if (Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _dirty) == 1)
        {
            ScheduleRefresh();
        }
    }
}
