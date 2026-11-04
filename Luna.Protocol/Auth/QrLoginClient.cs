using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Luna.Protocol.Auth;

/// <summary>
/// QR code login through id.zalo.me. On success the session cookies (zpw_sek...) are in
/// <see cref="ClientContext.Cookies"/> and can be reused for <see cref="LoginApi"/>.
/// Cookies and redirects are handled manually (like zca-js) because the server sets cookies for other domains
/// along the checksession redirect chain.
/// </summary>
public sealed class QrLoginClient : IDisposable
{
    private const string AccountBase = "https://id.zalo.me/account";
    private const string LoginPageUrl = "https://id.zalo.me/account?continue=https%3A%2F%2Fchat.zalo.me%2F";
    private const string CheckSessionUrl =
        "https://id.zalo.me/account/checksession?continue=https%3A%2F%2Fchat.zalo.me%2Findex.html";
    private const string UserInfoUrl = "https://jr.chat.zalo.me/jr/userinfo";
    private const string ChatSite = "https://chat.zalo.me/";
    private const string PcContinue = "https://zalo.me/pc";
    private const string PcReferer = "https://id.zalo.me/account?continue=https%3A%2F%2Fzalo.me%2Fpc";
    private const string ChatReferer = LoginPageUrl;
    private const string PngDataUriPrefix = "data:image/png;base64,";
    private const int WaitingErrorCode = 8;
    private const int DeclinedErrorCode = -13;
    private const int MaxRedirects = 10;

    private static readonly TimeSpan DefaultQrLifetime = TimeSpan.FromSeconds(100);
    private static readonly TimeSpan PollGap = TimeSpan.FromMilliseconds(250);

    private static readonly Regex DomainAttribute = new(
        @"(?:^|;)\s*domain\s*=\s*\.?([^;\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly ClientContext _context;
    private readonly HttpClient _http;
    private readonly TimeSpan _qrLifetime;
    private readonly Action<string>? _trace;

    /// <param name="handler">Only pass in tests. Leave empty to use the real handler.</param>
    /// <param name="qrLifetime">Lifetime of one QR code. Default 100 seconds, as in zca-js.</param>
    /// <param name="trace">Receives diagnostic lines (cookie names and JSON shapes only, no sensitive values).</param>
    public QrLoginClient(
        ClientContext context,
        HttpMessageHandler? handler = null,
        TimeSpan? qrLifetime = null,
        Action<string>? trace = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _qrLifetime = qrLifetime ?? DefaultQrLifetime;
        _trace = trace;

        // This code handles cookies and redirects itself, so both are turned off on the handler.
        _http = handler is null
            ? new HttpClient(new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
            })
            : new HttpClient(handler, disposeHandler: false);

        // The wait-for-scan/confirm steps are long-polls; the time limit is decided by the QR code.
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>
    /// Runs the whole QR flow. If the code expires, a new one is generated, at most <paramref name="maxAttempts"/> times.
    /// </summary>
    /// <exception cref="ApiException">The user declined on the phone (code -13) or the server reported an error.</exception>
    /// <exception cref="TimeoutException">The QR code expired more times in a row than allowed.</exception>
    public async Task<QrLoginResult> LoginAsync(
        Action<QrLoginEvent> onEvent,
        int maxAttempts = 5,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            QrLoginResult? result = await TryOnceAsync(onEvent, cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }
        }

        throw new TimeoutException($"The QR code expired {maxAttempts} times in a row without being scanned.");
    }

    public void Dispose() => _http.Dispose();

