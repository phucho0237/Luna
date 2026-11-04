using System.Globalization;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Luna.Protocol.Auth;
using Luna.Protocol.Crypto;
using Luna.Protocol.Http;

namespace Luna.Protocol.Api;

/// <summary>
/// Client for post-login data APIs: picks the host from the ServiceMap, encrypts parameters with <see cref="SessionCipher"/>,
/// decrypts the two-layer response, turns <c>error_code</c> into <see cref="ApiException"/>,
/// fails over to a fallback host on network error/timeout/HTTP 5xx, and rate-limits.
/// </summary>
public sealed class ApiClient : IDisposable
{
    // [Live] page 2 of getfriends (count 50) on an account with about 50 friends returned 112; the meaning "past the end of the list"
    // is inferred from that and from the zca-js note on getSentFriendRequest, not verified for a page in the middle of the list.
    private const int NoMoreDataErrorCode = 112;

    // [Source] zca-js and ctdozlite both fetch the friend list with ONE getfriends request with page 1, count 20000.
    // Not run on the live server (not verified on the live server).
    private const int FriendListCount = 20000;

    // Cooldown after HTTP 429: starts at 60 seconds, doubles on each consecutive 429, at most 15 minutes. Reset on success.
    // The server's real threshold is unknown (not verified on the live server), so these values are deliberately conservative.
    private static readonly TimeSpan BaseCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(15);

    private readonly ClientContext _context;
    private readonly LoginInfo _loginInfo;
    private readonly ApiClientOptions _options;
    private readonly ProtocolHttpClient _http;
    private readonly SessionCipher _cipher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _clock;
    private DateTimeOffset? _lastStart;

    // The cooldown state has its own lock (it does not wait for _gate) so a blocked call fails immediately, even while another request is running.
    private readonly object _cooldownLock = new();
    private DateTimeOffset? _blockedUntil;
    private TimeSpan _currentCooldown;
    private int _consecutive429;

    // Per-endpoint spacing: own lock, does not wait for _gate. The mark is recorded when the FIRST request of a call starts being sent.
    private readonly Dictionary<string, TimeSpan> _endpointIntervals;
    private readonly Dictionary<string, DateTimeOffset> _endpointLastStart = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _endpointLock = new();

    /// <param name="handler">Only pass in tests.</param>
    public ApiClient(
        ClientContext context,
        LoginInfo loginInfo,
        ApiClientOptions? options = null,
        HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(loginInfo);
        _context = context;
        _loginInfo = loginInfo;
        _options = options ?? new ApiClientOptions();
        _delay = _options.Delay ?? ((span, token) => Task.Delay(span, token));
        _clock = _options.Clock ?? (() => DateTimeOffset.UtcNow);
        _endpointIntervals = new Dictionary<string, TimeSpan>(_options.EndpointMinIntervals, StringComparer.OrdinalIgnoreCase);
        _cipher = new SessionCipher(loginInfo.ZpwEnk);
        _http = new ProtocolHttpClient(context, handler);
    }

