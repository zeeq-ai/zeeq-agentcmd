namespace ProviderAdapters.Platforms;

/// <summary>
/// OpenAI Chat Completions wire shape. Cached and reasoning tokens are subsets of prompt and completion totals; nothing is added to a total.
/// Register with <c>providerKey: null</c> only to opt in every Chat Completions client; otherwise register one instance per explicit provider key
/// (for example a compatible gateway whose wire shape is verified identical). Responses API is a different adapter.
/// </summary>
public sealed class OpenAiChatAdapter(string? providerKey = "openai") : IProviderResponseAdapter
{
    public string Id => "openai.chat-completions" + (providerKey is null ? "" : ":" + providerKey);
    public string Version => "0.1.0";
    public string RulesRevision => "provider-adapters.md@2026-11-01#openai-chat";
    public string? ProviderKey => providerKey;
    public ApiKind Api => ApiKind.ChatCompletions;
    public StreamUsageMode StreamUsage => StreamUsageMode.LatestSnapshot;
    public IReadOnlyDictionary<string, AdditionalCountRule> AdditionalCounts => ChatCompletionsWire.MeaiAdditionalCounts;

    public void Enrich(AdapterContext context)
    {
        ChatCompletionsWire.Read(context);
        context.Usage.CheckSum(Dim.Total, [Dim.Input, Dim.Output]);
    }
}
