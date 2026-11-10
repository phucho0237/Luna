using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Luna.Protocol.Diagnostics;

/// <summary>
/// Describes the JSON shape (field names and data types) for diagnostics. Never prints values.
/// Field names use an allow list (<see cref="SafeName"/>): names that do not match are replaced by
/// <c>&lt;key&gt;</c> because they may be a uid, msgId, phone number or a real person's name used as a key.
/// </summary>
public static class JsonShape
{
    private const string KeyPlaceholder = "<key>";

    private const string ValuePlaceholder = "<v>";

    private const int MaxStrictKeys = 30;

    private static readonly Regex StrictName = new(
        @"^[A-Za-z][A-Za-z0-9_]{0,23}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AllowedToken = new(
        @"^[a-z][a-z0-9_.-]{0,39}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AllowedName = new(
        @"^[A-Za-z][A-Za-z0-9_]{0,31}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FiveDigits = new(@"[0-9]{5}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HexKey = new(@"^[0-9a-fA-F]{16,}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Describes the shape of <paramref name="element"/>. For arrays only the first element is taken. Beyond
    /// <paramref name="maxDepth"/> depth it returns only the type name (object/array) and goes no further.
    /// </summary>
    public static string Describe(JsonElement element, int maxDepth = 6)
    {
        var sb = new StringBuilder();
        Write(sb, element, 0, maxDepth, strict: false);
        return sb.ToString();
    }

    /// <summary>
    /// Like <see cref="Describe(JsonElement, int)"/>. With <paramref name="strict"/> set, field names go through
    /// <see cref="SafeNameStrict"/> and an object with more than 30 keys prints only <c>object(N keys)</c>, because such
    /// an object is most likely a map keyed by data. Used for output built from live history data.
    /// </summary>
    public static string Describe(JsonElement element, int maxDepth, bool strict)
    {
        var sb = new StringBuilder();
        Write(sb, element, 0, maxDepth, strict);
        return sb.ToString();
    }

    /// <summary>
    /// Like <see cref="Describe"/> but for a <c>data</c> field that may be a nested JSON string. The string is tried
    /// verbatim first, then in the form missing the outer braces (for example <c>"uid":"1","ts":2</c>, like zca-js typing)
    /// by wrapping it in <c>{}</c>. If it cannot be parsed it returns <c>string(unparsed)</c>. Only catches
    /// <see cref="JsonException"/>. Never prints values.
    /// </summary>
    public static string DescribeEmbedded(JsonElement data, int maxDepth = 6)
    {
        if (data.ValueKind != JsonValueKind.String)
        {
            return Describe(data, maxDepth);
        }

        string text;
        try
        {
            text = data.GetString() ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            // The raw JSON has a lone surrogate: it cannot be read as a string.
            return "string(unparsed)";
        }
        try
        {
            using JsonDocument raw = JsonDocument.Parse(text);
            if (raw.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                return Describe(raw.RootElement, maxDepth);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            // Fall through to try the form missing the outer braces.
        }

        try
        {
            using JsonDocument wrapped = JsonDocument.Parse("{" + text + "}");
            return Describe(wrapped.RootElement, maxDepth);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return "string(unparsed)";
        }
    }

    /// <summary>
    /// <see cref="DescribeEmbedded"/> compacted onto ONE line: line breaks and consecutive whitespace become a single
    /// space, cut at <paramref name="maxLength"/> characters with <c>...</c> added if longer.
    /// </summary>
    public static string DescribeEmbeddedOneLine(JsonElement data, int maxLength = 300)
    {
        string flat = Whitespace.Replace(DescribeEmbedded(data), " ").Trim();
        return flat.Length > maxLength ? flat[..maxLength] + "..." : flat;
    }

    private static void Write(StringBuilder sb, JsonElement element, int level, int maxDepth, bool strict)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (level >= maxDepth)
                {
                    sb.Append("object");
                    return;
                }

                if (strict)
                {
                    int keys = element.EnumerateObject().Count();
                    if (keys > MaxStrictKeys)
                    {
                        sb.Append("object(").Append(keys.ToString(CultureInfo.InvariantCulture)).Append(" keys)");
                        return;
                    }
                }

                // Sibling keys with the same name (after replacing <id>) and the same shape are merged into one line.
                var lines = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty prop in element.EnumerateObject())
                {
                    var child = new StringBuilder();
                    Write(child, prop.Value, level + 1, maxDepth, strict);
                    string name = strict ? SafeNameStrict(prop.Name) : SafeName(prop.Name);
                    string line = $"{Indent(level + 1)}{name}: {child}";
                    if (seen.Add(line))
                    {
                        lines.Add(line);
                    }
                }

                if (lines.Count == 0)
                {
                    sb.Append("{}");
                    return;
                }

                sb.Append("{\n").Append(string.Join(",\n", lines)).Append('\n').Append(Indent(level)).Append('}');
                return;

            case JsonValueKind.Array:
                if (element.GetArrayLength() == 0)
                {
                    sb.Append("[]");
                    return;
                }

                if (level >= maxDepth)
                {
                    sb.Append("array");
                    return;
                }

                sb.Append('[');
                Write(sb, element.EnumerateArray().First(), level, maxDepth, strict);
                sb.Append(']');
                return;

            case JsonValueKind.String:
                sb.Append("string");
                return;
            case JsonValueKind.Number:
                sb.Append("number");
                return;
            case JsonValueKind.True:
            case JsonValueKind.False:
                sb.Append("boolean");
                return;
            case JsonValueKind.Null:
                sb.Append("null");
                return;
            default:
                sb.Append(element.ValueKind.ToString().ToLowerInvariant());
                return;
        }
    }

