using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HumanGrading;

public static class JsonPointer
{
    public static bool TryGet(JsonElement document, string pointer, out JsonElement value)
    {
        value = document;
        if (!TryTokens(pointer, out var tokens) || document.ValueKind == JsonValueKind.Undefined) return false;
        foreach (var token in tokens)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (!value.TryGetProperty(token, out value)) return false;
            }
            else if (value.ValueKind == JsonValueKind.Array && TryIndex(token, value.GetArrayLength(), out var index)) value = value[index];
            else return false;
        }
        return true;
    }

    // Replacement only. Adding/removing/reordering array items is a separate patch contract.
    public static bool TryReplace(JsonElement document, string pointer, JsonElement replacement, out JsonElement result)
    {
        result = default;
        if (replacement.ValueKind == JsonValueKind.Undefined || !TryGet(document, pointer, out _) || !TryTokens(pointer, out var tokens)) return false;
        if (tokens.Length == 0) { result = replacement.Clone(); return true; }
        var node = JsonNode.Parse(document.GetRawText());
        for (int i = 0; i < tokens.Length - 1; i++)
        {
            node = node is JsonArray array && TryIndex(tokens[i], array.Count, out var index)
                ? array[index] : node is JsonObject obj ? obj[tokens[i]] : null;
        }
        var replace = JsonNode.Parse(replacement.GetRawText());
        if (node is JsonObject target) target[tokens[^1]] = replace;
        else if (node is JsonArray array && TryIndex(tokens[^1], array.Count, out var index)) array[index] = replace;
        else return false;
        // Serialize the changed root, not just the selected nested node.
        while (node.Parent is not null) node = node.Parent;
        result = JsonSerializer.SerializeToElement(node);
        return true;
    }

    public static bool HasUniqueKeys(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => UniqueObject(value),
        JsonValueKind.Array => value.EnumerateArray().All(HasUniqueKeys),
        JsonValueKind.Undefined => false,
        _ => true
    };
    static bool UniqueObject(JsonElement value)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().All(p => keys.Add(p.Name) && HasUniqueKeys(p.Value));
    }
    static bool TryIndex(string token, int length, out int index)
    {
        index = -1;
        return token.Length > 0 && (token.Length == 1 || token[0] != '0') &&
            int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index >= 0 && index < length;
    }
    static bool TryTokens(string pointer, out string[] tokens)
    {
        tokens = [];
        if (pointer is null) return false;
        if (pointer.Length == 0) return true;
        if (pointer[0] != '/') return false;
        var parts = pointer[1..].Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            var decoded = new StringBuilder();
            for (int j = 0; j < parts[i].Length; j++)
            {
                char c = parts[i][j];
                if (c != '~') { decoded.Append(c); continue; }
                if (++j >= parts[i].Length || parts[i][j] is not ('0' or '1')) return false;
                decoded.Append(parts[i][j] == '0' ? '~' : '/');
            }
            parts[i] = decoded.ToString();
        }
        tokens = parts; return true;
    }
}
