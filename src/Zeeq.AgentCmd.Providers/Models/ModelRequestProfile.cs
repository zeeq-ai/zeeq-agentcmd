using Microsoft.Extensions.AI;

namespace Zeeq.AgentCmd.Providers;

/// <summary>A sampling range, fixed value, or unsupported field. Omission preserves the provider's default.</summary>
/// <param name="Minimum">Inclusive lower bound; null means unsupported.</param>
/// <param name="Maximum">Inclusive upper bound; equal bounds represent a fixed value.</param>
/// <param name="RequiresNoReasoning">Whether the field is supported only when reasoning is explicitly disabled.</param>
public sealed record SamplingConstraint(
    float? Minimum,
    float? Maximum,
    bool RequiresNoReasoning = false
)
{
    /// <summary>A field that must be omitted, even if its supplied value is a common default.</summary>
    public static SamplingConstraint Unsupported { get; } = new(null, null);

    /// <summary>A provider-fixed value. Omitting the field is also valid.</summary>
    public static SamplingConstraint Fixed(float value) => new(value, value);

    internal bool Allows(float? value, string effort) =>
        value is null
        || Minimum is float min
            && Maximum is float max
            && float.IsFinite(value.Value)
            && value >= min
            && value <= max
            && (!RequiresNoReasoning || effort == "none");

    internal void Validate()
    {
        if (
            Minimum.HasValue != Maximum.HasValue
            || Minimum > Maximum
            || Minimum is float min && !float.IsFinite(min)
            || Maximum is float max && !float.IsFinite(max)
        )
        {
            throw new ArgumentException("Invalid sampling constraint.");
        }
    }
}

/// <summary>
/// Reviewed request capabilities, separate from prices. Profiles validate MEAI
/// options without changing host settings or guessing from a model prefix.
/// </summary>
public sealed class ModelRequestProfile
{
    /// <summary>Copies an exact identity and protocol-specific constraints into an immutable profile.</summary>
    public ModelRequestProfile(
        ProviderModelIdentity model,
        ApiKind api,
        SamplingConstraint temperature,
        SamplingConstraint topP,
        IEnumerable<string> reasoningEfforts,
        string defaultReasoningEffort,
        int? maxOutputTokens = null,
        bool supportsTopK = false,
        bool toolsRequireNoReasoning = false,
        string? sourceUrl = null,
        IEnumerable<string>? additionalSourceUrls = null
    )
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(temperature);
        ArgumentNullException.ThrowIfNull(topP);
        ArgumentNullException.ThrowIfNull(reasoningEfforts);
        temperature.Validate();
        topP.Validate();
        var efforts = reasoningEfforts.Distinct(StringComparer.Ordinal).ToArray();
        if (
            efforts.Any(string.IsNullOrWhiteSpace)
            || !efforts.Contains(defaultReasoningEffort, StringComparer.Ordinal)
            || maxOutputTokens < 1
        )
        {
            throw new ArgumentException("Invalid model request profile.");
        }

        Model = model;
        Api = api;
        Temperature = temperature;
        TopP = topP;
        ReasoningEfforts = Array.AsReadOnly(efforts);
        DefaultReasoningEffort = defaultReasoningEffort;
        MaxOutputTokens = maxOutputTokens;
        SupportsTopK = supportsTopK;
        ToolsRequireNoReasoning = toolsRequireNoReasoning;
        SourceUrl = sourceUrl;
        SourceUrls = Array.AsReadOnly(
            (sourceUrl is null ? Array.Empty<string>() : [sourceUrl])
                .Concat(additionalSourceUrls ?? [])
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        );
    }

    /// <summary>Exact underlying model and accepted versions.</summary>
    public ProviderModelIdentity Model { get; }

    /// <summary>API whose constraints were reviewed.</summary>
    public ApiKind Api { get; }

    /// <summary>Allowed temperature values and reasoning precondition.</summary>
    public SamplingConstraint Temperature { get; }

    /// <summary>Allowed nucleus-sampling values and reasoning precondition.</summary>
    public SamplingConstraint TopP { get; }

    /// <summary>Allowed wire effort values; unknown values are rejected.</summary>
    public IReadOnlyList<string> ReasoningEfforts { get; }

    /// <summary>Provider default used only to validate omitted effort; never written into the request.</summary>
    public string DefaultReasoningEffort { get; }

    /// <summary>Reviewed output ceiling, including reasoning; null leaves the ceiling unchecked.</summary>
    public int? MaxOutputTokens { get; }

    /// <summary>Whether a positive top-k value is supported.</summary>
    public bool SupportsTopK { get; }

    /// <summary>Whether Chat Completions tool requests require reasoning to be disabled.</summary>
    public bool ToolsRequireNoReasoning { get; }

    /// <summary>First-party capability reference, independent of the pricing source.</summary>
    public string? SourceUrl { get; }

    /// <summary>Capability sources, including platform and model-provider guidance when they differ.</summary>
    public IReadOnlyList<string> SourceUrls { get; }

    /// <summary>Returns violated field names. It does not inspect opaque SDK options or alter the request.</summary>
    public IReadOnlyList<string> Validate(ChatOptions? options)
    {
        if (options is null)
        {
            return Array.Empty<string>();
        }

        var violations = new List<string>();
        var effort = options.Reasoning?.Effort switch
        {
            null => DefaultReasoningEffort,
            ReasoningEffort.None => "none",
            ReasoningEffort.Low => "low",
            ReasoningEffort.Medium => "medium",
            ReasoningEffort.High => "high",
            ReasoningEffort.ExtraHigh => "xhigh",
            _ => "unknown",
        };
        if (!ReasoningEfforts.Contains(effort, StringComparer.Ordinal))
        {
            violations.Add("reasoning.effort");
        }
        if (!Temperature.Allows(options.Temperature, effort))
        {
            violations.Add("temperature");
        }
        if (!TopP.Allows(options.TopP, effort))
        {
            violations.Add("top_p");
        }
        if (options.TopK is int topK && (!SupportsTopK || topK <= 0))
        {
            violations.Add("top_k");
        }
        if (
            options.MaxOutputTokens is int tokens
            && (tokens < 1 || MaxOutputTokens is int ceiling && tokens > ceiling)
        )
        {
            violations.Add("max_output_tokens");
        }
        if (options.Tools is { Count: > 0 } && ToolsRequireNoReasoning && effort != "none")
        {
            violations.Add("tools_with_reasoning");
        }

        return violations.AsReadOnly();
    }
}