    /// <summary>
    /// GET <c>{host}{path}?params=...&amp;zpw_ver=...&amp;zpw_type=...</c> (no signkey) and returns the inner <c>data</c>
    /// part of the decrypted response.
    /// </summary>
    /// <exception cref="ApiException">error_code at the outer or inner layer is not 0.</exception>
    /// <exception cref="RateLimitedException">HTTP 429.</exception>
    /// <exception cref="InvalidDataException">Malformed response, or the ServiceMap lacks the service.</exception>
    public async Task<JsonElement> GetEncryptedAsync(
        string service,
        string path,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        string[] baseHosts = PrepareCall(service, path, payloadJson, cancellationToken);

        string encrypted = _cipher.Encrypt(payloadJson);
        var query = new[] { new KeyValuePair<string, string>("params", encrypted) };
        int attempts = Math.Max(1, Math.Min(_options.MaxHostAttempts, baseHosts.Length));

        ExceptionDispatchInfo? last = null;
        for (int index = 0; index < attempts; index++)
        {
            _options.Trace?.Invoke($"api {service} {path} host#{index} attempt {index + 1}/{attempts}");
            string baseUrl = baseHosts[index] + path;

            string body;
            try
            {
                // Per-endpoint spacing applies to the first attempt only; failover within the same call is not blocked by its own mark.
                body = await SendThrottledAsync(baseUrl, path, index == 0, query, null, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null || (int)ex.StatusCode.Value >= 500)
            {
                last = ExceptionDispatchInfo.Capture(ex);
                continue;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // HttpClient timeout, not a cancellation by the caller.
                last = ExceptionDispatchInfo.Capture(ex);
                continue;
            }

            ResetCooldownCounter();
            return ParseResponse(body);
        }

        last!.Throw();
        throw new InvalidOperationException("Unreachable.");
    }

    /// <summary>
    /// WRITE path: POST <c>{first host}{path}?{extraQuery}&amp;zpw_ver=...&amp;zpw_type=...</c> with a form-urlencoded body
    /// of one (encrypted) <c>params</c> field, no signkey; returns the inner <c>data</c> part of the decrypted response.
    /// Reuses the read path's host validation, cooldown after 429, per-endpoint spacing and MinInterval, but differs in that it makes:
    /// EXACTLY ONE attempt on the first host, NO failover, NO retry. Every network error, timeout and HTTP 5xx
    /// is thrown as is. In that case it is NOT KNOWN whether the request reached the server: the caller must NOT resend by itself.
    /// </summary>
    /// <param name="extraQuery">Query parameters placed before <c>zpw_ver</c>/<c>zpw_type</c> (for example <c>nretry=0</c>); must not contain sensitive data.</param>
    /// <exception cref="ApiException">error_code at the outer or inner layer is not 0.</exception>
    /// <exception cref="RateLimitedException">HTTP 429, in cooldown after a 429, or the endpoint was called too early.</exception>
    /// <exception cref="InvalidDataException">Malformed response, ServiceMap lacks the service, or an invalid host.</exception>
    public Task<JsonElement> PostEncryptedAsync(
        string service,
        string path,
        string payloadJson,
        IEnumerable<KeyValuePair<string, string>>? extraQuery = null,
        CancellationToken cancellationToken = default) =>
        PostCoreAsync(service, path, payloadJson, extraQuery, dataOptional: false, cancellationToken);

    /// <summary>Core of the write path. <paramref name="dataOptional"/>: a missing inner-layer <c>data</c> returns <c>default</c> instead of throwing.</summary>
    private async Task<JsonElement> PostCoreAsync(
        string service,
        string path,
        string payloadJson,
        IEnumerable<KeyValuePair<string, string>>? extraQuery,
        bool dataOptional,
        CancellationToken cancellationToken)
    {
        string[] baseHosts = PrepareCall(service, path, payloadJson, cancellationToken);

        var form = new[] { new KeyValuePair<string, string>("params", _cipher.Encrypt(payloadJson)) };
        _options.Trace?.Invoke($"api {service} {path} host#0 attempt 1/1");

        // No catch/continue: network errors, timeouts and 5xx propagate immediately; no other host is tried.
        string body = await SendThrottledAsync(
            baseHosts[0] + path, path, true, extraQuery ?? [], form, cancellationToken).ConfigureAwait(false);

        ResetCooldownCounter();
        return ParseResponse(body, requireData: !dataOptional);
    }

    private const string SendTextPath = "/api/message/sms";
    private const int MaxToIdLength = 64;

    // A client-side choice; the server's ceiling is not verified (not verified on the live server).
    private const int MaxTextLength = 2000;

    /// <summary>
    /// Sends ONE private text message to <paramref name="toId"/>: <c>chat</c> + <c>/api/message/sms</c>, <c>nretry=0</c>.
    /// [Source] zca-js and ctdozlite; NOT run on the live server.
    /// This is a write operation: no failover, no retry, no loop (see <see cref="PostEncryptedAsync"/>).
    /// A network error, timeout or 5xx means it is NOT KNOWN whether the message arrived: the caller must NOT resend by itself.
    /// </summary>
    /// <param name="toId">1-64 characters: ASCII letters, digits, underscore, hyphen.</param>
    /// <param name="text">1-2000 UTF-16 code units, no lone surrogate, no control characters except LF, CR and tab.</param>
    /// <exception cref="ArgumentException">Invalid input; thrown before any network call, nothing is sent.</exception>
    public async Task<SentTextResult> SendTextAsync(
        string toId,
        string text,
        CancellationToken cancellationToken = default)
    {
        ValidateToId(toId);
        ValidateText(text);

        long clientId = _clock().ToUnixTimeMilliseconds();
        string payload = BuildSendTextPayload(toId, text, clientId);
        JsonElement data = await PostCoreAsync(
            "chat", SendTextPath, payload, [new("nretry", "0")], dataOptional: true, cancellationToken)
            .ConfigureAwait(false);

        // msgId is a 19-digit number or a string: read verbatim. A missing data or msgId is still success (error_code 0).
        string? msgId = null;
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("msgId", out JsonElement element)
            && element.ValueKind is JsonValueKind.String or JsonValueKind.Number)
        {
            try
            {
                msgId = JsonFields.ReadScalar(element);
            }
            catch (InvalidOperationException)
            {
                msgId = null; // Corrupt string (lone surrogate): degrade to the field level.
            }
        }

        return new SentTextResult(msgId, clientId);
    }

