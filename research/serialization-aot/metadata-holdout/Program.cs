using System.Text.Json;
using System.Text.Json.Serialization;
using SerializationSpikes;
#if CODEC_CANDIDATE
using CandidateSerializer = SerializationSpikes.CodecSerializer;
#else
using CandidateSerializer = MetadataSerializer;
#endif

// These cases were held outside the shared implementation corpus. A passing
// main corpus proves feasibility; it must not hide silent attribute loss.
var candidate = new CandidateSerializer();
var failures = new List<string>();

var baseline = new ConditionalIgnore { Name = "x", Note = "keep" };
using (
    var payload = JsonDocument.Parse(
        JsonSerializer.Serialize(baseline, HoldoutOracle.Default.ConditionalIgnore)
    )
)
{
    if (payload.RootElement.GetProperty("note").GetString() != "keep")
    {
        throw new InvalidOperationException(
            "Independent oracle changed its conditional-ignore behavior."
        );
    }
}

var baselineChoice = JsonSerializer.Deserialize(
    "{\"name\":\"x\"}",
    HoldoutOracle.Default.ConstructorChoice
)!;
if (baselineChoice.Name != "x" || baselineChoice.Count != 7)
{
    throw new InvalidOperationException("Independent oracle changed its constructor choice.");
}

try
{
    using var payload = JsonDocument.Parse(
        candidate.Serialize(new ConditionalIgnore { Name = "x", Note = "keep" })
    );
    if (!payload.RootElement.TryGetProperty("note", out var note) || note.GetString() != "keep")
    {
        failures.Add("JsonIgnoreCondition.Never silently removes a member.");
    }
}
catch (Exception exception)
{
    failures.Add($"Conditional ignore case: {exception.GetType().Name}.");
}

try
{
    var value = candidate.Deserialize<ConstructorChoice>("{\"name\":\"x\"}");
    if (value.Name != "x" || value.Count != 7)
    {
        failures.Add("JsonConstructor selection changes the authored constructor or initializer.");
    }
}
catch (Exception exception)
{
    failures.Add($"JsonConstructor selection: {exception.GetType().Name}.");
}

foreach (var failure in failures)
{
    Console.WriteLine(failure);
}

Console.WriteLine($"Metadata holdout: {2 - failures.Count}/2 passed.");
return failures.Count == 0 ? 0 : 1;

[CompositeOutput]
public sealed class ConditionalIgnore
{
    public string Name { get; init; } = "x";

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Note { get; init; }
}

[CompositeOutput]
public sealed class ConstructorChoice
{
    [JsonConstructor]
    public ConstructorChoice(string name)
    {
        Name = name;
    }

    public ConstructorChoice(string name, int count)
    {
        Name = name;
        Count = count;
    }

    public string Name { get; }
    public int Count { get; init; } = 7;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true
)]
[JsonSerializable(typeof(ConditionalIgnore))]
[JsonSerializable(typeof(ConstructorChoice))]
internal partial class HoldoutOracle : JsonSerializerContext;
