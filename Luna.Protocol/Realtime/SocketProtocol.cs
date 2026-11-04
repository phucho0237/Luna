using System.Text.Json;

namespace Luna.Protocol.Realtime;

/// <summary>
/// Handler for received frames, free of I/O so it is testable: takes the bytes of one frame, returns the decoded event
/// and keeps state (cipher key, authenticated, kicked for a duplicate connection).
/// </summary>
public sealed class SocketProtocol
{
    private const ushort CmdAuthen = 1;
    private const ushort CmdDuplicateConnection = 3000;

    public byte[]? CipherKey { get; private set; }

    public bool IsAuthenticated { get; private set; }

    /// <summary>The server reports that the account is open elsewhere (cmd 3000).</summary>
    public bool DuplicateConnection { get; private set; }

    /// <returns>The decoded event, or null for a frame with no body.</returns>
    /// <exception cref="InvalidDataException">Corrupt frame or missing cipher key.</exception>
    /// <exception cref="ApiException">The server rejected authentication.</exception>
    public SocketEvent? Handle(ReadOnlySpan<byte> raw)
    {
        SocketFrame frame = SocketFrame.Decode(raw);
        if (frame.Body.Length == 0)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(frame.Body);
        JsonElement envelope = document.RootElement;

        if (frame.Cmd == CmdAuthen)
        {
            return HandleAuthen(frame, envelope);
        }

        JsonElement payload = PayloadDecoder.DecodeEnvelope(envelope, CipherKey);
        if (frame.Cmd == CmdDuplicateConnection && frame.SubCmd == 0)
        {
            DuplicateConnection = true;
        }

        return new SocketEvent(frame.Cmd, frame.SubCmd, payload);
    }

    private SocketEvent HandleAuthen(SocketFrame frame, JsonElement envelope)
    {
        string? key = JsonFields.GetString(envelope, "key");
        if (string.IsNullOrEmpty(key))
        {
            throw new InvalidDataException("The authentication frame has no key.");
        }

        CipherKey = Convert.FromBase64String(key);

        JsonElement auth = PayloadDecoder.DecodeEnvelope(envelope, CipherKey);
        int code = JsonFields.ErrorCode(auth);
        if (code != 0)
        {
            throw new ApiException(code, JsonFields.ErrorMessage(auth));
        }

        IsAuthenticated = true;
        return new SocketEvent(frame.Cmd, frame.SubCmd, auth);
    }
}