    /// <summary>Fixed key order, matching zca-js: message, clientId (number), imei, ttl, toid. No hand-built string concatenation.</summary>
    private string BuildSendTextPayload(string toId, string text, long clientId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("message", text);
            writer.WriteNumber("clientId", clientId);
            writer.WriteString("imei", _context.Imei);
            writer.WriteNumber("ttl", 0);
            writer.WriteString("toid", toId);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // Error messages do not contain input values (toId and the message content are sensitive data).
    private static void ValidateToId(string toId)
    {
        ArgumentException.ThrowIfNullOrEmpty(toId);
        if (toId.Length > MaxToIdLength)
        {
            throw new ArgumentException($"toId is longer than {MaxToIdLength} characters.", nameof(toId));
        }

        foreach (char c in toId)
        {
            bool ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-';
            if (!ok)
            {
                throw new ArgumentException("toId may contain only ASCII letters, digits, underscore and hyphen.", nameof(toId));
            }
        }
    }

    private static void ValidateText(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        if (text.Length > MaxTextLength)
        {
            throw new ArgumentException($"The text is longer than {MaxTextLength} UTF-16 code units.", nameof(text));
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                continue;
            }

            if (char.IsSurrogate(c))
            {
                throw new ArgumentException("The text contains a lone surrogate.", nameof(text));
            }

            if (char.IsControl(c) && c is not ('\n' or '\r' or '\t'))
            {
                throw new ArgumentException("The text contains a control character (only line breaks and tab are allowed).", nameof(text));
            }
        }
    }

