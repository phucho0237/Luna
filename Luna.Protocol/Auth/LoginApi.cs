using System.Globalization;
using System.Text;
using System.Text.Json;
using Luna.Protocol.Crypto;
using Luna.Protocol.Http;

namespace Luna.Protocol.Auth;

/// <summary>getServerInfo and getLoginInfo on wpa.chat.zalo.me.</summary>
public sealed class LoginApi
{
    private const string BaseUrl = "https://wpa.chat.zalo.me/api/login";
    private const string ComputerName = "Web";

    private readonly ClientContext _context;
    private readonly ProtocolHttpClient _http;
    private readonly Func<long> _unixMillis;

    /// <param name="unixMillis">Only pass in tests to fix the clock.</param>
    public LoginApi(ClientContext context, ProtocolHttpClient http, Func<long>? unixMillis = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(http);
        _context = context;
        _http = http;
        _unixMillis = unixMillis ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>Calls getServerInfo. Parameters are not encrypted, only signed, and there is no zpw_ver/zpw_type.</summary>
    public async Task<JsonElement> GetServerInfoAsync(CancellationToken cancellationToken = default)
    {
        var signed = new Dictionary<string, string>
        {
            ["imei"] = _context.Imei,
            ["type"] = _context.ApiType,
            ["client_version"] = _context.ApiVersion,
            ["computer_name"] = ComputerName,
        };
        string signkey = SignKey.Compute(SignKey.NormalizeTag("getServerInfo"), signed);

        var query = new List<KeyValuePair<string, string>>(signed) { new("signkey", signkey) };
        string body = await _http
            .GetStringAsync($"{BaseUrl}/getServerInfo", query, includeApiVersion: false, cancellationToken)
            .ConfigureAwait(false);

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind == JsonValueKind.Null)
        {
            throw new ApiException(ReadErrorCode(root), ReadErrorMessage(root) ?? "The response is missing data.");
        }

        return data.Clone();
    }

    /// <summary>Calls getLoginInfo with params encrypted by ParamCipher v2. Needs the session cookies loaded in the context.</summary>
    public async Task<LoginInfo> LoginAsync(CancellationToken cancellationToken = default)
    {
        long now = _unixMillis();
        var encryptor = new ParamsEncryptor(_context.ApiType, _context.Imei, now);

        IReadOnlyDictionary<string, string> fields = encryptor.BuildRequest(
            _context.ApiType,
            _context.ApiVersion,
            BuildPayload(now),
            SignKey.NormalizeTag("getLoginInfo"));

        // nretry is appended after signing and is not part of the signature.
        var query = fields.ToList();
        query.Add(new KeyValuePair<string, string>("nretry", "0"));

        string body = await _http
            .GetStringAsync($"{BaseUrl}/getLoginInfo", query, includeApiVersion: true, cancellationToken)
            .ConfigureAwait(false);

        using JsonDocument outer = JsonDocument.Parse(body);
        JsonElement root = outer.RootElement;
        ThrowIfError(root);

        if (!root.TryGetProperty("data", out JsonElement dataElement) || dataElement.ValueKind != JsonValueKind.String)
        {
            throw new ApiException(ReadErrorCode(root), ReadErrorMessage(root) ?? "The response is missing data.");
        }

        string decrypted = encryptor.DecryptResponse(dataElement.GetString() ?? "");
        using JsonDocument inner = JsonDocument.Parse(decrypted);
        ThrowIfError(inner.RootElement);

        if (!inner.RootElement.TryGetProperty("data", out JsonElement loginData))
        {
            throw new InvalidDataException("The decrypted login response is missing data.");
        }

        return LoginInfo.Parse(loginData);
    }

    private string BuildPayload(long now)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("computer_name", ComputerName);
            writer.WriteString("imei", _context.Imei);
            writer.WriteString("language", _context.Language);
            writer.WriteNumber("ts", now);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void ThrowIfError(JsonElement envelope)
    {
        int code = ReadErrorCode(envelope);
        if (code != 0)
        {
            throw new ApiException(code, ReadErrorMessage(envelope));
        }
    }

    private static int ReadErrorCode(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("error_code", out JsonElement element))
        {
            return 0;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out int number) => number,
            JsonValueKind.String when int.TryParse(
                element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
            _ => 0,
        };
    }

    private static string? ReadErrorMessage(JsonElement envelope) =>
        envelope.TryGetProperty("error_message", out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}