    private static string Indent(int level) => new(' ', level * 2);

    /// <summary>
    /// Returns the field name if it is safe to print (starts with a letter, only letters/digits/underscore, at most 32 characters,
    /// no run of 5 consecutive digits, not a hex string of 16 or more characters), otherwise returns
    /// <c>&lt;key&gt;</c>.
    /// </summary>
    public static string SafeName(string name) =>
        AllowedName.IsMatch(name) && !FiveDigits.IsMatch(name) && !HexKey.IsMatch(name) ? name : KeyPlaceholder;

    /// <summary>
    /// The stricter form of <see cref="SafeName"/>, for names taken from live message data: returns the name only when
    /// it starts with an ASCII letter, has only ASCII letters, digits and underscore, is at most 24 characters and has at
    /// most one digit (this masks ids, uids and timestamps used as keys). Otherwise returns <c>&lt;key&gt;</c>.
    /// A short plain word still passes, so this narrows what can be printed; it cannot tell a field name from a word.
    /// </summary>
    public static string SafeNameStrict(string name)
    {
        if (name is null || !StrictName.IsMatch(name))
        {
            return KeyPlaceholder;
        }

        int digits = 0;
        foreach (char c in name)
        {
            if (c is >= '0' and <= '9')
            {
                digits++;
            }
        }

        return digits > 1 ? KeyPlaceholder : name;
    }

    /// <summary>True when an object has more keys than the strict mode prints by name (more than 30).</summary>
    internal static bool HasTooManyKeysForStrict(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && element.EnumerateObject().Count() > MaxStrictKeys;

    /// <summary>
    /// Returns a category value (for example <c>act_type</c>, <c>act</c>) if it is safe to print: starts with a lowercase letter,
    /// only lowercase letters/digits/<c>_.-</c>, at most 40 characters, no more than 3 digits, no 5 consecutive
    /// digits, not a hex string of 16 or more characters. Otherwise (including null or empty) returns <c>&lt;v&gt;</c>.
    /// </summary>
    public static string SafeToken(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || !AllowedToken.IsMatch(value)
            || FiveDigits.IsMatch(value)
            || HexKey.IsMatch(value))
        {
            return ValuePlaceholder;
        }

        int digits = 0;
        foreach (char c in value)
        {
            if (c is >= '0' and <= '9')
            {
                digits++;
            }
        }

        return digits > 3 ? ValuePlaceholder : value;
    }
}
