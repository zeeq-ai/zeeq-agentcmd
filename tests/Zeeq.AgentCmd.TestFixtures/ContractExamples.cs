using System.Text.Json.Serialization;

namespace CommandRoot;

using Zeeq.AgentCmd;

// Ordinary application records: no JsonSerializable context or JsonRequired attributes.
public sealed record ContractParameters(
    [property: JsonPropertyName("document_text")] string Document,
    string? Locale = "en",
    [property: JsonIgnore] string? InternalNote = null
);

public sealed record ContractChild(string? Note);

public sealed record ContractResult(
    [property: JsonPropertyName("display_name")] string? Label,
    ContractChild[] Children,
    int Count = 7
)
{
    [JsonIgnore]
    public string? InternalValue { get; init; }
}

public sealed partial class ContractExtract : PromptCommand<ContractParameters, ContractResult>
{
    public const string Id = "contract.extract";
    public const string UserTemplate = "Read {Document} ({Locale})";
}

public sealed partial class ContractDefaultsExtract
    : PromptCommand<ContractParameters, ContractResult>
{
    public const string Id = "contract.defaults";
    public const string UserTemplate = "Read {Document}";

    static partial void ConfigureDefaults(ChatSettingsPatch defaults) => defaults.Temperature = .2f;
}

public sealed partial class ContractThrowDefaultsExtract
    : PromptCommand<ContractParameters, ContractResult>
{
    public const string Id = "contract.defaults.throw";
    public const string UserTemplate = "Read {Document}";

    static partial void ConfigureDefaults(ChatSettingsPatch defaults) =>
        throw new InvalidOperationException("fixture default failed");
}
