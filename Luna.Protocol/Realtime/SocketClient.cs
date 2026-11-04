using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

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

    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly ClientContext _context;
    private readonly IReadOnlyList<string> _endpoints;
    private readonly Action<string>? _trace;
    private readonly Func<long> _unixMillis;
    private readonly Func<IWebSocketConnection> _connectionFactory;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

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

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string endpoint = _endpoints[endpointIndex % _endpoints.Count];
                string url = BuildUrl(endpoint, _context.ApiVersion, _context.ApiType, _unixMillis());
                var protocol = new SocketProtocol();
                onState?.Invoke(new SocketStateChange(SocketState.Connecting, new Uri(url).Host));

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

        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task pingTask = PingLoopAsync(socket, protocol, pingCts.Token);
        try
        {
            return await ReceiveLoopAsync(socket, protocol, onEvent, onState, ct).ConfigureAwait(false);
        }
        finally
        {
            await pingCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await pingTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
                // The ping loop ends together with the connection.
            }
        }
    }

    private async Task<string> ReceiveLoopAsync(
        IWebSocketConnection socket,
        SocketProtocol protocol,
        Action<SocketEvent> onEvent,
        Action<SocketStateChange>? onState,
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

    private async Task PingLoopAsync(IWebSocketConnection socket, SocketProtocol protocol, CancellationToken ct)
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
            await socket.SendAsync(frame.AsMemory(), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
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