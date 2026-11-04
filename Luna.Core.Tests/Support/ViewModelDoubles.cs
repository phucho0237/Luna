using Luna.Core.Auth;
using Luna.Core.ViewModels;
using Luna.Protocol;
using Luna.Protocol.Auth;

namespace Luna.Core.Tests.Support;

/// <summary>Runs every posted action immediately on the calling thread.</summary>
internal sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>Holds posted actions until the test runs them, to check ordering and deferred updates.</summary>
internal sealed class QueueingDispatcher : IUiDispatcher
{
    private readonly Queue<Action> _queue = new();
    private readonly object _gate = new();

    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    public void Post(Action action)
    {
        lock (_gate)
        {
            _queue.Enqueue(action);
        }
    }

    /// <summary>Runs every queued action in order, including ones queued while running.</summary>
    public void RunAll()
    {
        while (true)
        {
            Action next;
            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    return;
                }

                next = _queue.Dequeue();
            }

            next();
        }
    }
}

/// <summary>
/// Scriptable <see cref="IAuthService"/>: replays QR events, throws scripted exceptions, can block until released,
/// and records the cancellation tokens it received.
/// </summary>
internal sealed class FakeAuthService : IAuthService
{
    private readonly TaskCompletionSource _qrGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _qrEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _restoreCalls;
    private int _qrCalls;
    private Action<QrLoginEvent>? _callback;

    public AuthSession? RestoreResult { get; set; }

    public Exception? RestoreException { get; set; }

    public AuthSession QrResult { get; set; } = FakeSessions.Create();

    public Exception? QrException { get; set; }

    /// <summary>Events passed to the callback, in order, as soon as the QR login starts.</summary>
    public List<QrLoginEvent> QrEvents { get; } = [];

    /// <summary>When true the QR login waits for <see cref="Release"/> or cancellation after replaying events.</summary>
    public bool BlockQr { get; set; }

    public int RestoreCalls => Volatile.Read(ref _restoreCalls);

    public int QrCalls => Volatile.Read(ref _qrCalls);

    public CancellationToken LastQrToken { get; private set; }

    public CancellationToken LastRestoreToken { get; private set; }

    /// <summary>Completes once the QR login has been entered and all scripted events replayed.</summary>
    public Task QrEntered => _qrEntered.Task;

    public void Release() => _qrGate.TrySetResult();

    /// <summary>Delivers one more event to the callback of the running QR login, like a late server event.</summary>
    public void Emit(QrLoginEvent e) =>
        (Volatile.Read(ref _callback) ?? throw new InvalidOperationException("The QR login was not started."))(e);

    public Task<AuthSession?> RestoreAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _restoreCalls);
        LastRestoreToken = cancellationToken;
        if (RestoreException is not null)
        {
            return Task.FromException<AuthSession?>(RestoreException);
        }

        return Task.FromResult(RestoreResult);
    }

    public async Task<AuthSession> LoginWithQrAsync(
        Action<QrLoginEvent> onEvent, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _qrCalls);
        LastQrToken = cancellationToken;
        Volatile.Write(ref _callback, onEvent);

        foreach (QrLoginEvent scripted in QrEvents)
        {
            onEvent(scripted);
        }

        _qrEntered.TrySetResult();

        if (BlockQr)
        {
            await _qrGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (QrException is not null)
        {
            throw QrException;
        }

        return QrResult;
    }
}

internal static class FakeSessions
{
    public const string FakeImei = "fake-imei-0000";
    public const string FakeUid = "fake-uid-0000";
    public const string FakeEnk = "fake-enk-secret-0000";
    public const string FakeCookie = "fake-cookie-secret-0000";

    public static AuthSession Create()
    {
        var context = new ClientContext(FakeImei, "FakeAgent/1.0");
        context.AddCookies($"zpw_sek={FakeCookie}");
        var info = new LoginInfo(
            FakeUid,
            FakeEnk,
            new Dictionary<string, IReadOnlyList<string>>(),
            []);
        return new AuthSession(context, info);
    }

    public static ApiException ApiError(int code, string message) => new(code, message);
}
