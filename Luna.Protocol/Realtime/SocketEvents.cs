using System.Text.Json;

namespace Luna.Protocol.Realtime;

/// <summary>One decoded event from the server. <see cref="Payload"/> is the original JSON, usually read further at the "data" property.</summary>
public sealed record SocketEvent(ushort Cmd, byte SubCmd, JsonElement Payload);

public enum SocketState
{
    Connecting,

    /// <summary>Connected and authenticated, starts receiving events.</summary>
    Connected,

    Disconnected,

    /// <summary>The account is open elsewhere; will not reconnect by itself.</summary>
    DuplicateConnection,
}

public sealed record SocketStateChange(SocketState State, string? Detail = null);