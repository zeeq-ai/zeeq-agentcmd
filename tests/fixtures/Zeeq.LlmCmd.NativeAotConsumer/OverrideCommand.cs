using System.Text.Json;
using System.Text.Json.Serialization;
using Zeeq.LlmCmd;

namespace CompositionDemo;

/// <summary>Only this command opts into an authored context.</summary>
[PromptCommand("native.override", JsonContext = typeof(OverrideContext))]
public sealed partial class OverrideCommand
    : PromptCommand<OverrideInput, OverrideOutput>,
        IInputValidator<OverrideInput>,
        IResultValidator<OverrideOutput>
{
    /// <summary>Prompt used by the local response client.</summary>
    public const string UserTemplate = "OVERRIDE {Document}";

    static string? IInputValidator<OverrideInput>.ValidateInput(OverrideInput input) =>
        string.IsNullOrWhiteSpace(input.Document) ? "Document is required" : null;

    static string? IResultValidator<OverrideOutput>.ValidateResult(OverrideOutput result) =>
        result.ReceiptTotal < 0 ? "Receipt total is negative" : null;
}

/// <summary>Input for the explicit context probe.</summary>
public sealed record OverrideInput(string Document);

/// <summary>Output whose wire name depends on the explicit context.</summary>
public sealed record OverrideOutput(int ReceiptTotal)
{
    /// <summary>Shows the authored context can own extension data.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; init; } = new();
}

/// <summary>Explicit context for the separate override probe.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(OverrideInput))]
[JsonSerializable(typeof(OverrideOutput))]
public partial class OverrideContext : JsonSerializerContext;