/// <summary>Frozen exact-match capabilities. Hosts can supply reviewed profiles for new platforms or models.</summary>
public sealed class ModelRequestCatalog
{
    /// <summary>Copies profiles and rejects ambiguous platform, API and model IDs.</summary>
    public ModelRequestCatalog(IEnumerable<ModelRequestProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var copy = profiles.ToArray();
        var keys = new HashSet<(string, ApiKind, string)>();
        foreach (var profile in copy)
        {
            ArgumentNullException.ThrowIfNull(profile);
            foreach (var id in profile.Model.ResponseIds)
            {
                if (!keys.Add((profile.Model.ProviderKey.ToLowerInvariant(), profile.Api, id)))
                {
                    throw new ArgumentException(
                        "Overlapping model request profiles.",
                        nameof(profiles)
                    );
                }
            }
        }

        Profiles = Array.AsReadOnly(copy);
    }

    /// <summary>Reviewed profiles for the current test matrix. Unknown models are not implicitly supported.</summary>
    public static ModelRequestCatalog Default { get; } = new(CreateDefaults());

    /// <summary>Frozen profile definitions.</summary>
    public IReadOnlyList<ModelRequestProfile> Profiles { get; }

    /// <summary>Resolves one exact underlying model, never an arbitrary deployment-name prefix.</summary>
    public ModelRequestProfile? Find(string providerKey, ApiKind api, string modelId) =>
        Profiles.SingleOrDefault(profile =>
            string.Equals(
                profile.Model.ProviderKey,
                providerKey,
                StringComparison.OrdinalIgnoreCase
            )
            && profile.Api == api
            && profile.Model.Matches(modelId)
        );

    /// <summary>Creates the one explicit target mapping shared by model validation and pricing.</summary>
    public ModelTargetBinding Bind(string providerKey, ApiKind api, string target, string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        var profile =
            Find(providerKey, api, modelId)
            ?? throw new ArgumentException(
                "No reviewed model request profile matches the binding.",
                nameof(modelId)
            );
        return new(target, profile.Model);
    }

    static IEnumerable<ModelRequestProfile> CreateDefaults()
    {
        foreach (var model in KnownProviderModels.All)
        {
            if (model.ProviderKey == "anthropic")
            {
                // Sonnet 5.5 rejects thinking:disabled; the pinned SDK's None
                // mapping cannot express between_tools, so use adaptive Low.
                yield return new(
                    model,
                    ApiKind.Messages,
                    SamplingConstraint.Unsupported,
                    SamplingConstraint.Unsupported,
                    model.ModelId == "claude-haiku-5-5"
                        ? ["none", "low", "medium", "high", "xhigh", "max"]
                        : ["low", "medium", "high", "xhigh", "max"],
                    model.ModelId == "claude-sonnet-5-5" ? "high" : "medium",
                    maxOutputTokens: 128_000,
                    sourceUrl: model.ModelId switch
                    {
                        "claude-haiku-5-5" =>
                            "https://platform.claude.com/docs/en/models/haiku-5-5/overview",
                        "claude-sonnet-5-5" =>
                            "https://platform.claude.com/docs/en/models/sonnet-5-5/overview",
                        _ => "https://platform.claude.com/docs/en/models/opus-5-5/overview",
                    }
                );
            }
            else
            {
                bool requiresReasoning = model.ModelId is "gpt-6-astra" or "gpt-6.1-sol";
                // OpenAI documents sampling only with none. Microsoft's broad
                // support table is less specific; absence is the safe live default.
                yield return new(
                    model,
                    ApiKind.ChatCompletions,
                    new(0, 2, true),
                    new(0, 1, true),
                    requiresReasoning
                        ? ["low", "medium", "high", "xhigh"]
                        : ["none", "low", "medium", "high", "xhigh"],
                    "medium",
                    maxOutputTokens: 128_000,
                    toolsRequireNoReasoning: true,
                    sourceUrl: model.ProviderKey == "azureopenai"
                        ? "https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/reasoning"
                        : "https://developers.openai.com/api/docs/guides/latest-model",
                    additionalSourceUrls:
                    [
                        "https://developers.openai.com/api/docs/guides/latest-model",
                        "https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-sold-directly-by-azure",
                    ]
                );
            }
        }
    }
}
