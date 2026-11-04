using Microsoft.Extensions.AI;

namespace Zeeq.AgentCmd;

/// <summary>
/// Portable inference defaults for one profile. Immutable values can be included
/// in replay identity; provider-specific behaviors remain in MEAI middleware.
/// </summary>
/// <param name="Temperature">Sampling temperature; null preserves command or provider defaults.</param>
/// <param name="MaxOutputTokens">Output budget, including reasoning where the provider counts it.</param>
/// <param name="ReasoningEffort">Reasoning effort; null leaves the provider's default unchanged.</param>
/// <param name="TopP">Nucleus sampling value between zero and one, when supported.</param>
/// <param name="TopK">Positive top-k limit, when supported.</param>
/// <param name="ReasoningOutput">Requested reasoning output mode, when supported.</param>
public sealed record ProfileDefaults(
    float? Temperature = null,
    int? MaxOutputTokens = null,
    ReasoningEffort? ReasoningEffort = null,
    float? TopP = null,
    int? TopK = null,
    ReasoningOutput? ReasoningOutput = null
)
{
    internal bool IsEmpty =>
        Temperature is null
        && MaxOutputTokens is null
        && ReasoningEffort is null
        && TopP is null
        && TopK is null
        && ReasoningOutput is null;

    internal bool IsValid =>
        (Temperature is not float temperature || float.IsFinite(temperature))
        && (TopP is not float topP || float.IsFinite(topP) && topP is >= 0 and <= 1)
        && (MaxOutputTokens is null or > 0)
        && (TopK is null or > 0);

    internal void Apply(ChatOptions options)
    {
        options.MaxOutputTokens ??= MaxOutputTokens;
        options.TopP ??= TopP;
        options.TopK ??= TopK;

        if (
            options.Reasoning is not null
            || ReasoningEffort is not null
            || ReasoningOutput is not null
        )
        {
            // ChatOptions.Clone shares nested objects. Copy reasoning before a
            // provider or host middleware can change another request's settings.
            options.Reasoning = new ReasoningOptions
            {
                Effort = options.Reasoning?.Effort ?? ReasoningEffort,
                Output = options.Reasoning?.Output ?? ReasoningOutput,
            };
        }
    }
}
