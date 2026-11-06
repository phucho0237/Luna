using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Xunit;
using Luna.Protocol.Realtime;
using Luna.Protocol.Tests.Support;

namespace Luna.Protocol.Tests.Realtime;

/// <summary>
/// RequestOldMessages and the send loop (frames 510/511) with a scripted fake connection and a fake delay: no network.
/// </summary>
public class SocketClientHistoryTests
{
    private static byte[] Frame(ushort cmd, byte subCmd, string json) => new SocketFrame(1, cmd, subCmd, json).Encode();

    private static byte[] AuthFrame() =>
        Frame(1, 1, "{\"key\":\"" + Vectors.CipherKey + "\",\"encrypt\":0,\"data\":"
            + JsonSerializer.Serialize("{\"error_code\":0,\"data\":{}}") + "}");

    private static byte[] Expected(byte cmdLow, byte cmdHigh, string body)
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        byte[] frame = new byte[4 + bodyBytes.Length];
        frame[0] = 1;
        frame[1] = cmdLow;
        frame[2] = cmdHigh;
        frame[3] = 1;
        bodyBytes.CopyTo(frame, 4);
        return frame;
    }

    private static async Task<SocketRig> ConnectedRig(Action<FakeWebSocketConnection>? configure = null)
    {
        var rig = new SocketRig("wss://ws1.chat.zalo.me");
        FakeWebSocketConnection conn = rig.Script();
        configure?.Invoke(conn);
        conn.PushFrame(AuthFrame());
        rig.Start();
        await rig.NextState(SocketState.Connecting);
        await rig.NextState(SocketState.Connected);
        return rig;
    }

    [Theory]
    [InlineData(HistoryScope.Private, null, 0xFE, 0x01, "{\"first\":true,\"lastId\":null,\"preIds\":[]}")]
    [InlineData(HistoryScope.Private, "123", 0xFE, 0x01, "{\"first\":true,\"lastId\":\"123\",\"preIds\":[]}")]
    [InlineData(HistoryScope.Group, null, 0xFF, 0x01, "{\"first\":true,\"lastId\":null,\"preIds\":[]}")]
    [InlineData(HistoryScope.Group, "18446744073709551615", 0xFF, 0x01,
        "{\"first\":true,\"lastId\":\"18446744073709551615\",\"preIds\":[]}")]
    public async Task Request_is_sent_after_authentication_with_the_exact_frame_bytes(
        HistoryScope scope, string? lastId, int cmdLow, int cmdHigh, string body)
    {
        using SocketRig rig = await ConnectedRig();
        FakeWebSocketConnection conn = rig.Created[0];

        rig.Client.RequestOldMessages(scope, lastId);
        byte[] sent = await conn.SentFrames.NextAsync();

        Assert.Equal(Expected((byte)cmdLow, (byte)cmdHigh, body), sent);
        await rig.StopAsync();
    }

    [Fact]
    public async Task Nothing_is_sent_before_authentication_and_a_request_queued_earlier_is_sent_after_it()
    {
        using var rig = new SocketRig("wss://ws1.chat.zalo.me");
        FakeWebSocketConnection conn = rig.Script();
        rig.Client.RequestOldMessages(HistoryScope.Private, "42");
        rig.Start();
        await rig.NextState(SocketState.Connecting);

        await Task.Delay(100);
        Assert.Empty(conn.Sent);

        conn.PushFrame(AuthFrame());
        await rig.NextState(SocketState.Connected);
        byte[] sent = await conn.SentFrames.NextAsync();

        Assert.Equal(Expected(0xFE, 0x01, "{\"first\":true,\"lastId\":\"42\",\"preIds\":[]}"), sent);
        Assert.Single(conn.Sent);
        await rig.StopAsync();
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12a4")]
    [InlineData("")]
    [InlineData("123456789012345678901")]
    [InlineData("-1")]
    [InlineData(" 12")]
    [InlineData("1 2")]
    [InlineData("12\n")]
    [InlineData("١٢")]
    public void Invalid_last_ids_are_rejected_without_echoing_the_value(string lastId)
    {
        var client = new SocketClient(new ClientContext("imei", "agent"), ["wss://ws1.chat.zalo.me"]);

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => client.RequestOldMessages(HistoryScope.Private, lastId));

        if (lastId.Trim().Length > 0)
        {
            Assert.DoesNotContain(lastId.Trim(), ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_undefined_scope_is_rejected()
    {
        var client = new SocketClient(new ClientContext("imei", "agent"), ["wss://ws1.chat.zalo.me"]);

        Assert.Throws<ArgumentOutOfRangeException>(() => client.RequestOldMessages((HistoryScope)7, null));
    }

    [Fact]
    public async Task The_outbox_holds_eight_requests_and_is_emptied_when_RunAsync_ends()
    {
        using var rig = new SocketRig("wss://ws1.chat.zalo.me");
        FakeWebSocketConnection conn = rig.Script();
        for (int i = 0; i < 8; i++)
        {
            rig.Client.RequestOldMessages(HistoryScope.Group, null);
        }

        InvalidOperationException full = Assert.Throws<InvalidOperationException>(
            () => rig.Client.RequestOldMessages(HistoryScope.Group, "99"));
        Assert.DoesNotContain("99", full.Message, StringComparison.Ordinal);

        rig.Start();
        await rig.NextState(SocketState.Connecting);
        await rig.StopAsync();

        for (int i = 0; i < 8; i++)
        {
            rig.Client.RequestOldMessages(HistoryScope.Group, null);
        }

        Assert.Throws<InvalidOperationException>(() => rig.Client.RequestOldMessages(HistoryScope.Group, null));
        Assert.Empty(conn.Sent);
    }

    [Fact]
    public async Task The_send_loop_ends_with_the_connection_so_the_reconnect_backoff_is_reached()
    {
        using SocketRig rig = await ConnectedRig();
        FakeWebSocketConnection conn = rig.Created[0];
        rig.Client.RequestOldMessages(HistoryScope.Private, null);
        await conn.SentFrames.NextAsync();

        conn.PushClose(WebSocketCloseStatus.EndpointUnavailable, "maintenance");

        await rig.NextState(SocketState.Disconnected);
        DelayRequest backoff = await rig.Delay.Backoffs.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(2), backoff.Duration);
        Assert.True(conn.Disposed);
        await rig.StopAsync();
    }

    [Fact]
    public async Task The_send_loop_ends_on_cancel()
    {
        using SocketRig rig = await ConnectedRig();
        FakeWebSocketConnection conn = rig.Created[0];

        await rig.StopAsync();

        Assert.True(conn.Disposed);
        Assert.Empty(conn.Sent);
    }

    [Fact]
    public async Task Ping_and_requests_share_the_connection()
    {
        using SocketRig rig = await ConnectedRig();
        FakeWebSocketConnection conn = rig.Created[0];
        DelayRequest tick = await rig.Delay.Pings.NextAsync();

        rig.Client.RequestOldMessages(HistoryScope.Private, null);
        SocketFrame history = SocketFrame.Decode(await conn.SentFrames.NextAsync());
        tick.Complete();
        SocketFrame ping = SocketFrame.Decode(await conn.SentFrames.NextAsync());

        Assert.Equal((ushort)510, history.Cmd);
        Assert.Equal((ushort)2, ping.Cmd);
        await rig.StopAsync();
    }

    [Fact]
    public async Task A_ping_and_a_request_never_call_SendAsync_at_the_same_time()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using SocketRig rig = await ConnectedRig(c => c.SendGate = gate);
        FakeWebSocketConnection conn = rig.Created[0];
        DelayRequest tick = await rig.Delay.Pings.NextAsync();

        rig.Client.RequestOldMessages(HistoryScope.Private, null);
        await conn.SendStarted.NextAsync();
        tick.Complete();
        Task<int> secondStart = conn.SendStarted.NextAsync();
        await Task.WhenAny(secondStart, Task.Delay(200));

        gate.SetResult();
        await secondStart;
        byte[] first = await conn.SentFrames.NextAsync();
        byte[] second = await conn.SentFrames.NextAsync();

        Assert.Equal(1, conn.MaxConcurrentSends);
        Assert.Equal(
            [(ushort)2, (ushort)510],
            new[] { SocketFrame.Decode(first).Cmd, SocketFrame.Decode(second).Cmd }.Order().ToArray());
        await rig.StopAsync();
    }

    [Fact]
    public async Task Cancel_while_waiting_for_the_write_gate_ends_the_loops_without_sending()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using SocketRig rig = await ConnectedRig(c => c.SendGate = gate);
        FakeWebSocketConnection conn = rig.Created[0];
        DelayRequest tick = await rig.Delay.Pings.NextAsync();
        rig.Client.RequestOldMessages(HistoryScope.Private, null);
        await conn.SendStarted.NextAsync();
        tick.Complete();
        await Task.Delay(50);

        await rig.StopAsync();

        Assert.Empty(conn.Sent);
        Assert.Single(conn.SendStarted.All);
        Assert.True(conn.Disposed);
    }

    [Fact]
    public async Task A_request_queued_under_a_dropped_connection_is_not_sent_after_the_reconnect()
    {
        using var rig = new SocketRig("wss://ws1.chat.zalo.me");
        FakeWebSocketConnection first = rig.Script();
        FakeWebSocketConnection second = rig.Script();
        rig.Start();
        await rig.NextState(SocketState.Connecting);
        rig.Client.RequestOldMessages(HistoryScope.Private, "111");
        first.PushClose(WebSocketCloseStatus.EndpointUnavailable, "maintenance");
        await rig.NextState(SocketState.Disconnected);
        (await rig.Delay.Backoffs.NextAsync()).Complete();
        await rig.NextState(SocketState.Connecting);

        second.PushFrame(AuthFrame());
        await rig.NextState(SocketState.Connected);
        rig.Client.RequestOldMessages(HistoryScope.Group, "222");
        SocketFrame sent = SocketFrame.Decode(await second.SentFrames.NextAsync());

        Assert.Equal((ushort)511, sent.Cmd);
        Assert.Contains("\"222\"", sent.Body, StringComparison.Ordinal);
        Assert.Single(second.Sent);
        Assert.Empty(first.Sent);
        await rig.StopAsync();
    }
}
