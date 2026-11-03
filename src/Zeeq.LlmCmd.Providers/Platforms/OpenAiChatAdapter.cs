namespace Zeeq.LlmCmd.Providers.Platforms;

/// <summary>
/// OpenAI Chat Completions wire shape. Cached and reasoning tokens are subsets of prompt and completion totals; nothing is added to a total.
/// Register with <c>providerKey: null</c> only to opt in every Chat Completions client; otherwise register one instance per explicit provider key
/// (for example a compatible gateway whose wire shape is verified identical). Responses API is a different adapter.
/// </summary>
public sealed class OpenAiChatAdapter(string? providerKey = "openai") : IProviderResponseAdapter
{
    /// <inheritdoc/>
    public string Id => "openai.chat-completions" + (providerKey is null ? "" : ":" + providerKey);

    /// <inheritdoc/>
    public string Version => "0.1.0";

    /// <inheritdoc/>
    public string RulesRevision => "provider-adapters.md@2026-11-01#openai-chat";

    /// <inheritdoc/>
    public string? ProviderKey => providerKey;

    /// <inheritdoc/>
    public ApiKind Api => ApiKind.ChatCompletions;

    /// <inheritdoc/>
    public StreamUsageMode StreamUsage => StreamUsageMode.LatestSnapshot;

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, AdditionalCountRule> AdditionalCounts =>
        ChatCompletionsWire.MeaiAdditionalCounts;

    /// <inheritdoc/>
    public void Enrich(AdapterContext context)
    {
        var tier = context.ReadCategory(
            context.Wire,
            "service_tier",
            "$",
            "auto",
            "default",
            "standard",
            "flex",
            "scale",
            "priority"
        );
        if (tier is not null)
        {
            context.BillingContext = new(tier, null, null);
        }

        ChatCompletionsWire.Read(context);
        context.Usage.CheckSum(Dim.Total, [Dim.Input, Dim.Output]);
    }
}
