using Xunit;
using Luna.Protocol.Realtime;

namespace Luna.Protocol.Tests.Realtime;

public class SocketFrameTests
{
    [Fact]
    public void Encode_writes_version_little_endian_cmd_subcmd_then_body()
    {
        byte[] bytes = new SocketFrame(1, 521, 0, "{}").Encode();

        // 521 = 0x0209, little-endian is 09 02.
        Assert.Equal(new byte[] { 1, 0x09, 0x02, 0, (byte)'{', (byte)'}' }, bytes);
    }

    [Fact]
    public void Decode_reverses_encode()
    {
        var original = new SocketFrame(1, 3000, 7, "{\"a\":\"xin chào\"}");

        SocketFrame decoded = SocketFrame.Decode(original.Encode());

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Decode_allows_empty_body()
    {
        SocketFrame decoded = SocketFrame.Decode(new byte[] { 1, 2, 0, 1 });

        Assert.Equal((ushort)2, decoded.Cmd);
        Assert.Equal((byte)1, decoded.SubCmd);
        Assert.Equal("", decoded.Body);
    }

    [Fact]
    public void Decode_rejects_short_input()
    {
        Assert.Throws<InvalidDataException>(() => SocketFrame.Decode(new byte[] { 1, 2, 3 }));
    }
}