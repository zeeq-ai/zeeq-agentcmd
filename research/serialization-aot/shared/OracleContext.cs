using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SerializationSpikes;

// Registration markup is confined to the independent oracle. Neither candidate
// may resolve metadata through this context; that would conceal a missing emitter.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
)]
[JsonSerializable(typeof(DocumentParameters))]
[JsonSerializable(typeof(Header))]
[JsonSerializable(typeof(Line[]))]
[JsonSerializable(typeof(DocumentWhole))]
internal partial class OracleContext : JsonSerializerContext;

/// <summary>STJ's generated contract is the stored-data compatibility oracle.</summary>
public sealed class OracleSerializer : ISpikeSerializer
{
    private readonly JsonSerializerOptions options;

    public OracleSerializer()
    {
        options = new JsonSerializerOptions(OracleContext.Default.Options)
        {
            // STJ cannot recover the nullable argument of a closed generic from
            // CLR reflection. Production's symbol modifier supplies this too.
            TypeInfoResolver = OracleContext.Default.WithAddedModifier(info =>
            {
                if (info.Type == typeof(Box<Address>))
                {
                    info.Properties[0].IsGetNullable = false;
                    info.Properties[0].IsSetNullable = false;
                }

                if (info.Type == typeof(Box<string>))
                {
                    info.Properties[0].IsGetNullable = true;
                    info.Properties[0].IsSetNullable = true;
                }
            }),
        };
        options.MakeReadOnly();
    }

    public string Name => "stj-oracle";

    public string Serialize<TValue>(TValue value)
    {
        return JsonSerializer.Serialize(value, TypeInfo<TValue>());
    }

    public TValue Deserialize<TValue>(string json)
    {
        return JsonSerializer.Deserialize(json, TypeInfo<TValue>())!;
    }

    public JsonElement Schema<TValue>()
    {
        using var document = JsonDocument.Parse(
            TypeInfo<TValue>().GetJsonSchemaAsNode().ToJsonString()
        );
        return document.RootElement.Clone();
    }

    private JsonTypeInfo<TValue> TypeInfo<TValue>()
    {
        return (JsonTypeInfo<TValue>)options.GetTypeInfo(typeof(TValue));
    }
}