    /// <summary>
    /// Friend list (read-only): <c>profile</c> + <c>/api/social/friend/getfriends</c>.
    /// Returns the raw <c>data</c>; use <see cref="FriendParser"/> or <see cref="GetAllFriendsAsync"/> to get the model.
    /// </summary>
    public Task<JsonElement> GetFriendsAsync(
        int count = 50,
        int page = 1,
        int avatarSize = 120,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(avatarSize, 1);

        // Fixed key order, matching zca-js.
        string payload = string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"incInvalid\":1,\"page\":{page},\"count\":{count},\"avatar_size\":{avatarSize},\"actiontime\":0,\"imei\":{JsonSerializer.Serialize(_context.Imei)}}}");
        return GetEncryptedAsync("profile", "/api/social/friend/getfriends", payload, cancellationToken);
    }

    /// <summary>
    /// Friend list with EXACTLY ONE <c>getfriends</c> request (page 1, large count), no paging.
    /// Call at initialization or when the user presses refresh, not in a loop.
    /// Every error (including code 112) is thrown. <see cref="FriendListResult.Pages"/> is always 1.
    /// </summary>
    /// <param name="onElement">Called for each raw element (including corrupt ones) of the array; not stored. Not called if data is not an array.</param>
    /// <exception cref="InvalidDataException">data is not an array.</exception>
    public async Task<FriendListResult> GetFriendListAsync(
        Action<JsonElement>? onElement = null,
        CancellationToken cancellationToken = default)
    {
        JsonElement data = await GetFriendsAsync(FriendListCount, 1, 120, cancellationToken).ConfigureAwait(false);
        if (onElement is not null && data.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement element in data.EnumerateArray())
            {
                onElement(element);
            }
        }

        FriendListResult parsed = FriendParser.Parse(data);
        return parsed with { Pages = 1 };
    }

    /// <summary>
    /// For diagnostics only, NOT for use in the app: getfriends paging is not fully verified and several consecutive requests
    /// got 429 on the live server; use <see cref="GetFriendListAsync"/>.
    /// Fetches the whole friend list by calling <see cref="GetFriendsAsync"/> sequentially page by page (page from 1).
    /// Stops when: the page is empty; the page has no new userId after deduplication (if the page repeats the previous page entirely,
    /// sets <see cref="FriendListResult.PageParameterIgnored"/>); or <paramref name="maxPages"/> is reached
    /// (sets <see cref="FriendListResult.Truncated"/>: friends may remain unfetched, regardless of the last page length). Deliberately does NOT stop when a page is shorter than <paramref name="pageSize"/>,
    /// because it is not known whether the server has a lower count ceiling (not verified on the live server).
    /// The only exception: a <see cref="ApiException"/> with code 112 on page 2 or later is treated as the end of the list
    /// (stop, keep the earlier pages, set <see cref="FriendListResult.EndedByNoData"/>). This is an inference from code 112
    /// ("The server returns 112 on a later page"), not proof that the list is complete: treat it as complete only when the real friend count is known too.
    /// Every other error on any page, and 112 on the first page, is thrown; no partial result is returned.
    /// </summary>
    /// <param name="onElement">Called for each raw element (including duplicate or corrupt ones); not stored.</param>
    /// <exception cref="InvalidDataException">data is not an array.</exception>
    public async Task<FriendListResult> GetAllFriendsAsync(
        int pageSize = 50,
        int maxPages = 20,
        Action<JsonElement>? onElement = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 500);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPages, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPages, 100);

        var friends = new List<Friend>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string>? previousIds = null;
        int skipped = 0;
        int pages = 0;
        bool truncated = false;
        bool pageIgnored = false;
        bool endedByNoData = false;

        for (int page = 1; page <= maxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonElement data;
            try
            {
                data = await GetFriendsAsync(pageSize, page, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ApiException ex) when (page > 1 && ex.ErrorCode == NoMoreDataErrorCode)
            {
                pages++; // A request that returned 112 still counts as a request made.
                endedByNoData = true;
                break;
            }

            pages++;
            if (data.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("The friend list is not a JSON array.");
            }

            if (data.GetArrayLength() == 0)
            {
                break;
            }

            var pageIds = new HashSet<string>(StringComparer.Ordinal);
            int added = 0;
            foreach (JsonElement element in data.EnumerateArray())
            {
                onElement?.Invoke(element);
                if (!FriendParser.TryParseFriend(element, out Friend? friend))
                {
                    skipped++;
                    continue;
                }

                pageIds.Add(friend.UserId);
                if (seen.Add(friend.UserId))
                {
                    friends.Add(friend);
                    added++;
                }
            }

            if (added == 0)
            {
                pageIgnored = previousIds is not null && pageIds.Count > 0 && pageIds.SetEquals(previousIds);
                break;
            }

            previousIds = pageIds;
            // Page length is not an end-of-list signal because the count ceiling is unknown; only an empty page, code 112 or a duplicate page is a signal.
            if (page == maxPages)
            {
                truncated = true;
            }
        }

        return new FriendListResult(friends, skipped, pages, truncated, pageIgnored, endedByNoData);
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }

    /// <summary>Every HTTP request (including attempts on a fallback host) goes through here: sequential, at least MinInterval apart.</summary>
    private async Task<string> SendThrottledAsync(
        string baseUrl,
        string path,
        bool isFirstAttempt,
        IEnumerable<KeyValuePair<string, string>> query,
        IEnumerable<KeyValuePair<string, string>>? form,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check after holding _gate: the preceding request may have just received a 429 or just recorded the endpoint mark.
            ThrowIfCoolingDown();
            if (isFirstAttempt)
            {
                ThrowIfEndpointTooSoon(path);
            }

            if (_lastStart is { } previous)
            {
                // Clamped to [0, MinInterval]: a system clock set backwards must not make the client stand still.
                TimeSpan wait = previous + _options.MinInterval - _clock();
                wait = TimeSpan.FromTicks(Math.Min(wait.Ticks, _options.MinInterval.Ticks));
                if (wait > TimeSpan.Zero)
                {
                    await _delay(wait, cancellationToken).ConfigureAwait(false);
                }
            }

            // Record the mark right before sending (after every point that can cancel): a cancellation while waiting for _gate or MinInterval leaves no mark,
            // while a network error after this point still counts as sent.
            _lastStart = _clock();
            if (isFirstAttempt && _endpointIntervals.ContainsKey(path))
            {
                lock (_endpointLock)
                {
                    _endpointLastStart[path] = _lastStart.Value;
                }
            }

            try
            {
                return form is null
                    ? await _http.GetStringAsync(baseUrl, query, includeApiVersion: true, cancellationToken)
                        .ConfigureAwait(false)
                    : await _http.PostFormAsync(baseUrl, query, form, includeApiVersion: true, cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Record while still holding _gate: a request waiting behind must see the cooldown as soon as it enters.
                throw new RateLimitedException(StartCooldown());
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Common beginning of every call, before the network call: parameter check, cooldown after 429, endpoint spacing,
    /// then validation of EVERY host of the service (cookies and encrypted parameters may only be sent to *.zalo.me / *.zaloapp.com).
    /// </summary>
    private string[] PrepareCall(string service, string path, string payloadJson, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(payloadJson);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfCoolingDown();
        ThrowIfEndpointTooSoon(path);

        if (!_loginInfo.ServiceMap.TryGetValue(service, out IReadOnlyList<string>? hosts) || hosts.Count == 0)
        {
            throw new InvalidDataException($"The ServiceMap has no host for service '{service}'.");
        }

        string[] baseHosts = new string[hosts.Count];
        for (int i = 0; i < hosts.Count; i++)
        {
            baseHosts[i] = EnsureTrustedHost(hosts[i]);
        }

        return baseHosts;
    }

    /// <summary>Throws immediately if in cooldown after a 429; sends nothing.</summary>
    private void ThrowIfCoolingDown()
    {
        TimeSpan retryAfter;
        lock (_cooldownLock)
        {
            if (_blockedUntil is not { } until)
            {
                return;
            }

            DateTimeOffset now = _clock();
            TimeSpan remaining = until - now;
            if (remaining > _currentCooldown)
            {
                // Clock set backwards: clamp and re-anchor so the cooldown is never longer than the set amount.
                remaining = _currentCooldown;
                _blockedUntil = now + remaining;
            }

            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            retryAfter = remaining;
        }

        throw new RateLimitedException(retryAfter);
    }

    /// <summary>
    /// Throws immediately if the endpoint has a minimum interval and the most recent send was not long enough ago; sends nothing.
    /// Not a 429, so it does not touch the 429 counter.
    /// </summary>
    private void ThrowIfEndpointTooSoon(string path)
    {
        if (!_endpointIntervals.TryGetValue(path, out TimeSpan interval) || interval <= TimeSpan.Zero)
        {
            return;
        }

        TimeSpan retryAfter;
        lock (_endpointLock)
        {
            if (!_endpointLastStart.TryGetValue(path, out DateTimeOffset last))
            {
                return;
            }

            DateTimeOffset now = _clock();
            TimeSpan remaining = last + interval - now;
            if (remaining > interval)
            {
                // Clock set backwards: clamp and re-anchor so the wait is never longer than the set interval.
                remaining = interval;
                _endpointLastStart[path] = now;
            }

            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            retryAfter = remaining;
        }

        throw new RateLimitedException(retryAfter);
    }

    /// <summary>Starts a new cooldown after a 429 and returns its length.</summary>
    private TimeSpan StartCooldown()
    {
        lock (_cooldownLock)
        {
            _consecutive429 = Math.Min(_consecutive429 + 1, 16); // 16 prevents overflow when doubling; the 15-minute ceiling was reached long before.
            long ticks = BaseCooldown.Ticks << (_consecutive429 - 1);
            _currentCooldown = TimeSpan.FromTicks(Math.Min(ticks, MaxCooldown.Ticks));
            _blockedUntil = _clock() + _currentCooldown;
            return _currentCooldown;
        }
    }

    private void ResetCooldownCounter()
    {
        lock (_cooldownLock)
        {
            _consecutive429 = 0;
        }
    }

    /// <summary>Two-layer response: outer {error_code, error_message, data (encrypted string)}, inner {error_code, data}.</summary>
    private JsonElement ParseResponse(string body, bool requireData = true)
    {
        try
        {
            using JsonDocument outer = JsonDocument.Parse(body);
            JsonElement outerRoot = outer.RootElement;
            if (outerRoot.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The response is not a JSON object.");
            }

            int outerCode = ReadErrorCode(outerRoot)
                ?? throw new InvalidDataException("The response is missing error_code.");
            if (outerCode != 0)
            {
                throw new ApiException(outerCode, ReadMessage(outerRoot));
            }

            if (!outerRoot.TryGetProperty("data", out JsonElement dataElement)
                || dataElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("The response is missing data as an encrypted string.");
            }

            string decrypted = _cipher.Decrypt(dataElement.GetString() ?? "");
            using JsonDocument inner = JsonDocument.Parse(decrypted);
            JsonElement innerRoot = inner.RootElement;
            if (innerRoot.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The decrypted data is not a JSON object.");
            }

            int innerCode = ReadErrorCode(innerRoot)
                ?? throw new InvalidDataException("The decrypted data is missing error_code.");
            if (innerCode != 0)
            {
                throw new ApiException(innerCode, ReadMessage(innerRoot));
            }

            if (!innerRoot.TryGetProperty("data", out JsonElement result))
            {
                if (!requireData)
                {
                    return default;
                }

                throw new InvalidDataException("The decrypted data is missing the data field.");
            }

            return result.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The response is not valid JSON.", ex);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("The response data is not valid Base64.", ex);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("The response data could not be decrypted.", ex);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("The response data has an invalid length or content.", ex);
        }
        catch (InvalidOperationException ex)
        {
            // JsonElement.GetString() throws when the string has a lone surrogate.
            throw new InvalidDataException("The response contains an invalid Unicode string.", ex);
        }
    }

    /// <summary>
    /// Validates a host in the ServiceMap and returns the normalized form <c>https://{lowercase host}</c>.
    /// Accepts only DNS names under zalo.me/zaloapp.com, default port, no userinfo, path, query or fragment.
    /// </summary>
    private static string EnsureTrustedHost(string host)
    {
        if (!Uri.TryCreate(host, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length != 0
            || uri.HostNameType != UriHostNameType.Dns
            || !uri.IsDefaultPort
            || uri.AbsolutePath != "/"
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0
            || !IsServiceDomain(uri.Host))
        {
            throw new InvalidDataException("The ServiceMap contains an invalid host (needs https, a zalo.me or zaloapp.com domain, no port, path, query or fragment).");
        }

        return Uri.UriSchemeHttps + "://" + uri.Host.ToLowerInvariant();
    }

    private static bool IsServiceDomain(string host) =>
        host.Equals("zalo.me", StringComparison.OrdinalIgnoreCase)
        || host.Equals("zaloapp.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".zalo.me", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".zaloapp.com", StringComparison.OrdinalIgnoreCase);

    private static int? ReadErrorCode(JsonElement obj)
    {
        if (!obj.TryGetProperty("error_code", out JsonElement code))
        {
            return null;
        }

        switch (code.ValueKind)
        {
            case JsonValueKind.Number when code.TryGetInt32(out int number):
                return number;
            case JsonValueKind.String
                when int.TryParse(code.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed):
                return parsed;
            default:
                throw new InvalidDataException("error_code is not an integer.");
        }
    }

    /// <summary>Error message for reference only: a corrupt string (lone surrogate) returns null so the server's error code is kept.</summary>
    private static string? ReadMessage(JsonElement obj)
    {
        if (!obj.TryGetProperty("error_message", out JsonElement message) || message.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            return message.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
