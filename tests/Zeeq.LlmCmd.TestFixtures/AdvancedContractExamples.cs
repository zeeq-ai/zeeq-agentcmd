using System.Text.Json.Serialization;

namespace CommandRoot;

using Zeeq.LlmCmd;

public sealed record GraphParameters(string Document);

public sealed record GraphLeaf(string Text);

public sealed record GraphResult(
    Dictionary<string, GraphLeaf> Values,
    Dictionary<string, GraphLeaf?> NullableValues
);

public sealed partial class ExtractGraph : PromptCommand<GraphParameters, GraphResult>
{
    const string Id = "graph.extract";
    const string UserTemplate = "Read {Document}";
}

public sealed class RequiredMemberResult
{
    public required string Name { get; init; }
    public int Count { get; init; } = 4;
}

public sealed partial class ExtractRequiredMember
    : PromptCommand<GraphParameters, RequiredMemberResult>
{
    const string Id = "required.extract";
    const string UserTemplate = "Read {Document}";
}

[JsonDerivedType(typeof(DerivedGraph), "derived")]
public abstract record BaseGraph;

public sealed record DerivedGraph(GraphLeaf[] Children) : BaseGraph;

public sealed record PolymorphicResult(BaseGraph Part);

public sealed partial class ExtractPolymorphic : PromptCommand<GraphParameters, PolymorphicResult>
{
    const string Id = "polymorphic.extract";
    const string UserTemplate = "Read {Document}";
}

public sealed record ContractBox<T>(T Value);

public sealed record BoxResult(ContractBox<string> Box);

public sealed partial class ExtractBox : PromptCommand<GraphParameters, BoxResult>
{
    const string Id = "box.extract";
    const string UserTemplate = "Read {Document}";
}

// Non-nullable settable references require presence, even without an annotation.
public sealed class SetterResult
{
    public string Name { get; init; } = null!;
    public string? Note { get; init; }
}

public sealed partial class ExtractSetter : PromptCommand<GraphParameters, SetterResult>
{
    const string Id = "setter.extract";
    const string UserTemplate = "Read {Document}";
}

public sealed record StrictResult(string Name);

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(GraphParameters)), JsonSerializable(typeof(StrictResult))]
internal partial class StrictJson : JsonSerializerContext;

[PromptCommand("strict.extract", JsonContext = typeof(StrictJson))]
public sealed partial class ExtractStrict : PromptCommand<GraphParameters, StrictResult>
{
    const string UserTemplate = "Read {Document}";
}
