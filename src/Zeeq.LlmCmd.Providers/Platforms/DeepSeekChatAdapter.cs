namespace Zeeq.LlmCmd.Providers.Platforms;

/// <summary>
/// DeepSeek Chat Completions. Reuses the OpenAI-shaped usage reader and adds <c>prompt_cache_hit_tokens</c> (alias of nested cached tokens)
/// and <c>prompt_cache_miss_tokens</c> (a detail of the prompt total, not an Anthropic-style cache-creation category).
/// DeepSeek puts usage on the last choice-bearing chunk when streaming. Its Responses and Messages APIs are not covered.
/// </summary>
public sealed class DeepSeekChatAdapter : IProviderResponseAdapter
{
    /// <inheritdoc/>
    public string Id => "deepseek.chat-completions";

    /// <inheritdoc/>
    public string Version => "0.1.0";

    /// <inheritdoc/>
    public string RulesRevision => "provider-adapters.md@2026-11-01#deepseek-chat";

    /// <inheritdoc/>
    public string? ProviderKey => "deepseek";

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
        ChatCompletionsWire.Read(context, "prompt_cache_hit_tokens", "prompt_cache_miss_tokens");
        if (context.ReadObject(context.Wire, "usage", "$") is { } usage)
        {
            if (context.ReadCount(usage, "prompt_cache_hit_tokens", "$.usage") is { } hit)
            {
                context.Usage.Observe(
                    Dim.Cached,
                    hit,
                    Unit.Tokens,
                    Relation.SubsetOfParent,
                    Dim.Input,
                    Origin.Native,
                    "wire:usage.prompt_cache_hit_tokens"
                );
            }

            if (context.ReadCount(usage, "prompt_cache_miss_tokens", "$.usage") is { } miss)
            {
                context.Usage.Observe(
                    Dim.DeepSeekCacheMiss,
                    miss,
                    Unit.Tokens,
                    Relation.SubsetOfParent,
                    Dim.Input,
                    Origin.Native,
                    "wire:usage.prompt_cache_miss_tokens"
                );
            }
        }
        context.Usage.CheckSum(Dim.Input, [Dim.Cached, Dim.DeepSeekCacheMiss]);
        context.Usage.CheckSum(Dim.Total, [Dim.Input, Dim.Output]);
    }
}