    /// <returns>The result when login finishes, or null when the QR code expired (call again with a new code).</returns>
    private async Task<QrLoginResult?> TryOnceAsync(Action<QrLoginEvent> onEvent, CancellationToken ct)
    {
        string version = await LoadLoginVersionAsync(ct).ConfigureAwait(false);

        await PostFormAsync("logininfo", PcReferer, [("continue", PcContinue), ("v", version)], ct)
            .ConfigureAwait(false);
        await PostFormAsync("verify-client", PcReferer, [("type", "device"), ("continue", PcContinue), ("v", version)], ct)
            .ConfigureAwait(false);

        JsonElement generated = await PostFormAsync(
            "authen/qr/generate", PcReferer, [("continue", PcContinue), ("v", version)], ct).ConfigureAwait(false);
        if (!generated.TryGetProperty("data", out JsonElement qrData) || qrData.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException(
                JsonFields.ErrorCode(generated), JsonFields.ErrorMessage(generated) ?? "Could not generate the QR code.");
        }

        string code = JsonFields.GetString(qrData, "code")
            ?? throw new InvalidDataException("The QR generate response is missing code.");
        string image = JsonFields.GetString(qrData, "image")
            ?? throw new InvalidDataException("The QR generate response is missing image.");
        string base64 = image.StartsWith(PngDataUriPrefix, StringComparison.Ordinal)
            ? image[PngDataUriPrefix.Length..]
            : image;
        onEvent(new QrGenerated(code, Convert.FromBase64String(base64)));

        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(ct);
        expiry.CancelAfter(_qrLifetime);

        string displayName;
        try
        {
            JsonElement scan = await PollAsync(
                "authen/qr/waiting-scan",
                ChatReferer,
                [("code", code), ("continue", ChatSite), ("v", version)],
                expiry.Token).ConfigureAwait(false);
            if (!scan.TryGetProperty("data", out JsonElement scanData))
            {
                throw new ApiException(
                    JsonFields.ErrorCode(scan), JsonFields.ErrorMessage(scan) ?? "Could not read the scan result.");
            }

            displayName = JsonFields.GetString(scanData, "display_name") ?? "";
            onEvent(new QrScanned(displayName, JsonFields.GetString(scanData, "avatar")));

            JsonElement confirm = await PollAsync(
                "authen/qr/waiting-confirm",
                ChatReferer,
                [("code", code), ("gToken", ""), ("gAction", "CONFIRM_QR"), ("continue", ChatSite), ("v", version)],
                expiry.Token).ConfigureAwait(false);
            _trace?.Invoke($"waiting-confirm: {Shape(confirm)}");

            int confirmCode = JsonFields.ErrorCode(confirm);
            if (confirmCode == DeclinedErrorCode)
            {
                onEvent(new QrDeclined());
                throw new ApiException(DeclinedErrorCode, "The user declined the login on the phone.");
            }

            if (confirmCode != 0)
            {
                throw new ApiException(confirmCode, JsonFields.ErrorMessage(confirm));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The code's lifetime ran out, not a cancellation by the user.
            onEvent(new QrExpired());
            return null;
        }

        await CheckSessionAsync(ct).ConfigureAwait(false);

        JsonElement userInfo = await GetJsonAsync(UserInfoUrl, ChatSite, ct).ConfigureAwait(false);
        if (!userInfo.TryGetProperty("data", out JsonElement userData)
            || !userData.TryGetProperty("logged", out JsonElement logged)
            || logged.ValueKind != JsonValueKind.True)
        {
            _trace?.Invoke($"userinfo: {Shape(userInfo)}");
            throw new ApiException(
                -1, "Login is not complete: the account is not in the logged-in state after confirmation.");
        }

        JsonElement info = userData.TryGetProperty("info", out JsonElement infoElement)
            ? infoElement.Clone()
            : default;
        return new QrLoginResult(displayName, info);
    }

    private async Task<string> LoadLoginVersionAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LoginPageUrl);
        ApplyHeaders(request, ChatSite, isDocument: true, sameSite: true);

        using HttpResponseMessage response = await SendFollowingAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        Match match = Regex.Match(html, @"https://stc-zlogin\.zdn\.vn/main-([\d.]+)\.js", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new InvalidDataException("Could not find the login page version (main-*.js of stc-zlogin).");
        }

