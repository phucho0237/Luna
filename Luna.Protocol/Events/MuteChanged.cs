namespace Luna.Protocol.Events;

/// <summary>
/// A conversation was muted or unmuted (cmd 601, <c>mute/set</c> and <c>mute/unset</c>).
/// <see cref="Duration"/> and <see cref="StartTime"/> keep the JSON number verbatim (the unit and the meaning of special values are not verified).
/// </summary>
public sealed record MuteChanged(string ThreadId, bool Muted, string? Duration, string? StartTime) : ChatEvent
{
    /// <summary>Prints only the state; not <see cref="ThreadId"/>.</summary>
    public override string ToString() => $"MuteChanged {{ Muted = {Muted} }}";
}
