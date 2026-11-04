using Luna.Protocol.Messaging;

namespace Luna.Core.Storage;

/// <summary>One message row read back from the local store. Ids are strings (they exceed 2^53 on the wire).</summary>
public sealed record StoredMessage(
    long Id,
    ThreadType ThreadType,
    string ThreadId,
    string MsgId,
    string CliMsgId,
    long TimestampMs,
    bool IsSelf,
    string UidFrom,
    string IdTo,
    string DisplayName,
    string MsgType,
    string? Text,
    bool Recalled,
    string RawJson)
{
    /// <summary>The cursor that makes the next page start right after this message.</summary>
    public MessageCursor ToCursor() => new(TimestampMs, Id);

    /// <summary>Never prints text, uids, thread ids or raw JSON.</summary>
    public override string ToString() => $"StoredMessage {{ Id = {Id}, ThreadType = {ThreadType}, Recalled = {Recalled} }}";
}
