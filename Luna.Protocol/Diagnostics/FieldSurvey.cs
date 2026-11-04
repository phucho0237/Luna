using System.Globalization;
using System.Text.Json;

namespace Luna.Protocol.Diagnostics;

/// <summary>
/// Field survey across many JSON elements: for each field (down to depth 2, named like <c>parent.child</c>) which types were seen
/// and how many times. Used to learn the real type of a field seen only in the first element. Never stores or prints values;
/// field names go through <see cref="JsonShape.SafeName"/>.
/// </summary>
public sealed class FieldSurvey
{
    private const int MaxDepth = 2;

    private readonly Dictionary<string, Dictionary<string, int>> _fields = new(StringComparer.Ordinal);
    private int _total;

    /// <summary>Adds one element. An element that is not an object is skipped.</summary>
    public void Add(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        _total++;
        // Each path counts once per element (many keys masked as <key> must not push a count above the total).
        var counted = new HashSet<string>(StringComparer.Ordinal);
        Visit(element, prefix: "", depth: 1, counted);
    }

    /// <summary>Each line: <c>name: type x count, ...</c>, plus <c>absent x N</c> if some element lacks the field. Sorted by name.</summary>
    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string>();
        foreach (string name in _fields.Keys.Order(StringComparer.Ordinal))
        {
            Dictionary<string, int> kinds = _fields[name];
            var parts = kinds
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => string.Create(CultureInfo.InvariantCulture, $"{pair.Key} x{pair.Value}"))
                .ToList();

            int absent = _total - kinds.Values.Sum();
            if (absent > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"absent x{absent}"));
            }

            lines.Add($"{name}: {string.Join(", ", parts)}");
        }

        return lines;
    }

    private void Visit(JsonElement obj, string prefix, int depth, HashSet<string> counted)
    {
        foreach (JsonProperty property in obj.EnumerateObject())
        {
            string path = prefix + JsonShape.SafeName(property.Name);
            string kind = KindOf(property.Value);
            if (counted.Add(path))
            {
                if (!_fields.TryGetValue(path, out Dictionary<string, int>? kinds))
                {
                    kinds = new Dictionary<string, int>(StringComparer.Ordinal);
                    _fields[path] = kinds;
                }

                kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
            }

            if (property.Value.ValueKind == JsonValueKind.Object && depth < MaxDepth)
            {
                Visit(property.Value, path + ".", depth + 1, counted);
            }
        }
    }

    private static string KindOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        _ => "other",
    };
}
