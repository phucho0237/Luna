using Luna.Core.Auth;
using Luna.Protocol.Messaging;
using Luna.Protocol.Realtime;

namespace Luna.Core.Realtime;

/// <summary>
/// Thin adapter over <see cref="SocketClient"/>: maps its states and hands over the parsed messages of the two message
/// commands (501, 521). It adds no logic of its own. The client is built with NO trace action, so nothing is written to
/// any log. The run loop (reconnect, backoff, stop on a duplicate connection) is the already tested socket client.
/// </summary>
public sealed class SocketRealtimeSource : IRealtimeSource
{
    private readonly SocketClient _client;
    private readonly MessageParser _parser;

    public SocketRealtimeSource(AuthSession session)
        : this(CreateClient(session), session.Info.Uid)
    {
    }

    /// <summary>Test seam: a client with a fake connection factory.</summary>
    public SocketRealtimeSource(SocketClient client, string selfUid)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _parser = new MessageParser(selfUid);
    }

    public static LiveSyncState Map(SocketState state) => state switch
    {
        SocketState.Connecting => LiveSyncState.Connecting,
        SocketState.Connected => LiveSyncState.Connected,
        SocketState.Disconnected => LiveSyncState.Disconnected,
        SocketState.DuplicateConnection => LiveSyncState.DuplicateConnection,
        _ => LiveSyncState.Disconnected,
    };

    /// <summary>
    /// The messages of a message event (cmd 501 or 521); an empty list for any other event. Never throws for data
    /// that cannot be parsed: the socket client does not swallow callback exceptions, and one bad event must not
    /// end the connection.
    /// </summary>
    public static IReadOnlyList<ChatMessage> ExtractMessages(MessageParser parser, SocketEvent socketEvent)
    {
        ArgumentNullException.ThrowIfNull(parser);
        return ExtractMessages(socketEvent, e => parser.Parse(e).Messages);
    }

    /// <summary>
    /// Test seam of <see cref="ExtractMessages(MessageParser, SocketEvent)"/>: the same guard around any parse function.
    /// </summary>
    public static IReadOnlyList<ChatMessage> ExtractMessages(
        SocketEvent socketEvent, Func<SocketEvent, IReadOnlyList<ChatMessage>> parse)
    {
        ArgumentNullException.ThrowIfNull(socketEvent);
        ArgumentNullException.ThrowIfNull(parse);

        if (socketEvent.Cmd is not (MessageParser.CmdUserMessage or MessageParser.CmdGroupMessage))
        {
            return [];
        }

        try
        {
            return parse(socketEvent);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Every other failure ends in an empty list: the socket client does not swallow callback exceptions, so one
            // bad event would end the live connection. The exception is dropped on purpose because it could carry
            // message content.
            return [];
        }
    }

    /// <inheritdoc />
    public Task RunAsync(
        Action<IReadOnlyList<ChatMessage>> onMessages,
        Action<LiveSyncState> onState,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onMessages);
        ArgumentNullException.ThrowIfNull(onState);

        return _client.RunAsync(
            onEvent: e =>
            {
                IReadOnlyList<ChatMessage> messages = ExtractMessages(_parser, e);
                if (messages.Count > 0)
                {
                    onMessages(messages);
                }
            },
            onState: change => onState(Map(change.State)),
            cancellationToken);
    }

    /// <summary>Never prints the session.</summary>
    public override string ToString() => nameof(SocketRealtimeSource);

    private static SocketClient CreateClient(AuthSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new SocketClient(session.Context, session.Info.WebSocketUrls);
    }
}
