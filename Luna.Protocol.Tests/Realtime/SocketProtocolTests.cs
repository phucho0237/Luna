using System.Text.Json;
using Xunit;
using Luna.Protocol.Realtime;

namespace Luna.Protocol.Tests.Realtime;

public class SocketProtocolTests
{
    private static byte[] Frame(ushort cmd, byte subCmd, string json) => new SocketFrame(1, cmd, subCmd, json).Encode();

    /// <summary>Authentication frame with plain data (encrypt 0), containing a JSON string.</summary>
    private static byte[] AuthFrame(string innerJson) =>
        Frame(1, 1, "{\"key\":\"" + Vectors.CipherKey + "\",\"encrypt\":0,\"data\":" + JsonSerializer.Serialize(innerJson) + "}");

    private static SocketProtocol Authenticated()
    {
        var protocol = new SocketProtocol();
        protocol.Handle(AuthFrame("{\"error_code\":0,\"data\":{}}"));
        return protocol;
    }

    [Fact]
    public void Authen_stores_key_and_authenticates()
    {
        var protocol = new SocketProtocol();

        SocketEvent? socketEvent = protocol.Handle(AuthFrame("{\"error_code\":0,\"data\":{\"qCmds\":[]}}"));

        Assert.True(protocol.IsAuthenticated);
        Assert.NotNull(protocol.CipherKey);
        Assert.Equal(32, protocol.CipherKey.Length);
        Assert.NotNull(socketEvent);
        Assert.Equal((ushort)1, socketEvent.Cmd);
    }

    [Fact]
    public void Authen_rejected_by_server_throws()
    {
        var protocol = new SocketProtocol();

        var ex = Assert.Throws<ApiException>(
            () => protocol.Handle(AuthFrame("{\"error_code\":114,\"error_message\":\"no\"}")));

        Assert.Equal(114, ex.ErrorCode);
        Assert.False(protocol.IsAuthenticated);
    }

    [Fact]
    public void Authen_without_key_throws()
    {
        var protocol = new SocketProtocol();

        Assert.Throws<InvalidDataException>(() => protocol.Handle(Frame(1, 1, "{\"encrypt\":0}")));
    }

    [Fact]
    public void Push_message_is_decrypted_with_the_negotiated_key()
    {
        SocketProtocol protocol = Authenticated();

        SocketEvent? socketEvent = protocol.Handle(
            Frame(501, 0, "{\"encrypt\":3,\"data\":\"" + Vectors.Encrypt3 + "\"}"));

        Assert.NotNull(socketEvent);
        Assert.Equal((ushort)501, socketEvent.Cmd);
        string? content = socketEvent.Payload.GetProperty("data").GetProperty("msgs")[0].GetProperty("content").GetString();
        Assert.Equal("xin chào", content);
    }

    [Fact]
    public void Encrypted_push_before_handshake_fails()
    {
        var protocol = new SocketProtocol();
        byte[] frame = Frame(501, 0, "{\"encrypt\":3,\"data\":\"" + Vectors.Encrypt3 + "\"}");

        Assert.Throws<InvalidDataException>(() => protocol.Handle(frame));
    }

    [Fact]
    public void Cmd_3000_marks_duplicate_connection()
    {
        SocketProtocol protocol = Authenticated();

        SocketEvent? socketEvent = protocol.Handle(Frame(3000, 0, "{\"encrypt\":0}"));

        Assert.True(protocol.DuplicateConnection);
        Assert.NotNull(socketEvent);
    }

    [Fact]
    public void Frame_without_body_returns_null()
    {
        SocketProtocol protocol = Authenticated();

        Assert.Null(protocol.Handle(Frame(5, 0, "")));
    }
}