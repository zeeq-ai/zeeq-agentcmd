namespace Zeeq.AgentCmd.Providers;

/// <summary>
/// Reviewed model identity shared by pricing and request validation. Deployment
/// names are host configuration; they must not become model-name heuristics.
/// </summary>
public sealed class ProviderModelIdentity
{
    /// <summary>Copies an exact canonical ID and its explicitly supported response aliases or versions.</summary>
    public ProviderModelIdentity(
        string providerKey,
        string modelId,
        IReadOnlyList<string>? responseIds = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ProviderKey = providerKey;
        ModelId = modelId;
        var ids = new[] { modelId }
            .Concat(responseIds ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Model IDs must be nonempty.", nameof(responseIds));
        }

        ResponseIds = Array.AsReadOnly(ids);
    }

    /// <summary>Platform identity; direct OpenAI and Azure remain distinct.</summary>
    public string ProviderKey { get; }

    /// <summary>Canonical underlying model, independent of a deployment name.</summary>
    public string ModelId { get; }

    /// <summary>Exact accepted IDs; no arbitrary date suffix or prefix is accepted.</summary>
    public IReadOnlyList<string> ResponseIds { get; }

    /// <summary>Checks an exact alias or version against this reviewed identity.</summary>
    public bool Matches(string? modelId) =>
        modelId is not null && ResponseIds.Contains(modelId, StringComparer.Ordinal);
}

/// <summary>One host-declared target-to-model mapping, shared by request validation and pricing drift checks.</summary>
/// <param name="Target">Requested model or deployment target.</param>
/// <param name="Model">Reviewed underlying identity and accepted response versions.</param>
public sealed record ModelTargetBinding(string Target, ProviderModelIdentity Model);

// Versions come from Microsoft's model listing. A newly observed version must be
// reviewed before it inherits a price or capability policy from an older model.
internal static class KnownProviderModels
{
    internal static readonly IReadOnlyList<ProviderModelIdentity> All = Array.AsReadOnly(
        new[] { "openai", "azureopenai" }
            .SelectMany(provider =>
                new[]
                {
                    new ProviderModelIdentity(provider, "gpt-5.6-sol", ["gpt-5.6-sol-2026-07-09"]),
                    new ProviderModelIdentity(
                        provider,
                        "gpt-5.6-terra",
                        ["gpt-5.6-terra-2026-07-09"]
                    ),
                    new ProviderModelIdentity(provider, "gpt-6-astra", ["gpt-6-astra-2026-09-03"]),
                    new ProviderModelIdentity(provider, "gpt-6-luna", ["gpt-6-luna-2026-09-22"]),
                    new ProviderModelIdentity(provider, "gpt-6-sol", ["gpt-6-sol-2026-09-22"]),
                    new ProviderModelIdentity(provider, "gpt-6.1-sol", ["gpt-6.1-sol-2026-09-29"]),
                }
            )
            .Concat(
                new[]
                {
                    new ProviderModelIdentity("anthropic", "claude-haiku-5-5"),
                    new ProviderModelIdentity("anthropic", "claude-sonnet-5-5"),
                    new ProviderModelIdentity("anthropic", "claude-opus-5-5"),
                }
            )
            .ToArray()
    );

    internal static ProviderModelIdentity Get(string provider, string model) =>
        All.Single(identity => identity.ProviderKey == provider && identity.ModelId == model);
}
