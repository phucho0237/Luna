using System.Net.WebSockets;

namespace Luna.Protocol.Realtime;

/// <summary>Pass-through adapter over <see cref="ClientWebSocket"/>. It holds no logic on purpose.</summary>
internal sealed class ClientWebSocketConnection : IWebSocketConnection
{
    private readonly ClientWebSocket _socket = new();

    public WebSocketState State => _socket.State;

    public WebSocketCloseStatus? CloseStatus => _socket.CloseStatus;

    public string? CloseStatusDescription => _socket.CloseStatusDescription;

    public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        foreach (KeyValuePair<string, string> header in headers)
        {
            _socket.Options.SetRequestHeader(header.Key, header.Value);
        }

        return _socket.ConnectAsync(uri, cancellationToken);
    }

    public ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        _socket.ReceiveAsync(buffer, cancellationToken);

    public ValueTask SendAsync(
        ReadOnlyMemory<byte> data, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
        _socket.SendAsync(data, messageType, endOfMessage, cancellationToken);

    public Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken) =>
        _socket.CloseOutputAsync(status, description, cancellationToken);

    public void Dispose() => _socket.Dispose();
}
