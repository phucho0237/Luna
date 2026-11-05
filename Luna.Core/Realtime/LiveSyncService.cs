using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using Luna.Core.Storage;
using Luna.Core.ViewModels;
using Luna.Protocol.Messaging;

namespace Luna.Core.Realtime;

/// <summary>
/// Keeps the local store up to date from the live feed. Safety rules:
/// (1) at most ONE connection: a second <see cref="Start"/> or <see cref="Reconnect"/> while the source is running does
/// nothing; (2) after a duplicate connection (or a failed source) nothing restarts by itself, only an explicit
/// <see cref="Reconnect"/>; (3) the socket callback only queues the batch, it never waits for SQLite (one writer task
/// saves the queue in order); (4) every state change goes through <see cref="IUiDispatcher.Post"/>;
/// (5) shutdown order: cancel the source and wait for it, complete the queue, drain what is already queued, then report
/// <see cref="LiveSyncState.Stopped"/>. State is numbers and enums only: no message, uid or server text is ever kept.
/// </summary>
public sealed partial class LiveSyncService : ObservableObject, IDisposable
{
    /// <summary>
    /// How many batches may wait for the writer. When the queue is full the NEWEST batch is dropped (the socket callback
    /// never waits) and <see cref="DroppedBatches"/> counts it.
    /// </summary>
    public const int QueueCapacity = 1024;

    /// <summary>How long <see cref="Dispose"/> lets the writer drain before it cancels a save that is still running.</summary>
    public static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly IRealtimeSource _source;
    private readonly IMessageStore _store;
    private readonly IUiDispatcher _ui;
    private readonly object _gate = new();

    private Session? _session;
    private bool _disposed;
    private int _dropped;

