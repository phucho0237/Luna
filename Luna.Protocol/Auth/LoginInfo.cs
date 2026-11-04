using System.Text.Json;

namespace Luna.Protocol.Auth;

/// <summary>Result of getLoginInfo (the <c>data</c> part after decryption).</summary>
public sealed record LoginInfo(
    string Uid,
    string ZpwEnk,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ServiceMap,
    IReadOnlyList<string> WebSocketUrls,
    string? Send2MeId = null)
{
    public static LoginInfo Parse(JsonElement data)
    {
        string uid = ReadScalar(data, "uid")
            ?? throw new InvalidDataException("The login response is missing uid.");
        string enk = ReadScalar(data, "zpw_enk")
            ?? throw new InvalidDataException("The login response is missing zpw_enk.");

        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (data.TryGetProperty("zpw_service_map_v3", out JsonElement serviceMap)
            && serviceMap.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty service in serviceMap.EnumerateObject())
            {
                map[service.Name] = ReadStrings(service.Value);
            }
        }

        IReadOnlyList<string> ws = data.TryGetProperty("zpw_ws", out JsonElement wsElement)
            ? ReadStrings(wsElement)
            : [];

        return new LoginInfo(uid, enk, map, ws, ReadSend2MeId(data));
    }

    /// <summary>
    /// [Source] <c>send2me_id</c> is the "My Documents" id (zca-js, ctdozlite); not verified on the live server that the real login response
    /// has this field. Accepts only a non-empty string; missing, wrong type or a corrupt string (lone surrogate) returns null, no exception thrown.
    /// </summary>
    private static string? ReadSend2MeId(JsonElement data)
    {
        if (!data.TryGetProperty("send2me_id", out JsonElement element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            string? value = element.GetString();
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Never prints <c>Uid</c>, <c>ZpwEnk</c> or <c>Send2MeId</c> to logs.</summary>
    public override string ToString() => "LoginInfo";

    private static string? ReadScalar(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null,
        };
    }

    private static List<string> ReadStrings(JsonElement element)
    {
        var result = new List<string>();
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                result.Add(element.GetString() ?? "");
                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        result.Add(item.GetString() ?? "");
                    }
                }

                break;
        }

        return result;
    }
}