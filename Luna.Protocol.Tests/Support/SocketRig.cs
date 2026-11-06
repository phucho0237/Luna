using System.Collections.Concurrent;
using Luna.Protocol.Realtime;

namespace Luna.Protocol.Tests.Support;

/// <summary>Wires a SocketClient to fake connections, a fake delay and a fake clock; records states and events.</summary>
internal sealed class SocketRig : IDisposable
{
    private const long StartMillis = 1_700_000_000_000;

    private readonly ConcurrentQueue<FakeWebSocketConnection> _script = new();
    private readonly List<FakeWebSocketConnection> _created = [];
    private readonly CancellationTokenSource _cts = new();
    private long _now = StartMillis;
    private Task? _run;

    public SocketRig(params string[] endpoints)
    {
        Context = new ClientContext("imei", "test-agent/1.0");
        Client = new SocketClient(
            Context,
            endpoints,
            unixMillis: () => Interlocked.Read(ref _now),
            connectionFactory: Next,
            delay: Delay.Delay);
    }

    public ClientContext Context { get; }

    public SocketClient Client { get; }

    public FakeDelay Delay { get; } = new();

    public AsyncQueue<SocketStateChange> States { get; } = new();

    public AsyncQueue<SocketEvent> Events { get; } = new();

    /// <summary>The RunAsync task started by <see cref="Start"/>.</summary>
    public Task Run => _run!;

    public FakeWebSocketConnection Script() => Enqueue(new FakeWebSocketConnection());

    public void Start()
    {
        _run = Client.RunAsync(Events.Add, States.Add, _cts.Token);
    }

    /// <summary>Cancels and waits for RunAsync, which must return normally.</summary>
    public async Task StopAsync()
    {
        await _cts.CancelAsync();
        await _run!.Soon();
    }

    public async Task<SocketStateChange> NextState(SocketState expected)
    {
        SocketStateChange change = await States.NextAsync();
        Xunit.Assert.Equal(expected, change.State);
        return change;
    }

    public void Dispose() => _cts.Dispose();

    /// <summary>Connections handed to the client so far, in order.</summary>
    public IReadOnlyList<FakeWebSocketConnection> Created
    {
        get
        {
            lock (_created)
            {
                return [.. _created];
            }
        }
    }

    private FakeWebSocketConnection Enqueue(FakeWebSocketConnection connection)
    {
        _script.Enqueue(connection);
        return connection;
    }

    private IWebSocketConnection Next()
    {
        if (!_script.TryDequeue(out FakeWebSocketConnection? connection))
        {
            throw new InvalidOperationException("No scripted connection is left.");
        }

        lock (_created)
        {
            _created.Add(connection);
        }

        return connection;
    }
}
