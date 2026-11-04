using System.Net;
using System.Text;

namespace Luna.Protocol.Http;

/// <summary>HttpClient with the headers, cookies and URL construction in the service style built in.</summary>
public sealed class ProtocolHttpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ClientContext _context;

    /// <param name="handler">Only pass in tests. Leave empty to use the real handler with a CookieContainer.</param>
    public ProtocolHttpClient(ClientContext context, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _http = handler is null
            ? new HttpClient(new HttpClientHandler
            {
                CookieContainer = context.Cookies,
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.All,
            })
            : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Builds a URL with an encoded query. When <paramref name="includeApiVersion"/> is true,
    /// <c>zpw_ver</c> and <c>zpw_type</c> are appended at the end.
    /// </summary>
    public string BuildUrl(string baseUrl, IEnumerable<KeyValuePair<string, string>> query, bool includeApiVersion)
    {
        var sb = new StringBuilder(baseUrl).Append('?');
        bool first = true;

        void Add(string key, string value)
        {
            if (!first)
            {
                sb.Append('&');
            }

            first = false;
            sb.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }

        foreach (KeyValuePair<string, string> pair in query)
        {
            Add(pair.Key, pair.Value);
        }

        if (includeApiVersion)
        {
            Add("zpw_ver", _context.ApiVersion);
            Add("zpw_type", _context.ApiType);
        }

        return sb.ToString();
    }

    public async Task<string> GetStringAsync(
        string baseUrl,
        IEnumerable<KeyValuePair<string, string>> query,
        bool includeApiVersion,
        CancellationToken cancellationToken = default)
    {
        string url = BuildUrl(baseUrl, query, includeApiVersion);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// POST <c>application/x-www-form-urlencoded</c> to <paramref name="baseUrl"/> (query built like <see cref="GetStringAsync"/>),
    /// with the same headers, cookies and HTTP error reporting. The form body is written nowhere except the outgoing request.
    /// </summary>
    public async Task<string> PostFormAsync(
        string baseUrl,
        IEnumerable<KeyValuePair<string, string>> query,
        IEnumerable<KeyValuePair<string, string>> fields,
        bool includeApiVersion,
        CancellationToken cancellationToken = default)
    {
        string url = BuildUrl(baseUrl, query, includeApiVersion);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields),
        };
        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        request.Headers.TryAddWithoutValidation("Origin", "https://chat.zalo.me");
        request.Headers.TryAddWithoutValidation("Referer", "https://chat.zalo.me/");
        request.Headers.TryAddWithoutValidation("User-Agent", _context.UserAgent);

        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();
}