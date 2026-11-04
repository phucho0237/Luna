using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Xunit;
using Luna.Protocol.Realtime;
using Luna.Protocol.Tests.Support;

namespace Luna.Protocol.Tests.Realtime;

/// <summary>
/// Characterization of the SocketClient connect/reconnect loop with a scripted fake connection and a fake delay:
/// no network and no real waiting.
/// </summary>
public class SocketClientReconnectTests
{
    private const long StartMillis = 1_700_000_000_000;

    private static byte[] Frame(ushort cmd, byte subCmd, string json) => new SocketFrame(1, cmd, subCmd, json).Encode();

    private static byte[] AuthFrame() =>
        Frame(1, 1, "{\"key\":\"" + Vectors.CipherKey + "\",\"encrypt\":0,\"data\":"
            + JsonSerializer.Serialize("{\"error_code\":0,\"data\":{}}") + "}");

    private static byte[] EventFrame(ushort cmd = 501) =>
        Frame(cmd, 0, "{\"encrypt\":3,\"data\":\"" + Vectors.Encrypt3 + "\"}");

    private static byte[] DuplicateFrame() => Frame(3000, 0, "{\"encrypt\":0}");

    private static async Task<SocketStateChange> NextState(Rig rig, SocketState expected)
    {
        SocketStateChange change = await rig.States.NextAsync();
        Assert.Equal(expected, change.State);
        return change;
    }

    [Fact]
    public async Task Connect_passes_url_and_headers()
    {
        using var rig = new Rig("wss://ws1.chat.zalo.me");
        rig.Context.AddCookies("a=1; b=2");
        FakeWebSocketConnection conn = rig.Script();
        conn.PushFrame(AuthFrame());
        rig.Start();

        SocketStateChange connecting = await NextState(rig, SocketState.Connecting);
        await NextState(rig, SocketState.Connected);

        Assert.Equal("ws1.chat.zalo.me", connecting.Detail);
        Assert.Equal(
            "wss://ws1.chat.zalo.me/?zpw_ver=694&zpw_type=30&t=1700000000000", conn.ConnectUri!.AbsoluteUri);
        IReadOnlyDictionary<string, string> headers = conn.ConnectHeaders!;
        Assert.Equal("https://chat.zalo.me", headers["Origin"]);
        Assert.Equal("test-agent/1.0", headers["User-Agent"]);
        Assert.Contains("a=1", headers["Cookie"], StringComparison.Ordinal);
        Assert.Contains("b=2", headers["Cookie"], StringComparison.Ordinal);
        Assert.Equal(3, headers.Count);
        await rig.StopAsync();
    }

    [Fact]
    public async Task Connect_without_cookies_sends_no_cookie_header()
    {
        using var rig = new Rig("wss://ws1.chat.zalo.me");
        FakeWebSocketConnection conn = rig.Script();
        conn.PushFrame(AuthFrame());
        rig.Start();

        await NextState(rig, SocketState.Connecting);
        await NextState(rig, SocketState.Connected);

        Assert.False(conn.ConnectHeaders!.ContainsKey("Cookie"));
        await rig.StopAsync();
    }

    [Fact]
    public async Task Connected_is_raised_once_after_authentication_and_events_reach_the_callback()
    {
        using var rig = new Rig("wss://ws1.chat.zalo.me");
        FakeWebSocketConnection conn = rig.Script();
        conn.PushFrame(AuthFrame());
        conn.PushFrame(EventFrame());
        rig.Start();

        SocketEvent auth = await rig.Events.NextAsync();
        SocketEvent push = await rig.Events.NextAsync();

        Assert.Equal((ushort)1, auth.Cmd);
        Assert.Equal((ushort)501, push.Cmd);
        string? msgId = push.Payload.GetProperty("data").GetProperty("msgs")[0].GetProperty("msgId").GetString();
        Assert.Equal("1", msgId);
        Assert.Equal(
            [SocketState.Connecting, SocketState.Connected],
            rig.States.All.Select(s => s.State).ToArray());
        await rig.StopAsync();
    }

