using System.Globalization;
using System.Text.Json;

namespace Luna.Protocol;

/// <summary>Reads the common fields of a server response (error_code, error_message, scalar, number).</summary>
internal static class JsonFields
{
    public static int ErrorCode(JsonElement envelope)
    {
        if (envelope.ValueKind != JsonValueKind.Object
            || !envelope.TryGetProperty("error_code", out JsonElement element))
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

    public static string? ErrorMessage(JsonElement envelope) => GetString(envelope, "error_message");

    public static string? GetString(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out JsonElement element)
        && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    /// <summary>
    /// Reads a scalar value (string, number, boolean) as a string.
    /// For numbers uses <see cref="JsonElement.GetRawText"/> so no precision is lost with 64-bit or larger numbers.
    /// </summary>
    public static string? ReadScalar(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(name, out JsonElement element))
        {
            return null;
        }

        return ReadScalar(element);
    }

    /// <summary>
    /// Reads a scalar <see cref="JsonElement"/> directly as a string.
    /// </summary>
    public static string? ReadScalar(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };

    /// <summary>
    /// Reads a string, a number, or the raw JSON if it is an object/array.
    /// </summary>
    public static string? ReadScalarOrJson(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(name, out JsonElement element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText(),
        };
    }

    /// <summary>
    /// Reads a 32-bit integer from a JSON number or string.
    /// </summary>
    public static int ReadInt32(JsonElement parent, string name, int defaultValue = 0)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(name, out JsonElement element))
        {
            return defaultValue;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out int value) => value,
            JsonValueKind.String when int.TryParse(
                element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
            _ => defaultValue,
        };
    }
}
