namespace Luna.Protocol.Api;

/// <summary>Result of sending a text message: the server accepted it. <c>MsgId</c> is null if the response has none.</summary>
/// <param name="MsgId">Message id issued by the server, kept verbatim (64-bit or larger number); never logged.</param>
/// <param name="ClientId">Epoch milliseconds the client sent in the payload.</param>
public sealed record SentTextResult(string? MsgId, long ClientId)
{
    /// <summary>Does not print <c>MsgId</c> or <c>ClientId</c>.</summary>
    public override string ToString() => "SentTextResult";
}
