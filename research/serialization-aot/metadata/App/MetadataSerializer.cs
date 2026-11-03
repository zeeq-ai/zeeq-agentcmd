using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SerializationSpikes;

/// <summary>Uses generated metadata for discovered roots without reflection.</summary>
public sealed class MetadataSerializer : ISpikeSerializer
{
    private readonly JsonSerializerOptions options = new()
    {
        TypeInfoResolver = new GeneratedMetadataResolver(),
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public string Name => "metadata";

    /// <summary>Allows host naming and schema settings before the first use.</summary>
    public JsonSerializerOptions Options => options;

    public string Serialize<TValue>(TValue value) =>
        JsonSerializer.Serialize(value, GetInfo<TValue>());

    public TValue Deserialize<TValue>(string json) =>
        JsonSerializer.Deserialize(json, GetInfo<TValue>())!;

    public JsonElement Schema<TValue>()
    {
        using var document = JsonDocument.Parse(
            GetInfo<TValue>().GetJsonSchemaAsNode().ToJsonString()
        );
        return document.RootElement.Clone();
    }

    private JsonTypeInfo<TValue> GetInfo<TValue>()
    {
        if (options.Converters.Count != 0)
        {
            throw new NotSupportedException(
                "Options-level converters are not supported by this spike."
            );
        }

        return (JsonTypeInfo<TValue>)options.GetTypeInfo(typeof(TValue));
    }
}
