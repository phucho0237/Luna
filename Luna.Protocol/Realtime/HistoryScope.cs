namespace Luna.Protocol.Realtime;

/// <summary>Which kind of conversation an old-message request (WebSocket cmd 510 or 511) is about.</summary>
public enum HistoryScope
{
    /// <summary>Private chats (cmd 510, answered with a <c>msgs</c> array).</summary>
    Private = 0,

    /// <summary>Group chats (cmd 511, answered with a <c>groupMsgs</c> array).</summary>
    Group = 1,
}
