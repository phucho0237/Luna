using System.Text.Json;
using Luna.Protocol.Diagnostics;

namespace Luna.Protocol.Events;

/// <summary>Control event type, classified by <c>act_type</c> (see the "act_type / act values" section in PROTOCOL.md).</summary>
public enum ControlKind
{
    Other = 0,
    Group,
    Friend,
    Mute,
    PinnedConversation,
    EmojiStatus,
    OneOne,
    FileDone,
    ContactTab,
}

/// <summary>
/// Control event (cmd 601): group, friend, mute, pinned conversations...
/// <see cref="Content"/> is the original <c>content</c>, kept as is (the inner <c>data</c> may be a string or an object);
/// the router emits this event only when there is no typed replacement event (<see cref="FriendEvent"/>, <see cref="MuteChanged"/>,
/// <see cref="PinnedConversationsChanged"/>) or the data is unknown or missing fields. <see cref="ActType"/> and <see cref="Act"/> are open keywords.
/// </summary>
public sealed record ControlEvent(string ActType, string Act, JsonElement Content, ControlKind Kind) : ChatEvent
{
    /// <summary>Classified by <c>act_type</c>; an unknown value gives <see cref="ControlKind.Other"/>.</summary>
    public static ControlKind KindOf(string actType) => actType switch
    {
        "group" => ControlKind.Group,
        "fr" => ControlKind.Friend,
        "mute" => ControlKind.Mute,
        "pinned_convers" => ControlKind.PinnedConversation,
        "emoji_status" => ControlKind.EmojiStatus,
        "oneone" => ControlKind.OneOne,
        "file_done" => ControlKind.FileDone,
        "contact_tab" => ControlKind.ContactTab,
        _ => ControlKind.Other,
    };

    /// <summary>Prints only the type and act_type/act through <see cref="JsonShape.SafeToken"/>; not <see cref="Content"/>.</summary>
    public override string ToString() =>
        $"ControlEvent {{ Kind = {Kind}, ActType = {JsonShape.SafeToken(ActType)}, Act = {JsonShape.SafeToken(Act)} }}";
}
