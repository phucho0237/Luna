using Luna.Protocol.Messaging;

namespace Luna.Protocol.Events;

/// <summary>
/// A new message was received (cmd 501 or 521). A message sent by this account itself from another device
/// also raises this event, with <c>Message.IsSelf</c> set to <c>true</c> (seen on the live server).
/// </summary>
public sealed record MessageReceived(ChatMessage Message) : ChatEvent
{
    /// <summary>Prints only the message id, not the uid or content.</summary>
    public override string ToString() => $"MessageReceived {{ MsgId = {Message.MsgId} }}";
}

/// <summary>A message was recalled. Every id is a string so no precision is lost.</summary>
public sealed record MessageUndone(ThreadType ThreadType, string ThreadId, string GlobalMsgId, string CliMsgId) : ChatEvent
{
    /// <summary>Prints only the id of the recalled message, not <see cref="ThreadId"/>.</summary>
    public override string ToString() => $"MessageUndone {{ GlobalMsgId = {GlobalMsgId}, CliMsgId = {CliMsgId} }}";
}
