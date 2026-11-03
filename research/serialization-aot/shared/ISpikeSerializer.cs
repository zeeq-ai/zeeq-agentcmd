using System.Text.Json;

namespace SerializationSpikes;

/// <summary>Both candidate paths expose the same typed operations to the independent harness.</summary>
public interface ISpikeSerializer
{
    string Name { get; }

    string Serialize<TValue>(TValue value);

    TValue Deserialize<TValue>(string json);

    JsonElement Schema<TValue>();
}
