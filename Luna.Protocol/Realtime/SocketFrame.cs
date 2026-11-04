using System.Text;

namespace Luna.Protocol.Realtime;

/// <summary>
/// A WebSocket frame: version byte, 16-bit little-endian cmd, subCmd byte, then a UTF-8 JSON body.
/// </summary>
public sealed record SocketFrame(byte Version, ushort Cmd, byte SubCmd, string Body)
{
    public const int HeaderLength = 4;

    /// <summary>Prints the header numbers only; the body can hold message content, uids and phone numbers.</summary>
    public override string ToString() => $"SocketFrame {{ Version = {Version}, Cmd = {Cmd}, SubCmd = {SubCmd} }}";

    public byte[] Encode()
    {
        byte[] body = Encoding.UTF8.GetBytes(Body);
        byte[] buffer = new byte[HeaderLength + body.Length];
        buffer[0] = Version;
        buffer[1] = (byte)(Cmd & 0xFF);
        buffer[2] = (byte)(Cmd >> 8);
        buffer[3] = SubCmd;
        body.CopyTo(buffer, HeaderLength);
        return buffer;
    }

    public static SocketFrame Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderLength)
        {
            throw new InvalidDataException($"The WebSocket frame is too short ({data.Length} bytes).");
        }

        ushort cmd = (ushort)(data[1] | (data[2] << 8));
        return new SocketFrame(data[0], cmd, data[3], Encoding.UTF8.GetString(data[HeaderLength..]));
    }
}