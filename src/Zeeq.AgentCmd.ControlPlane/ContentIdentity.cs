using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zeeq.AgentCmd.ControlPlane;

/// <summary>Stable JSON content identity without altering array order, string meaning or numeric representation.</summary>
public static class ContentIdentity
{
    /// <summary>Normalizes object member order and rejects duplicate keys before content can become immutable.</summary>
    public static string Normalize(JsonElement value)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            Write(writer, value, 0);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>Hashes exact normalized UTF-8 content; the digest proves content, not publisher authority.</summary>
    public static string Hash(string normalized) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));

    private static void Write(Utf8JsonWriter writer, JsonElement value, int depth)
    {
        if (depth > 64)
        {
            throw new ManagementException("content_too_deep");
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = value
                .EnumerateObject()
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToArray();
            if (
                properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count()
                != properties.Length
            )
            {
                throw new ManagementException("duplicate_json_member");
            }
            writer.WriteStartObject();
            foreach (var property in properties)
            {
                writer.WritePropertyName(property.Name);
                Write(writer, property.Value, depth + 1);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray())
            {
                Write(writer, item, depth + 1);
            }
            writer.WriteEndArray();
        }
        else if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw new ManagementException("content_required");
        }
        else
        {
            value.WriteTo(writer);
        }
    }
}