    [Fact]
    public async Task Ping_is_sent_only_after_authentication_once_per_tick()
    {
        using var rig = new Rig("wss://ws1.chat.zalo.me");
        FakeWebSocketConnection conn = rig.Script();
        rig.Start();

        DelayRequest tick1 = await rig.Delay.Pings.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(15), tick1.Duration);
        tick1.Complete();
        DelayRequest tick2 = await rig.Delay.Pings.NextAsync();
        Assert.Empty(conn.Sent);

        conn.PushFrame(AuthFrame());
        await NextState(rig, SocketState.Connecting);
        await NextState(rig, SocketState.Connected);
        Assert.Empty(conn.Sent);

        rig.Now = StartMillis + 15_000;
        tick2.Complete();
        DelayRequest tick3 = await rig.Delay.Pings.NextAsync();
        Assert.Single(conn.Sent);
        AssertPing(conn.Sent[0], StartMillis + 15_000);

        rig.Now = StartMillis + 30_000;
        tick3.Complete();
        await rig.Delay.Pings.NextAsync();
        Assert.Equal(2, conn.Sent.Count);
        AssertPing(conn.Sent[1], StartMillis + 30_000);
        await rig.StopAsync();
    }

    private static void AssertPing(byte[] data, long expectedEventId)
    {
        SocketFrame frame = SocketFrame.Decode(data);
        Assert.Equal((byte)1, frame.Version);
        Assert.Equal((ushort)2, frame.Cmd);
        Assert.Equal((byte)1, frame.SubCmd);
        Assert.Equal("{\"eventId\":" + expectedEventId + "}", frame.Body);
    }

    [Fact]
    public async Task Connect_failure_reports_disconnected_and_rotates_endpoints_with_wrap_around()
    {
        using var rig = new Rig("wss://ws1.chat.zalo.me", "wss://ws2.chat.zalo.me");
        FakeWebSocketConnection c1 = rig.ScriptFailure();
        FakeWebSocketConnection c2 = rig.ScriptFailure();
        FakeWebSocketConnection c3 = rig.ScriptFailure();
        rig.Start();

        await NextState(rig, SocketState.Connecting);
        SocketStateChange down = await NextState(rig, SocketState.Disconnected);
        Assert.Equal("boom", down.Detail);
        Assert.Equal("ws1.chat.zalo.me", c1.ConnectUri!.Host);
        (await rig.Delay.Backoffs.NextAsync()).Complete();

        await NextState(rig, SocketState.Connecting);
        await NextState(rig, SocketState.Disconnected);
        Assert.Equal("ws2.chat.zalo.me", c2.ConnectUri!.Host);
        (await rig.Delay.Backoffs.NextAsync()).Complete();

        await NextState(rig, SocketState.Connecting);
        await NextState(rig, SocketState.Disconnected);
        Assert.Equal("ws1.chat.zalo.me", c3.ConnectUri!.Host);
        await rig.StopAsync();
    }

    [Fact]
    public async Task Backoff_doubles_from_two_seconds_up_to_thirty()
    {
        using var rig = new Rig("wss://ws1.chat.zalo.me");
        for (int i = 0; i < 7; i++)
        {
            rig.ScriptFailure();
        }

        rig.Start();
        for (int i = 0; i < 6; i++)
        {
            (await rig.Delay.Backoffs.NextAsync()).Complete();
        }

        await rig.Delay.Backoffs.NextAsync();

        Assert.Equal(
            [2, 4, 8, 16, 30, 30, 30],
            rig.Delay.Backoffs.All.Select(r => (int)r.Duration.TotalSeconds).ToArray());
        await rig.StopAsync();
    }

    [Fact]
    public async Task Backoff_resets_to_two_seconds_after_a_session_that_authenticated()
    {
        using var rig = new Rig("wss://ws1.chat.zalo.me");
        rig.ScriptFailure();
        rig.ScriptFailure();
        FakeWebSocketConnection session = rig.Script();
        session.PushFrame(AuthFrame());
        session.PushClose(WebSocketCloseStatus.EndpointUnavailable, "maintenance");
        rig.ScriptFailure();
        rig.Start();

        for (int i = 0; i < 3; i++)
        {
            (await rig.Delay.Backoffs.NextAsync()).Complete();
        }

        await rig.Delay.Backoffs.NextAsync();

        Assert.Equal(
            [2, 4, 2, 4],
            rig.Delay.Backoffs.All.Select(r => (int)r.Duration.TotalSeconds).ToArray());
        await rig.StopAsync();
    }

    [Fact]
    public async Task Duplicate_connection_stops_without_reconnecting()
    {
        using var rig = new Rig("wss://ws1.chat.zalo.me");
        FakeWebSocketConnection conn = rig.Script();
        conn.PushFrame(AuthFrame());
        conn.PushFrame(DuplicateFrame());
        rig.Start();

        await rig.Run.Soon();

        Assert.Equal(
            [SocketState.Connecting, SocketState.Connected, SocketState.DuplicateConnection],
            rig.States.All.Select(s => s.State).ToArray());
        Assert.Equal([(WebSocketCloseStatus)3000], conn.Closes.Select(c => c.Status).ToArray());
        Assert.True(conn.Disposed);
        Assert.Single(rig.Created);
        Assert.Empty(rig.Delay.Backoffs.All);
        Assert.Equal((ushort)3000, rig.Events.All[^1].Cmd);
    }

    [Fact]
    public async Task Cancel_during_the_backoff_delay_returns_normally_without_another_connect()
    {
        using var rig = new Rig("wss://ws1.chat.zalo.me");
        FakeWebSocketConnection conn = rig.ScriptFailure();
        rig.ScriptFailure();
        rig.Start();
        await NextState(rig, SocketState.Connecting);
        await NextState(rig, SocketState.Disconnected);
        DelayRequest backoff = await rig.Delay.Backoffs.NextAsync();

        await rig.StopAsync();

        Assert.True(backoff.IsCanceled);
        Assert.Single(rig.Created);
        Assert.True(conn.Disposed);
    }

    [Fact]
    public async Task Cancel_during_receive_returns_normally_and_ends_the_ping_loop()
    {
        using var rig = new Rig("wss://ws1.chat.zalo.me");
        FakeWebSocketConnection conn = rig.Script();
        rig.ScriptFailure();
        conn.PushFrame(AuthFrame());
        rig.Start();
        await NextState(rig, SocketState.Connecting);
        await NextState(rig, SocketState.Connected);
        DelayRequest tick = await rig.Delay.Pings.NextAsync();

        await rig.StopAsync();

        Assert.True(tick.IsCanceled);
        Assert.True(conn.Disposed);
        Assert.Single(rig.Created);
        Assert.Empty(rig.Delay.Backoffs.All);
    }

    [Fact]
    public async Task Server_close_reports_disconnected_and_closes_the_output()
    {
        using var rig = new Rig("wss://ws1.chat.zalo.me");
        FakeWebSocketConnection conn = rig.Script();
        conn.PushClose(WebSocketCloseStatus.EndpointUnavailable, "maintenance");
        rig.Start();

        await NextState(rig, SocketState.Connecting);
        SocketStateChange down = await NextState(rig, SocketState.Disconnected);
        DelayRequest backoff = await rig.Delay.Backoffs.NextAsync();

        Assert.Equal("The server closed the connection (1001: maintenance)", down.Detail);
        Assert.Equal(
            [(WebSocketCloseStatus.NormalClosure, (string?)"bye")],
            conn.Closes.ToArray());
        Assert.Equal(TimeSpan.FromSeconds(2), backoff.Duration);
        Assert.True(conn.Disposed);
        await rig.StopAsync();
    }

    [Fact]
    public async Task Text_frame_is_ignored_and_undecodable_frame_is_skipped_without_ending_the_session()
    {
        using var rig = new Rig("wss://ws1.chat.zalo.me");
        FakeWebSocketConnection conn = rig.Script();
        conn.PushFrame(AuthFrame(), type: WebSocketMessageType.Text);
        conn.PushFrame([1, 2]);
        conn.PushFrame(AuthFrame());
        conn.PushFrame(EventFrame());
        rig.Start();

        SocketEvent first = await rig.Events.NextAsync();
        SocketEvent second = await rig.Events.NextAsync();

        Assert.Equal((ushort)1, first.Cmd);
        Assert.Equal((ushort)501, second.Cmd);
        Assert.Equal(
            ["Skipped a frame that could not be decoded: InvalidDataException"],
            rig.Traces.All.ToArray());
        Assert.Equal(
            [SocketState.Connecting, SocketState.Connected],
            rig.States.All.Select(s => s.State).ToArray());
        Assert.False(conn.Disposed);
        await rig.StopAsync();
    }

    [Fact]
    public async Task Message_split_over_two_receives_yields_one_event()
    {
        using var rig = new Rig("wss://ws1.chat.zalo.me");
        FakeWebSocketConnection conn = rig.Script();
        byte[] push = EventFrame();
        conn.PushFrame(AuthFrame());
        conn.PushFrame(push[..20], endOfMessage: false);
        conn.PushFrame(push[20..], endOfMessage: true);
        conn.PushFrame(EventFrame(502));
        rig.Start();

        await rig.Events.NextAsync();
        SocketEvent joined = await rig.Events.NextAsync();
        SocketEvent next = await rig.Events.NextAsync();

        Assert.Equal((ushort)501, joined.Cmd);
        Assert.Equal((ushort)502, next.Cmd);
        string? msgId = joined.Payload.GetProperty("data").GetProperty("msgs")[0].GetProperty("msgId").GetString();
        Assert.Equal("1", msgId);
        Assert.Equal(3, rig.Events.All.Count);
        await rig.StopAsync();
    }

    [Fact]
    public async Task Exception_from_the_event_callback_propagates_and_disposes_the_connection()
    {
        using var rig = new Rig("wss://ws1.chat.zalo.me");
        FakeWebSocketConnection conn = rig.Script();
        conn.PushFrame(AuthFrame());
        conn.PushFrame(EventFrame());
        rig.Start(e =>
        {
            if (e.Cmd == 501)
            {
                throw new InvalidOperationException("callback failed");
            }
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Run.Soon());

        DelayRequest tick = await rig.Delay.Pings.NextAsync();
        Assert.True(tick.IsCanceled);
        Assert.True(conn.Disposed);
        Assert.Single(rig.Created);
        Assert.Empty(rig.Delay.Backoffs.All);
    }

    /// <summary>Wires a SocketClient to fake connections, a fake delay and a fake clock; records states, events, traces.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly ConcurrentQueue<FakeWebSocketConnection> _script = new();
        private readonly List<FakeWebSocketConnection> _created = [];
        private readonly CancellationTokenSource _cts = new();
        private readonly SocketClient _client;
        private Task? _run;

        public Rig(params string[] endpoints)
        {
            Context = new ClientContext("imei", "test-agent/1.0");
            _client = new SocketClient(
                Context,
                endpoints,
                Traces.Add,
                () => Interlocked.Read(ref _now),
                Next,
                Delay.Delay);
        }

        private long _now = StartMillis;

        public ClientContext Context { get; }

        public FakeDelay Delay { get; } = new();

        public AsyncQueue<SocketStateChange> States { get; } = new();

        public AsyncQueue<SocketEvent> Events { get; } = new();

        public AsyncQueue<string> Traces { get; } = new();

        public long Now
        {
            set => Interlocked.Exchange(ref _now, value);
        }

        public IReadOnlyList<FakeWebSocketConnection> Created
        {
            get
            {
                lock (_created)
                {
                    return [.. _created];
                }
            }
        }

        public FakeWebSocketConnection Script() => Enqueue(new FakeWebSocketConnection());

        public FakeWebSocketConnection ScriptFailure() =>
            Enqueue(new FakeWebSocketConnection { ConnectFailure = new WebSocketException("boom") });

        /// <summary>The RunAsync task started by <see cref="Start"/>.</summary>
        public Task Run => _run!;

        public void Start(Action<SocketEvent>? onEvent = null)
        {
            _run = _client.RunAsync(
                e =>
                {
                    Events.Add(e);
                    onEvent?.Invoke(e);
                },
                States.Add,
                _cts.Token);
        }

        /// <summary>Cancels and waits for RunAsync, which must return normally.</summary>
        public async Task StopAsync()
        {
            await _cts.CancelAsync();
            await _run!.Soon();
        }

        public void Dispose() => _cts.Dispose();

        private FakeWebSocketConnection Enqueue(FakeWebSocketConnection connection)
        {
            _script.Enqueue(connection);
            return connection;
        }

        private IWebSocketConnection Next()
        {
            if (!_script.TryDequeue(out FakeWebSocketConnection? connection))
            {
                throw new InvalidOperationException("No scripted connection is left.");
            }

            lock (_created)
            {
                _created.Add(connection);
            }

            return connection;
        }
    }
}
