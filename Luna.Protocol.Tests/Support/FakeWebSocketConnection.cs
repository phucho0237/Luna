using System.Net.WebSockets;
using System.Threading.Channels;
using Luna.Protocol.Realtime;

namespace Luna.Protocol.Tests.Support;

/// <summary>
/// Scripted connection for SocketClient tests. The test pushes receive steps (frame, close, failure); ReceiveAsync
/// blocks until a step is available or the token is canceled. Nothing touches the network.
/// </summary>
internal sealed class FakeWebSocketConnection : IWebSocketConnection
{
    private readonly Channel<Func<ValueWebSocketReceiveResult>> _steps = Channel.CreateUnbounded<Func<ValueWebSocketReceiveResult>>();
    private readonly List<byte[]> _sent = [];
    private readonly List<(WebSocketCloseStatus Status, string? Description)> _closes = [];
    private Memory<byte> _current;

    /// <summary>Set to make ConnectAsync fail with this exception instead of opening.</summary>
    public Exception? ConnectFailure { get; init; }

    public WebSocketState State { get; private set; } = WebSocketState.None;

    public WebSocketCloseStatus? CloseStatus { get; private set; }

    public string? CloseStatusDescription { get; private set; }

    public Uri? ConnectUri { get; private set; }

    public IReadOnlyDictionary<string, string>? ConnectHeaders { get; private set; }

    public bool Disposed { get; private set; }

    public IReadOnlyList<byte[]> Sent
    {
        get
        {
            lock (_sent)
            {
                return [.. _sent];
            }
        }
    }

    public IReadOnlyList<(WebSocketCloseStatus Status, string? Description)> Closes
    {
        get
        {
            lock (_closes)
            {
                return [.. _closes];
            }
        }
    }

    /// <summary>Queues one received chunk. A message split in two chunks is two calls: endOfMessage false, then true.</summary>
    public void PushFrame(byte[] data, bool endOfMessage = true, WebSocketMessageType type = WebSocketMessageType.Binary)
    {
        _steps.Writer.TryWrite(() =>
        {
            _current = data;
            return new ValueWebSocketReceiveResult(data.Length, type, endOfMessage);
        });
    }

    /// <summary>Queues a Close message from the server.</summary>
    public void PushClose(WebSocketCloseStatus status, string description)
    {
        _steps.Writer.TryWrite(() =>
        {
            CloseStatus = status;
            CloseStatusDescription = description;
            State = WebSocketState.CloseReceived;
            return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        });
    }

    public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        ConnectUri = uri;
        ConnectHeaders = new Dictionary<string, string>(headers);
        if (ConnectFailure is not null)
        {
            return Task.FromException(ConnectFailure);
        }

        State = WebSocketState.Open;
        return Task.CompletedTask;
    }

    public async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        Func<ValueWebSocketReceiveResult> step = await _steps.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        ValueWebSocketReceiveResult result = step();
        if (result.Count > 0)
        {
            _current.CopyTo(buffer);
        }

        return result;
    }

    public ValueTask SendAsync(
        ReadOnlyMemory<byte> data, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        lock (_sent)
        {
            _sent.Add(data.ToArray());
        }

        return ValueTask.CompletedTask;
    }

    public Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
    {
        lock (_closes)
        {
            _closes.Add((status, description));
        }

        State = State == WebSocketState.CloseReceived ? WebSocketState.Closed : WebSocketState.CloseSent;
        return Task.CompletedTask;
    }

    public void Dispose() => Disposed = true;
}
