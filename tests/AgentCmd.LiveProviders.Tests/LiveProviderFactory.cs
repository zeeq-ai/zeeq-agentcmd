using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Zeeq.AgentCmd.Providers;

namespace AgentCmd.LiveProviders.Tests;

internal static class LiveProviderFactory
{
    internal static IChatClient Create(LiveSettings settings, HttpClient http)
    {
        IChatClient client = settings.Provider switch
        {
            "openai" or "azureopenai" => new ChatClient(
                settings.Model,
                new ApiKeyCredential(settings.ApiKey),
                new OpenAIClientOptions
                {
                    Endpoint =
                        settings.Provider == "azureopenai"
                            ? settings.Endpoint
                                ?? throw new InvalidOperationException(
                                    "Azure endpoint is required."
                                )
                            : null,
                    Transport = new HttpClientPipelineTransport(http),
                    RetryPolicy = new ClientRetryPolicy(0),
                }
            ).AsIChatClient(),
            "anthropic" => new AnthropicClient
            {
                ApiKey = settings.ApiKey,
                HttpClient = http,
                BaseUrl = "https://api.anthropic.com",
                MaxRetries = 0,
                Timeout = TimeSpan.FromSeconds(45),
            }.AsIChatClient(settings.Model, defaultMaxOutputTokens: 64),
            _ => throw new ArgumentException("Unknown provider"),
        };
        var registration = new ProviderIdentity(
            settings.Provider,
            "live-tests." + settings.Provider,
            settings.Provider == "anthropic" ? ApiKind.Messages : ApiKind.ChatCompletions
        );
        var validated = new ModelPolicyChatClient(
            client,
            registration,
            modelBindings: [Binding(settings)]
        );
        return new BoundedChatClient(
            validated,
            settings.Model,
            MinimumEffort(settings),
            OutputBudget(settings)
        );
    }

    internal static ModelTargetBinding Binding(LiveSettings settings) =>
        ModelRequestCatalog.Default.Bind(
            settings.Provider,
            settings.Provider == "anthropic" ? ApiKind.Messages : ApiKind.ChatCompletions,
            settings.Model,
            settings.UnderlyingModel
        );

    internal static ReasoningEffort MinimumEffort(LiveSettings settings)
    {
        var profile =
            ModelRequestCatalog.Default.Find(
                settings.Provider,
                settings.Provider == "anthropic" ? ApiKind.Messages : ApiKind.ChatCompletions,
                settings.UnderlyingModel
            ) ?? throw new InvalidOperationException("No reviewed model request profile exists.");
        return profile.ReasoningEfforts.Contains("none", StringComparer.Ordinal)
            ? ReasoningEffort.None
            : ReasoningEffort.Low;
    }

    internal static int OutputBudget(LiveSettings settings) =>
        MinimumEffort(settings) == ReasoningEffort.None ? 64 : 512;

    private sealed class BoundedChatClient(
        IChatClient inner,
        string target,
        ReasoningEffort effort,
        int outputBudget
    ) : DelegatingChatClient(inner)
    {
        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            // Clone to retain the runtime's schema and model while avoiding mutation.
            var bounded = options?.Clone() ?? new ChatOptions();
            bounded.ModelId ??= target;
            bounded.MaxOutputTokens = Math.Clamp(
                bounded.MaxOutputTokens ?? outputBudget,
                1,
                outputBudget
            );
            // This test-only budget layer supplies defaults. The library policy
            // validates caller values and never silently repairs an invalid one.
            bounded.Reasoning ??= new ReasoningOptions { Effort = effort };
            return base.GetResponseAsync(messages, bounded, cancellationToken);
        }
    }
}
