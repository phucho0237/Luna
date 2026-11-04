namespace Luna.Protocol.Events;

/// <summary>
/// Someone is typing (cmd 602, <c>act_type</c> is <c>typing</c>; <c>act</c> is <c>typing</c> for private chat, <c>gtyping</c> for group).
/// The fields <c>uid</c>, <c>gid</c>, <c>ts</c> are inside the JSON string <c>data</c>; field names follow zca-js,
/// not verified on the live server. <see cref="GroupId"/> exists only in group chat.
/// </summary>
public sealed record TypingNotice(bool IsGroup, string Uid, string? GroupId, string? Timestamp) : ChatEvent
{
    /// <summary>Prints only the conversation type; not the uid or group id.</summary>
    public override string ToString() => $"TypingNotice {{ IsGroup = {IsGroup} }}";
}
