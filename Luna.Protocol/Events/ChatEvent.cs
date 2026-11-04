using System.Text.Json;
using Luna.Protocol.Diagnostics;

namespace Luna.Protocol.Events;

/// <summary>
/// A typed event created by <see cref="EventRouter"/> from a raw socket event.
/// Every subclass must override <c>ToString()</c> so it does not leak a uid or content.
/// </summary>
public abstract record ChatEvent;

/// <summary>
/// An event (or part of the data) that the router does not understand: an unsupported cmd, or an unhandled array/object in <c>data</c>.
/// <see cref="Payload"/> is the original payload of the socket event.
/// </summary>
public sealed record UnknownEvent(ushort Cmd, byte SubCmd, JsonElement Payload) : ChatEvent
{
    /// <summary>Prints only the cmd, subcmd and the key names of <c>data</c> (through <see cref="JsonShape.SafeName"/>).</summary>
    public override string ToString()
    {
        JsonElement container = Payload;
        if (Payload.ValueKind == JsonValueKind.Object
            && Payload.TryGetProperty("data", out JsonElement data)
            && data.ValueKind == JsonValueKind.Object)
        {
            container = data;
        }

        string keys = container.ValueKind == JsonValueKind.Object
            ? string.Join(", ", container.EnumerateObject().Select(p => JsonShape.SafeName(p.Name)))
            : "";
        return $"UnknownEvent {{ Cmd = {Cmd}, SubCmd = {SubCmd}, Keys = [{keys}] }}";
    }
}
