using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace Luna.Protocol.Realtime;

/// <summary>
/// WebSocket connection to the server: authentication, keep-alive ping, receiving events, automatic reconnect (rotating address,
/// growing delay). Stops for good when cancelled or when the account is opened elsewhere.
/// </summary>
public sealed class SocketClient
{
    private const ushort CmdPing = 2;
    private const byte SubKeepConnection = 1;
    private const int DuplicateCloseCode = 3000;
    private const ushort CmdOldMessagesPrivate = 510;
    private const ushort CmdOldMessagesGroup = 511;
    private const byte SubOldMessages = 1;

    /// <summary>Most old-message requests that may wait to be written.</summary>
    public const int OutboxCapacity = 8;

    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly ClientContext _context;
    private readonly IReadOnlyList<string> _endpoints;
    private readonly Action<string>? _trace;
    private readonly Func<long> _unixMillis;
    private readonly Func<IWebSocketConnection> _connectionFactory;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    // Old-message requests waiting for the send loop. Bounded: a full outbox is reported to the caller, never grown.
    private readonly Channel<byte[]> _outbox = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(OutboxCapacity) { FullMode = BoundedChannelFullMode.Wait });

    /// <param name="endpoints">The <c>zpw_ws</c> list from getLoginInfo.</param>
    /// <param name="connectionFactory">Creates one connection per attempt. Defaults to a real <c>ClientWebSocket</c>.</param>
    /// <param name="delay">Waits for a duration (reconnect backoff and ping interval). Defaults to <c>Task.Delay</c>.</param>
    public SocketClient(
        ClientContext context,
        IReadOnlyList<string> endpoints,
        Action<string>? trace = null,
        Func<long>? unixMillis = null,
        Func<IWebSocketConnection>? connectionFactory = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.Count == 0)
        {
            throw new ArgumentException("At least one WebSocket address is required.", nameof(endpoints));
        }

        _context = context;
        _endpoints = endpoints;
        _trace = trace;
        _unixMillis = unixMillis ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _connectionFactory = connectionFactory ?? (() => new ClientWebSocketConnection());
        _delay = delay ?? Task.Delay;
    }

    /// <summary>
    /// Queues a request for old messages (WebSocket frame version 1, cmd 510 private or 511 group, subCmd 1; body
    /// <c>{"first":true,"lastId":&lt;id or null&gt;,"preIds":[]}</c>) [Source: zca-js 2.2.0, not verified on the live server].
    /// The frame is written by the send loop of the CURRENT connection, one at a time, only after authentication.
    /// It is NOT retried: a request queued under a connection that then drops is discarded when the next connection
    /// starts, and everything still queued is discarded when <see cref="RunAsync"/> ends. The answer arrives through the
    /// normal event callback (cmd 510 or 511, subCmd 1).
    /// </summary>
    /// <param name="lastMessageId">null for the first page, otherwise 1 to 20 ASCII digits (the id is never echoed in errors).</param>
    /// <exception cref="ArgumentException"><paramref name="lastMessageId"/> is not null and not 1 to 20 ASCII digits.</exception>
    /// <exception cref="InvalidOperationException">The outbox already holds <see cref="OutboxCapacity"/> requests.</exception>
    public void RequestOldMessages(HistoryScope scope, string? lastMessageId)
    {
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), "Unknown history scope.");
        }

        if (lastMessageId is not null && !IsValidMessageId(lastMessageId))
        {
            throw new ArgumentException("The last message id must be 1 to 20 ASCII digits.", nameof(lastMessageId));
        }

        // The id was checked above (digits only), so it needs no JSON escaping.
        string lastId = lastMessageId is null ? "null" : "\"" + lastMessageId + "\"";
        string body = "{\"first\":true,\"lastId\":" + lastId + ",\"preIds\":[]}";
        ushort cmd = scope == HistoryScope.Private ? CmdOldMessagesPrivate : CmdOldMessagesGroup;
        byte[] frame = new SocketFrame(1, cmd, SubOldMessages, body).Encode();

        if (!_outbox.Writer.TryWrite(frame))
        {
            throw new InvalidOperationException($"The old-message outbox is full ({OutboxCapacity} requests).");
        }
    }

    private static bool IsValidMessageId(string value)
    {
        if (value.Length is 0 or > 20)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private void ClearOutbox()
    {
        while (_outbox.Reader.TryRead(out _))
        {
        }
    }

    /// <summary>
    /// Builds the connection URL. The server rejects a request line without a path, so an address with only a host must get "/" appended.
    /// </summary>
    public static string BuildUrl(string endpoint, string apiVersion, string apiType, long unixMillis)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        int queryStart = endpoint.IndexOf('?', StringComparison.Ordinal);
        string host = queryStart >= 0 ? endpoint[..queryStart] : endpoint;
        string? query = queryStart >= 0 ? endpoint[(queryStart + 1)..] : null;

        int schemeEnd = host.IndexOf("://", StringComparison.Ordinal);
        string afterScheme = schemeEnd >= 0 ? host[(schemeEnd + 3)..] : host;
        if (!afterScheme.Contains('/', StringComparison.Ordinal))
        {
            host += "/";
        }

        string extra = string.Create(
            CultureInfo.InvariantCulture, $"zpw_ver={apiVersion}&zpw_type={apiType}&t={unixMillis}");
        return query is null ? $"{host}?{extra}" : $"{host}?{query}&{extra}";
    }

    /// <summary>
    /// Runs until cancelled (<paramref name="cancellationToken"/>) or the account is opened elsewhere.
    /// Network and authentication errors are reported only through <paramref name="onState"/> and then retried.
    /// Exceptions thrown from the callbacks are not swallowed.
    /// </summary>
    public async Task RunAsync(
        Action<SocketEvent> onEvent,
        Action<SocketStateChange>? onState = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onEvent);

        int endpointIndex = 0;
        TimeSpan backoff = InitialBackoff;
        bool firstAttempt = true;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string endpoint = _endpoints[endpointIndex % _endpoints.Count];
                string url = BuildUrl(endpoint, _context.ApiVersion, _context.ApiType, _unixMillis());
                var protocol = new SocketProtocol();
                onState?.Invoke(new SocketStateChange(SocketState.Connecting, new Uri(url).Host));

                // Requests queued before the very first connection belong to it; later connections start with an
                // empty outbox so a request queued under a dropped connection never fires after the reconnect.
                if (!firstAttempt)
                {
                    ClearOutbox();
                }

                firstAttempt = false;

                string reason;
                try
                {
                    reason = await RunOnceAsync(url, protocol, onEvent, onState, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException or IOException or HttpRequestException or ApiException)
                {
                    reason = ex.Message;
                }

                if (protocol.DuplicateConnection)
                {
                    onState?.Invoke(new SocketStateChange(SocketState.DuplicateConnection, "The account is open elsewhere."));
                    return;
                }

                onState?.Invoke(new SocketStateChange(SocketState.Disconnected, reason));

                if (protocol.IsAuthenticated)
                {
                    backoff = InitialBackoff;
                }

                endpointIndex++;
                await _delay(backoff, cancellationToken).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, backoff.TotalSeconds * 2));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Deliberate stop, not an error.
        }
        finally
        {
            // Cancel, duplicate connection or an exception from a callback: nothing queued may fire later.
            ClearOutbox();
        }
    }

    private async Task<string> RunOnceAsync(
        string url,
        SocketProtocol protocol,
        Action<SocketEvent> onEvent,
        Action<SocketStateChange>? onState,
        CancellationToken ct)
    {
        using IWebSocketConnection socket = _connectionFactory();

        // The headers hold the session cookie: they must never be traced.
        var headers = new Dictionary<string, string>
        {
            ["Origin"] = "https://chat.zalo.me",
            ["User-Agent"] = _context.UserAgent,
        };

        // Like zca-js: cookies are taken for https://chat.zalo.me.
        string cookie = _context.Cookies.GetCookieHeader(new Uri("https://chat.zalo.me"));
        if (cookie.Length > 0)
        {
            headers["Cookie"] = cookie;
        }

        await socket.ConnectAsync(new Uri(url), headers, ct).ConfigureAwait(false);

        // One write at a time per connection: ClientWebSocket throws on overlapping SendAsync calls, and both the ping
        // loop and the send loop write. Disposed only after both loops were awaited below.
        using var sendGate = new SemaphoreSlim(1, 1);
        var authenticated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var loopsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task pingTask = PingLoopAsync(socket, protocol, sendGate, loopsCts.Token);
        Task sendTask = SendLoopAsync(socket, protocol, authenticated.Task, sendGate, loopsCts.Token);
        try
        {
            return await ReceiveLoopAsync(socket, protocol, onEvent, onState, authenticated, ct).ConfigureAwait(false);
        }
        finally
        {
            await loopsCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await EndLoopAsync(pingTask).ConfigureAwait(false);
            }
            finally
            {
                await EndLoopAsync(sendTask).ConfigureAwait(false);
            }
        }
    }

    private static async Task EndLoopAsync(Task loop)
    {
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // The loops end together with the connection.
        }
    }

    private async Task<string> ReceiveLoopAsync(
        IWebSocketConnection socket,
        SocketProtocol protocol,
        Action<SocketEvent> onEvent,
        Action<SocketStateChange>? onState,
        TaskCompletionSource authenticated,
        CancellationToken ct)
    {
        byte[] buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        bool reportedConnected = false;

        while (socket.State == WebSocketState.Open)
        {
            message.SetLength(0);
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    string reason = $"The server closed the connection ({(int?)socket.CloseStatus}: {socket.CloseStatusDescription})";
                    await TryCloseAsync(socket, WebSocketCloseStatus.NormalClosure, "bye").ConfigureAwait(false);
                    return reason;
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Binary)
            {
                continue;
            }

            SocketEvent? socketEvent;
            try
            {
                socketEvent = protocol.Handle(message.GetBuffer().AsSpan(0, (int)message.Length));
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or JsonException or CryptographicException)
            {
                _trace?.Invoke($"Skipped a frame that could not be decoded: {ex.GetType().Name}");
                continue;
            }

            if (!reportedConnected && protocol.IsAuthenticated)
            {
                reportedConnected = true;
                authenticated.TrySetResult();
                onState?.Invoke(new SocketStateChange(SocketState.Connected));
            }

            if (socketEvent is not null)
            {
                onEvent(socketEvent);
            }

            if (protocol.DuplicateConnection)
            {
                await TryCloseAsync(socket, (WebSocketCloseStatus)DuplicateCloseCode, "duplicate connection")
                    .ConfigureAwait(false);
                return "The account is open elsewhere.";
            }
        }

        return $"The connection is closed ({socket.State}).";
    }

    private async Task PingLoopAsync(
        IWebSocketConnection socket, SocketProtocol protocol, SemaphoreSlim sendGate, CancellationToken ct)
    {
        // A plain delay loop instead of PeriodicTimer so the delay can be injected; the interval drifts by the time
        // spent sending a ping, which is negligible for a keep-alive.
        while (true)
        {
            await _delay(PingInterval, ct).ConfigureAwait(false);
            if (!protocol.IsAuthenticated || socket.State != WebSocketState.Open)
            {
                continue;
            }

            string body = string.Create(CultureInfo.InvariantCulture, $"{{\"eventId\":{_unixMillis()}}}");
            byte[] frame = new SocketFrame(1, CmdPing, SubKeepConnection, body).Encode();
            await SendSerializedAsync(socket, frame, sendGate, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes the queued old-message requests, one at a time, only while the connection is authenticated and open.
    /// Requests queued before authentication simply wait in the outbox. Ends with the connection (its token).
    /// </summary>
    private async Task SendLoopAsync(
        IWebSocketConnection socket,
        SocketProtocol protocol,
        Task authenticated,
        SemaphoreSlim sendGate,
        CancellationToken ct)
    {
        await authenticated.WaitAsync(ct).ConfigureAwait(false);
        while (true)
        {
            byte[] frame = await _outbox.Reader.ReadAsync(ct).ConfigureAwait(false);
            if (!protocol.IsAuthenticated || socket.State != WebSocketState.Open)
            {
                // The connection is going away; a request is never carried over to another connection.
                return;
            }

            await SendSerializedAsync(socket, frame, sendGate, ct).ConfigureAwait(false);
        }
    }

    private static async Task SendSerializedAsync(
        IWebSocketConnection socket, byte[] frame, SemaphoreSlim sendGate, CancellationToken ct)
    {
        await sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A release and a cancel can race; a canceled waiter must not start a send.
            ct.ThrowIfCancellationRequested();
            await socket.SendAsync(frame.AsMemory(), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }
        finally
        {
            sendGate.Release();
        }
    }

    private static async Task TryCloseAsync(IWebSocketConnection socket, WebSocketCloseStatus status, string description)
    {
        try
        {
            await socket.CloseOutputAsync(status, description, CancellationToken.None).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
            // The peer already closed first, which is fine.
        }
    }
}