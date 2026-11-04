using System.Net.WebSockets;

namespace Luna.Protocol.Realtime;

/// <summary>
/// The slice of <see cref="ClientWebSocket"/> that <see cref="SocketClient"/> needs, so the connect/reconnect loop can be
/// tested with a fake connection. One instance serves one connection attempt.
/// </summary>
public interface IWebSocketConnection : IDisposable
{
    WebSocketState State { get; }

    WebSocketCloseStatus? CloseStatus { get; }

    string? CloseStatusDescription { get; }

    /// <param name="headers">Request headers for the handshake. They can hold the session cookie: never trace them.</param>
    Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken);

    ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    ValueTask SendAsync(
        ReadOnlyMemory<byte> data, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken);

    Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken);
}