    public LiveSyncService(IRealtimeSource source, IMessageStore store, IUiDispatcher ui)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ui);
        _source = source;
        _store = store;
        _ui = ui;
    }

    [ObservableProperty]
    public partial LiveSyncState State { get; private set; }

    /// <summary>
    /// True when the source run has ended by itself (duplicate connection or failure) and
    /// <see cref="Reconnect"/> would start a new one. The view can enable its reconnect button from it.
    /// </summary>
    [ObservableProperty]
    public partial bool CanReconnect { get; private set; }

    /// <summary>How many batches could not be saved (the batch is lost; a later history pull is meant to fill gaps).</summary>
    [ObservableProperty]
    public partial int SaveFailures { get; private set; }

    /// <summary>
    /// How many batches were dropped because the queue was full (the writer could not keep up). A batch that arrives
    /// after the queue was completed (a straggler after a stop) is not counted.
    /// </summary>
    [ObservableProperty]
    public partial int DroppedBatches { get; private set; }

    /// <summary>Raised on the UI thread after a batch changed the store (new or recalled messages).</summary>
    public event EventHandler? MessagesStored;

    /// <summary>Never prints messages, uids or server text.</summary>
    public override string ToString() => nameof(LiveSyncService);

    /// <summary>
    /// Starts the writer and the first connection. Does nothing when already started (or still stopping), and after
    /// <see cref="Dispose"/>. Never blocks and never throws.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _session is not null)
            {
                return;
            }

            var session = new Session(OnBatchDropped);
            _session = session;
            session.Writer = Task.Run(() => WriteLoopAsync(session));
            StartRun(session);
        }
    }

    /// <summary>
    /// Explicit reconnect after the source run has ended by itself (<see cref="CanReconnect"/>). Does nothing while the
    /// source is still running (that would be a second connection), when stopped, and after <see cref="Dispose"/>.
    /// </summary>
    public void Reconnect()
    {
        lock (_gate)
        {
            if (_disposed || _session is not { } session || session.Stopping || !session.RunEnded)
            {
                return;
            }

            StartRun(session);
        }
    }

    /// <summary>
    /// Stops in a safe order: cancel the source and wait for it, complete the queue, drain what was already queued, then
    /// report <see cref="LiveSyncState.Stopped"/>. <paramref name="drainToken"/> bounds the waiting: when it is
    /// cancelled the source is given up and a save still in flight is cancelled. Safe to call twice (concurrent callers
    /// share the first call's drain token), without a start, and while a save is blocked. Never throws.
    /// </summary>
    public async Task StopAsync(CancellationToken drainToken = default)
    {
        Session session;
        TaskCompletionSource done;
        bool leader = false;
        lock (_gate)
        {
            if (_session is not { } current)
            {
                return;
            }

            session = current;
            if (session.StopDone is null)
            {
                session.StopDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                session.Stopping = true;
                leader = true;
            }

            done = session.StopDone;
        }

        if (!leader)
        {
            await done.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            await TearDownAsync(session, drainToken).ConfigureAwait(false);
        }
        finally
        {
            done.TrySetResult();
        }
    }

    /// <summary>
    /// Begins a stop and returns at once: the source is cancelled now and the writer gets
    /// <see cref="DisposeDrainTimeout"/> to drain in the background. Safe to call twice.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        var drain = new CancellationTokenSource(DisposeDrainTimeout);
        _ = StopAsync(drain.Token).ContinueWith(_ => drain.Dispose(), TaskScheduler.Default);
    }

    private async Task TearDownAsync(Session session, CancellationToken drainToken)
    {
        // 1. Stop the source first so nothing new arrives. A source that ignores the token is given up when the drain
        //    token is cancelled.
        await session.SourceCts.CancelAsync().ConfigureAwait(false);
        bool sourceStopped = true;
        try
        {
            await session.SourceTask.WaitAsync(drainToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            sourceStopped = false;
        }

        // 2. Complete the queue; the writer saves what is already queued and then ends.
        session.Queue.Writer.TryComplete();
        bool writerStopped = true;
        try
        {
            await session.Writer.WaitAsync(drainToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Giving up on the drain: cancel the save in flight and wait for the writer to unwind.
            await session.WriterCts.CancelAsync().ConfigureAwait(false);
            await session.Writer.ConfigureAwait(false);
        }

        // 3. Report Stopped. Late state callbacks are ignored from here on (Stopping is set).
        lock (_gate)
        {
            if (_session == session)
            {
                _session = null;
            }

            PostState(LiveSyncState.Stopped);
            Post(() => CanReconnect = false);
        }

        if (sourceStopped && writerStopped)
        {
            session.SourceCts.Dispose();
            session.WriterCts.Dispose();
        }
    }

    // Must be called under _gate.
    private void StartRun(Session session)
    {
        session.RunId++;
        int run = session.RunId;
        session.RunEnded = false;
        session.LastState = LiveSyncState.Connecting;
        session.SourceCts.Dispose();
        session.SourceCts = new CancellationTokenSource();
        CancellationToken token = session.SourceCts.Token;
        PostState(LiveSyncState.Connecting);
        Post(() => CanReconnect = false);
        session.SourceTask = Task.Run(() => RunSourceAsync(session, run, token));
    }

    private async Task RunSourceAsync(Session session, int run, CancellationToken token)
    {
        try
        {
            await _source.RunAsync(
                messages => OnMessages(session, messages),
                state => OnState(session, run, state),
                token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            // A failing source ends the run as Disconnected. The exception is dropped on purpose: it could carry
            // server text, and the state is all the view needs.
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        // The run ended without being asked to: a duplicate connection, a failure, or a source that just returned.
        lock (_gate)
        {
            if (_session != session || session.Stopping || session.RunId != run)
            {
                return;
            }

            session.RunEnded = true;
            if (session.LastState != LiveSyncState.DuplicateConnection)
            {
                session.LastState = LiveSyncState.Disconnected;
                PostState(LiveSyncState.Disconnected);
            }

            Post(() => CanReconnect = true);
        }
    }

    // Socket thread. Only queues the batch: never waits for storage or for room. A full queue drops the batch and reports
    // it through the channel's dropped-item callback (see OnBatchDropped); a batch that arrives after the queue was
    // completed (a straggler after a stop) makes TryWrite return false without any callback, so it is not counted.
    private static void OnMessages(Session session, IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count > 0)
        {
            session.Queue.Writer.TryWrite(messages);
        }
    }

    // Called by the channel on the writing thread, only when a batch is dropped because the queue is full. Does no
    // I/O: an increment and a dispatcher post.
    private void OnBatchDropped(IReadOnlyList<ChatMessage> batch)
    {
        Interlocked.Increment(ref _dropped);
        Post(() => DroppedBatches = Volatile.Read(ref _dropped));
    }

    private void OnState(Session session, int run, LiveSyncState state)
    {
        lock (_gate)
        {
            if (_session != session || session.Stopping || session.RunId != run)
            {
                return;
            }

            session.LastState = state;
            PostState(state);
        }
    }

    private async Task WriteLoopAsync(Session session)
    {
        CancellationToken token = session.WriterCts.Token;
        int failures = 0;
        try
        {
            await foreach (IReadOnlyList<ChatMessage> batch in session.Queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    StoreResult result = await _store.SaveAsync(batch, token).ConfigureAwait(false);
                    if (result.Inserted + result.Recalled > 0)
                    {
                        Post(() => MessagesStored?.Invoke(this, EventArgs.Empty));
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // The batch is lost, the writer goes on. The exception is dropped on purpose: it could carry
                    // message content.
                    failures++;
                    int count = failures;
                    Post(() => SaveFailures = count);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The drain was given up.
        }
    }

    private void PostState(LiveSyncState state) => Post(() => State = state);

    private void Post(Action action) => _ui.Post(action);

    private sealed class Session
    {
        public Session(Action<IReadOnlyList<ChatMessage>> onDropped)
        {
            Queue = Channel.CreateBounded(
                new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true },
                onDropped);
        }

        public Channel<IReadOnlyList<ChatMessage>> Queue { get; }

        public CancellationTokenSource SourceCts { get; set; } = new();

        public CancellationTokenSource WriterCts { get; } = new();

        public Task Writer { get; set; } = Task.CompletedTask;

        public Task SourceTask { get; set; } = Task.CompletedTask;

        // The fields below are only read and written under LiveSyncService._gate.
        public int RunId { get; set; }

        public bool RunEnded { get; set; }

        public bool Stopping { get; set; }

        public LiveSyncState LastState { get; set; }

        public TaskCompletionSource? StopDone { get; set; }
    }
}
