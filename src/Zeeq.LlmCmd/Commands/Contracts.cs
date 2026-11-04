using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Zeeq.LlmCmd;

/// <summary>Optional explicit metadata for a source-declared typed command.</summary>
/// <param name="id">Stable command definition key.</param>
[AttributeUsage(AttributeTargets.Class)]
public sealed class PromptCommandAttribute(string id) : Attribute
{
    /// <summary>Stable command definition key.</summary>
    public string Id { get; } = id;
    /// <summary>Definition revision; defaults to one.</summary>
    public int Revision { get; set; } = 1;
    /// <summary>Optional named execution profile.</summary>
    public string Profile { get; set; } = "";
    /// <summary>Source-generated JSON serializer context, when explicitly supplied.</summary>
    public Type? JsonContext
    {
        get; set;
    }
}

/// <summary>Serialized input parameter retained in an execution snapshot.</summary>
/// <param name="Name">CLR parameter name.</param>
/// <param name="TypeId">Stable type identifier.</param>
/// <param name="Value">Serialized parameter value.</param>
/// <param name="JsonName">Serialized property name, when different or available.</param>
public sealed record ParameterRecord(string Name, string TypeId, JsonElement Value, string? JsonName = null);

/// <summary>Immutable generated command definition, independent of client and invocation state.</summary>
/// <param name="Id">Stable command key.</param>
/// <param name="Revision">Definition revision.</param>
/// <param name="Profile">Optional profile override.</param>
/// <param name="SystemTemplate">Unrendered system template.</param>
/// <param name="UserTemplate">Unrendered user template.</param>
/// <param name="DefinitionHash">Hash identifying the definition.</param>
/// <param name="SchemaName">Provider-compatible schema name.</param>
/// <param name="Schema">Lazy result JSON schema.</param>
/// <param name="ResultType">Result serializer metadata, when supplied.</param>
/// <param name="Render">Renders the user message from parameters.</param>
/// <param name="RenderSystem">Renders the system message from parameters.</param>
/// <param name="Project">Captures serialized input parameters.</param>
/// <param name="ValidateInput">Returns an input validation error, or null.</param>
/// <param name="ValidateResult">Returns a result validation error, or null.</param>
/// <param name="Contract">Typed JSON serialization contract, when supplied.</param>
/// <param name="ConfigureDefaults">Optional command-level settings defaults.</param>
/// <param name="RuntimeBinding">Generated reflection-free runtime template and parameter binding, when supplied.</param>
public sealed record PromptDescriptor<TP, TR>(
    string Id,
    int Revision,
    string Profile,
    string SystemTemplate,
    string UserTemplate,
    string DefinitionHash,
    string SchemaName,
    Lazy<JsonElement> Schema,
    JsonTypeInfo<TR>? ResultType,
    Func<TP, string> Render,
    Func<TP, string> RenderSystem,
    Func<TP, IReadOnlyList<ParameterRecord>> Project,
    Func<TP, string?> ValidateInput,
    Func<TR, string?> ValidateResult,
    CommandJsonContract<TP, TR>? Contract = null,
    Action<ChatSettingsPatch>? ConfigureDefaults = null,
    IRuntimeBinding<TP>? RuntimeBinding = null);

/// <summary>Application call site associated with an invocation.</summary>
/// <param name="Member">Calling member name.</param>
/// <param name="File">Calling source file.</param>
/// <param name="Line">Calling source line.</param>
public sealed record CallSite(string Member, string File, int Line);

/// <summary>Captured command definition, parameters and selected execution settings.</summary>
/// <param name="PromptId">Command definition key.</param>
/// <param name="Revision">Definition revision.</param>
/// <param name="DefinitionHash">Definition hash.</param>
/// <param name="RawTemplate">Unrendered user template.</param>
/// <param name="FormattedPrompt">Rendered prompt, when capture is enabled.</param>
/// <param name="Parameters">Captured serialized input parameters.</param>
/// <param name="Profile">Selected profile name.</param>
/// <param name="Model">Selected model, if set.</param>
/// <param name="Temperature">Selected temperature, if set.</param>
/// <param name="Routing">Routing and attempt capture, if available.</param>
/// <param name="Experiment">Effective experiment and source evidence, when configured.</param>
public sealed record ExecutionSnapshot(
    string PromptId,
    int Revision,
    string DefinitionHash,
    string RawTemplate,
    string? FormattedPrompt,
    IReadOnlyList<ParameterRecord> Parameters,
    string Profile,
    string? Model,
    float? Temperature,
    RouteCapture? Routing = null,
    ExperimentEvidence? Experiment = null);

/// <summary>Named runtime settings supplied by the host.</summary>
/// <param name="Model">Model identifier, if specified.</param>
/// <param name="Temperature">Sampling temperature, if specified.</param>
public sealed record Profile(string? Model = null, float? Temperature = null);

/// <summary>Best-effort observation of a completed command execution.</summary>
/// <param name="ExecutionId">Execution identifier.</param>
/// <param name="Outcome">Outcome category.</param>
/// <param name="Snapshot">Captured definition and settings, if available.</param>
/// <param name="Context">Captured execution context, if available.</param>
public sealed record ExecutionOutcomeRecord(
    Guid ExecutionId,
    string Outcome,
    ExecutionSnapshot? Snapshot,
    CommandExecutionContext? Context = null);

/// <summary>Mutable overrides applied to a command invocation or defaults.</summary>
public sealed class ChatSettingsPatch
{
    /// <summary>Sampling temperature override; null leaves the existing setting unchanged.</summary>
    public float? Temperature
    {
        get; set;
    }
}

/// <summary>Delivery is separate from model success. Durability depends on the configured ledger.</summary>
public closed record OutputDelivery
{
    /// <summary>The ledger accepted the output artifact.</summary>
    /// <param name="Output">Exact stored output and reference.</param>
    public sealed record Stored(OutputArtifact Output) : OutputDelivery;
    /// <summary>The ledger refused to store the output.</summary>
    /// <param name="Code">Machine-readable rejection reason.</param>
    public sealed record Rejected(string Code) : OutputDelivery;
    /// <summary>The ledger operation failed; the model result may still be valid.</summary>
    public sealed record Failed : OutputDelivery;
}
