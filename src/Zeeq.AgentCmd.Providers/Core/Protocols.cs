using Zeeq.AgentCmd.Providers.Platforms;

namespace Zeeq.AgentCmd.Providers;

/// <summary>
/// A provider key, wire protocol and the adapters that read it, bundled so a host names the protocol
/// once. The host still chooses it explicitly; capture never infers a provider from a model name or
/// SDK type, because the same model can be served by providers with different accounting.
/// </summary>
/// <param name="ProviderKey">Provider identifier used for adapter selection and pricing.</param>
/// <param name="Api">Wire protocol the client speaks.</param>
/// <param name="Adapters">Adapters for this protocol; empty keeps normalized MEAI fields only.</param>
public sealed record ProviderProtocol(
    string ProviderKey,
    ApiKind Api,
    IReadOnlyList<IProviderResponseAdapter> Adapters
)
{
    /// <summary>Creates the provider identity for one client registration.</summary>
    /// <param name="registrationId">Client registration name, such as the DI key.</param>
    /// <param name="endpoint">Optional endpoint; credentials and query are removed.</param>
    public ProviderIdentity Identify(string registrationId, Uri? endpoint = null) =>
        new(ProviderKey, registrationId, Api, endpoint: endpoint);
}

/// <summary>Protocol presets for the providers with contract-tested adapters.</summary>
public static class Protocols
{
    /// <summary>OpenAI Chat Completions.</summary>
    public static ProviderProtocol OpenAIChatCompletions { get; } =
        new("openai", ApiKind.ChatCompletions, [new OpenAiChatAdapter()]);

    /// <summary>Azure OpenAI Chat Completions. Pricing also needs the host's deployment-to-model binding.</summary>
    public static ProviderProtocol AzureOpenAIChatCompletions { get; } =
        new("azureopenai", ApiKind.ChatCompletions, [new OpenAiChatAdapter("azureopenai")]);

    /// <summary>Anthropic Messages.</summary>
    public static ProviderProtocol AnthropicMessages { get; } =
        new("anthropic", ApiKind.Messages, [new AnthropicMessagesAdapter()]);

    /// <summary>DeepSeek Chat Completions.</summary>
    public static ProviderProtocol DeepSeekChatCompletions { get; } =
        new("deepseek", ApiKind.ChatCompletions, [new DeepSeekChatAdapter()]);
}
