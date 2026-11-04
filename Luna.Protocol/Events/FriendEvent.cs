namespace Luna.Protocol.Events;

/// <summary>Friend event type (<c>act_type = fr</c>); direction confirmed on 03/11/2026 (see PROTOCOL.md).</summary>
public enum FriendEventKind
{
    /// <summary><c>req_v2</c>: a friend request sent by this account.</summary>
    RequestSent = 0,

    /// <summary><c>undo_req</c>: cancels a friend request that was sent.</summary>
    RequestUndone,

    /// <summary><c>req</c>: a friend request sent to this account by another account.</summary>
    RequestReceived,

    /// <summary><c>add</c>: the two sides became friends (the side that accepted cannot be told apart yet).</summary>
    Added,

    /// <summary><c>remove</c>: unfriend.</summary>
    Removed,
}

/// <summary>
/// Friend event (cmd 601, <c>act_type = fr</c>). <see cref="Uid"/> exists only on <c>req</c> (the sender, a JSON number kept verbatim);
/// <see cref="ToUid"/> and <see cref="FromUid"/> exist on <c>req_v2</c> and <c>undo_req</c>. <see cref="Message"/> is the attached note.
/// </summary>
public sealed record FriendEvent(
    FriendEventKind Kind,
    string? Uid,
    string? ToUid,
    string? FromUid,
    string? Message) : ChatEvent
{
    /// <summary>Prints only the type; not the uid or the note.</summary>
    public override string ToString() => $"FriendEvent {{ Kind = {Kind} }}";
}
