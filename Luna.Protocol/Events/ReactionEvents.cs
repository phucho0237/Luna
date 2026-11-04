using Luna.Protocol.Messaging;

namespace Luna.Protocol.Events;

/// <summary>
/// Someone reacted to a message (cmd 612). <see cref="ThreadType"/> is inferred from the array name:
/// <c>reacts</c> is private chat, <c>reactGroups</c> is group (inferred from key names, meaning not verified on the live server).
/// <see cref="RawContent"/> keeps the original string, not parsed.
/// </summary>
public sealed record ReactionReceived(
    ThreadType ThreadType,
    string MsgId,
    string CliMsgId,
    string UidFrom,
    string IdTo,
    string RawContent,
    string Timestamp) : ChatEvent
{
    /// <summary>Prints only the conversation type and the message id; not the uid or the reaction content.</summary>
    public override string ToString() => $"ReactionReceived {{ ThreadType = {ThreadType}, MsgId = {MsgId} }}";
}