        return match.Groups[1].Value;
    }

    private async Task<JsonElement> PostFormAsync(
        string endpoint, string referer, (string Key, string Value)[] form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{AccountBase}/{endpoint}")
        {
            Content = new FormUrlEncodedContent(form.Select(p => new KeyValuePair<string, string>(p.Key, p.Value))),
        };
        ApplyHeaders(request, referer, isDocument: false, sameSite: true);

        using HttpResponseMessage response = await SendFollowingAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    /// <summary>Calls repeatedly until the server stops returning the "waiting" code (8).</summary>
    private async Task<JsonElement> PollAsync(
        string endpoint, string referer, (string Key, string Value)[] form, CancellationToken ct)
    {
        while (true)
        {
            JsonElement response = await PostFormAsync(endpoint, referer, form, ct).ConfigureAwait(false);
            if (JsonFields.ErrorCode(response) != WaitingErrorCode)
            {
                return response;
            }

            await Task.Delay(PollGap, ct).ConfigureAwait(false);
        }
    }

    /// <summary>checksession redirects through several steps and sets the session cookies along the way; all of them must be followed.</summary>
    private async Task CheckSessionAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CheckSessionUrl);
        ApplyHeaders(request, ChatReferer, isDocument: true, sameSite: true);

        using HttpResponseMessage response = await SendFollowingAsync(request, ct).ConfigureAwait(false);

        Uri? finalUri = response.RequestMessage?.RequestUri;
        _trace?.Invoke(
            $"checksession: HTTP {(int)response.StatusCode}, final destination {finalUri?.GetLeftPart(UriPartial.Path)}");
        _trace?.Invoke("Current cookies (names only): "
            + string.Join(", ", _context.Cookies.GetAllCookies().Select(c => $"{c.Name}@{c.Domain}")));
    }

    private async Task<JsonElement> GetJsonAsync(string url, string referer, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(request, referer, isDocument: false, sameSite: false);

        using HttpResponseMessage response = await SendFollowingAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Sends the request, attaches cookies from the shared store, saves cookies from the response and follows redirects itself (switching to GET).
    /// The caller is responsible for disposing the returned response.
    /// </summary>
    private async Task<HttpResponseMessage> SendFollowingAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpRequestMessage current = request;
        for (int hop = 0; ; hop++)
        {
            AttachCookies(current);
            HttpResponseMessage response = await _http.SendAsync(current, ct).ConfigureAwait(false);
            StoreCookies(response, current.RequestUri!);

            Uri? location = response.Headers.Location;
            bool isRedirect = (int)response.StatusCode is >= 300 and < 400 && location is not null;
            if (!isRedirect || hop >= MaxRedirects)
            {
                return response;
            }

            Uri next = location!.IsAbsoluteUri ? location : new Uri(current.RequestUri!, location);
            response.Dispose();

            current = new HttpRequestMessage(HttpMethod.Get, next);
            ApplyHeaders(current, "https://id.zalo.me/", isDocument: true, sameSite: false);
        }
    }

    private void AttachCookies(HttpRequestMessage request)
    {
        string header = _context.Cookies.GetCookieHeader(request.RequestUri!);
        if (header.Length > 0)
        {
            request.Headers.Remove("Cookie");
            request.Headers.TryAddWithoutValidation("Cookie", header);
        }
    }

    /// <summary>
    /// Saves each Set-Cookie. If a cookie declares a Domain different from the response host (but still a service domain), it is attached
    /// to that domain, like zca-js. Domains outside the service are ignored.
    /// </summary>
    private void StoreCookies(HttpResponseMessage response, Uri responseUri)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values))
        {
            return;
        }

        foreach (string setCookie in values)
        {
            Match match = DomainAttribute.Match(setCookie);
            string? domain = match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;

            try
            {
                Uri target = domain is not null && domain != "zalo.me" && IsServiceDomain(domain)
                    ? new Uri($"https://{domain}")
                    : responseUri;
                _context.Cookies.SetCookies(target, setCookie);
            }
            catch (Exception ex) when (ex is CookieException or UriFormatException)
            {
                _trace?.Invoke($"Skipped an invalid cookie: {ex.Message}");
            }
        }
    }

    private static bool IsServiceDomain(string domain) =>
        domain is "zalo.me" or "zaloapp.com"
        || domain.EndsWith(".zalo.me", StringComparison.Ordinal)
        || domain.EndsWith(".zaloapp.com", StringComparison.Ordinal);

    private void ApplyHeaders(HttpRequestMessage request, string referer, bool isDocument, bool sameSite)
    {
        var headers = request.Headers;
        headers.TryAddWithoutValidation(
            "Accept", isDocument ? "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8" : "*/*");
        headers.TryAddWithoutValidation("Accept-Language", "vi-VN,vi;q=0.9,fr-FR;q=0.8,fr;q=0.7,en-US;q=0.6,en;q=0.5");
        headers.TryAddWithoutValidation("Origin", "https://chat.zalo.me");
        headers.TryAddWithoutValidation("Referer", referer);
        headers.TryAddWithoutValidation("User-Agent", _context.UserAgent);
        headers.TryAddWithoutValidation("sec-ch-ua", "\"Chromium\";v=\"130\", \"Google Chrome\";v=\"130\", \"Not?A_Brand\";v=\"99\"");
        headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        headers.TryAddWithoutValidation("sec-fetch-dest", isDocument ? "document" : "empty");
        headers.TryAddWithoutValidation("sec-fetch-mode", isDocument ? "navigate" : "cors");
        headers.TryAddWithoutValidation("sec-fetch-site", sameSite ? "same-origin" : "same-site");
    }

    /// <summary>Describes the JSON shape without exposing values (keeps only error_code and logged).</summary>
    private static string Shape(JsonElement element, int depth = 0)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (depth >= 2)
                {
                    return "{...}";
                }

                IEnumerable<string> members = element.EnumerateObject().Select(p =>
                    p.Name is "error_code" or "logged"
                        ? $"{p.Name}: {p.Value.GetRawText()}"
                        : $"{p.Name}: {Shape(p.Value, depth + 1)}");
                return "{" + string.Join(", ", members) + "}";
            case JsonValueKind.Array:
                return $"[{element.GetArrayLength()} elements]";
            case JsonValueKind.String:
                return "<string>";
            case JsonValueKind.Number:
                return "<number>";
            default:
                return element.ValueKind.ToString().ToLowerInvariant();
        }
    }
}