using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using OpenAI.Chat;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.LlmCmd;
using Zeeq.LlmCmd.Generated;
using Zeeq.LlmCmd.Providers;
using Zeeq.LlmCmd.Providers.Platforms;

namespace LlmCmd.LiveProviders.Tests;

/// <summary>Real SDK bridges over an in-memory transport prove which model reaches each API shape.</summary>
[Category("Offline")]
public sealed class NamedClientSdkTests
{
    [Test]
    public async Task Profiles_select_bound_OpenAI_clients_and_share_a_multi_model_Claude_client()
    {
        using var openAiTransport = new OfflineTransport(anthropic: false);
        using var claudeTransport = new OfflineTransport(anthropic: true);
        using var openAiHttp = new HttpClient(openAiTransport);
        using var claudeHttp = new HttpClient(claudeTransport);
        var snapshots = new SnapshotSink();
        var extractor = new ProviderExtractor(
            new ProviderAdapterRegistry(
                [new OpenAiChatAdapter("azureopenai"), new AnthropicMessagesAdapter()],
                [new OpenAiSdkProjector(), new AnthropicSdkProjector()]
            )
        );

        IChatClient Azure(string model, string registration)
        {
            var identity = new ProviderIdentity(
                "azureopenai",
                registration,
                ApiKind.ChatCompletions
            );
            var captured = new ProviderExtractionChatClient(
                new ChatClient(
                    model,
                    new ApiKeyCredential("synthetic-not-a-credential"),
                    new OpenAIClientOptions
                    {
                        Endpoint = new Uri("https://synthetic.example/openai/v1/"),
                        Transport = new HttpClientPipelineTransport(openAiHttp),
                        RetryPolicy = new ClientRetryPolicy(0),
                    }
                ).AsIChatClient(),
                extractor,
                identity,
                snapshots
            );
            return new ModelPolicyChatClient(
                captured,
                identity,
                modelBindings:
                [
                    ModelRequestCatalog.Default.Bind(
                        "azureopenai",
                        ApiKind.ChatCompletions,
                        model,
                        model
                    ),
                ]
            );
        }

        var services = new ServiceCollection();
        services.AddKeyedChatClient("azure.luna", _ => Azure("gpt-6-luna", "azure.luna"));
        services.AddKeyedChatClient("azure.astra", _ => Azure("gpt-6-astra", "azure.astra"));
        services.AddKeyedChatClient(
            "claude",
            _ =>
            {
                var identity = new ProviderIdentity("anthropic", "claude", ApiKind.Messages);
                var captured = new ProviderExtractionChatClient(
                    new AnthropicClient
                    {
                        ApiKey = "synthetic-not-a-credential",
                        HttpClient = claudeHttp,
                        BaseUrl = "https://synthetic.example",
                        MaxRetries = 0,
                    }.AsIChatClient(defaultModelId: null, defaultMaxOutputTokens: 64),
                    extractor,
                    identity,
                    snapshots
                );
                return new ModelPolicyChatClient(
                    captured,
                    identity,
                    modelBindings:
                    [
                        ModelRequestCatalog.Default.Bind(
                            "anthropic",
                            ApiKind.Messages,
                            "claude-haiku-5-5",
                            "claude-haiku-5-5"
                        ),
                        ModelRequestCatalog.Default.Bind(
                            "anthropic",
                            ApiKind.Messages,
                            "claude-sonnet-5-5",
                            "claude-sonnet-5-5"
                        ),
                    ]
                );
            }
        );
        services
            .AddLlmCmd(builder =>
                builder
                    .AddChatClient("azure.luna", fixedModel: "gpt-6-luna")
                    .AddChatClient("azure.astra", fixedModel: "gpt-6-astra")
                    .AddChatClient("claude")
                    .AddProfile(
                        "luna",
                        "azure.luna",
                        "gpt-6-luna",
                        new(MaxOutputTokens: 64, ReasoningEffort: ReasoningEffort.None)
                    )
                    .AddProfile(
                        "astra",
                        "azure.astra",
                        "gpt-6-astra",
                        new(MaxOutputTokens: 512, ReasoningEffort: ReasoningEffort.Low)
                    )
                    .AddProfile(
                        "haiku",
                        "claude",
                        "claude-haiku-5-5",
                        new(MaxOutputTokens: 64, ReasoningEffort: ReasoningEffort.None)
                    )
                    .AddProfile(
                        "sonnet",
                        "claude",
                        "claude-sonnet-5-5",
                        new(MaxOutputTokens: 512, ReasoningEffort: ReasoningEffort.Low)
                    )
                    .UseDefaultProfile("luna")
                    .UseActor(_ => new("offline", "reviewer"))
            )
            .AddGeneratedCommands();
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RuntimeProbe>();
        foreach (var profile in new[] { "luna", "astra", "haiku", "sonnet" })
        {
            var outcome = await command
                .WithParameters(new("local fixture"))
                .WithProfile(profile)
                .ExecuteAsync();
            await Assert
                .That(
                    outcome
                        is Outcome<RuntimeProbeResult>.Success
                        {
                            Value.Answer: "OK",
                            Delivery: OutputDelivery.Stored
                        }
                )
                .IsTrue();
        }

        await Assert
            .That(
                openAiTransport
                    .Requests.Select(request => request.Model)
                    .SequenceEqual(["gpt-6-luna", "gpt-6-astra"])
            )
            .IsTrue();
        await Assert
            .That(
                claudeTransport
                    .Requests.Select(request => request.Model)
                    .SequenceEqual(["claude-haiku-5-5", "claude-sonnet-5-5"])
            )
            .IsTrue();
        await Assert
            .That(
                openAiTransport
                    .Requests.Select(request => request.OutputLimit)
                    .SequenceEqual([64, 512])
            )
            .IsTrue();
        await Assert
            .That(
                claudeTransport
                    .Requests.Select(request => request.OutputLimit)
                    .SequenceEqual([64, 512])
            )
            .IsTrue();
        await Assert
            .That(
                openAiTransport
                    .Requests.Select(request => request.Effort)
                    .SequenceEqual(["none", "low"])
            )
            .IsTrue();
        await Assert
            .That(
                claudeTransport
                    .Requests.Select(request => request.Effort)
                    .SequenceEqual(["disabled", "low"])
            )
            .IsTrue();
        await Assert
            .That(
                openAiTransport
                    .Requests.Concat(claudeTransport.Requests)
                    .All(request => request.HasSchema && request.OmitsSampling)
            )
            .IsTrue();
        await Assert.That(snapshots.Items.Count).IsEqualTo(4);
        await Assert
            .That(
                snapshots
                    .Items.Select(result => result.Snapshot.RequestedModelId)
                    .SequenceEqual([
                        "gpt-6-luna",
                        "gpt-6-astra",
                        "claude-haiku-5-5",
                        "claude-sonnet-5-5",
                    ])
            )
            .IsTrue();
        await Assert
            .That(snapshots.Items.All(result => result.Snapshot.RawContent is null))
            .IsTrue();

        // Named routing must preserve model policy middleware. Astra's low
        // reasoning route rejects sampling before either capture or transport.
        var invalid = await command
            .WithParameters(new("local fixture"))
            .WithProfile("astra")
            .Configure(options => options.Temperature = .5f)
            .ExecuteAsync();
        await Assert.That(invalid is Outcome<RuntimeProbeResult>.Failed).IsTrue();
        await Assert
            .That(openAiTransport.Requests.Count + claudeTransport.Requests.Count)
            .IsEqualTo(4);
        await Assert.That(snapshots.Items.Count).IsEqualTo(4);
    }

    sealed record Request(
        string Model,
        int OutputLimit,
        string? Effort,
        bool HasSchema,
        bool OmitsSampling
    );

    sealed class OfflineTransport(bool anthropic) : HttpMessageHandler
    {
        internal List<Request> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            var model = body["model"]!.GetValue<string>();
            var effort = anthropic
                ? body["output_config"]?["effort"]?.GetValue<string>()
                    ?? body["thinking"]?["type"]?.GetValue<string>()
                : body["reasoning_effort"]?.GetValue<string>();
            var schema = anthropic
                ? body["output_config"]?["format"]?["schema"]
                : body["response_format"]?["json_schema"]?["schema"];
            Requests.Add(
                new(
                    model,
                    body[anthropic ? "max_tokens" : "max_completion_tokens"]!.GetValue<int>(),
                    effort,
                    schema?["properties"]?["Answer"] is not null,
                    body["temperature"] is null && body["top_p"] is null && body["top_k"] is null
                )
            );
            var response = anthropic
                ? new JsonObject
                {
                    ["id"] = "msg_synthetic",
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["model"] = model,
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "text", ["text"] = "{\"Answer\":\"OK\"}" }
                    ),
                    ["stop_reason"] = "end_turn",
                    ["stop_sequence"] = null,
                    ["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 1 },
                }
                : new JsonObject
                {
                    ["id"] = "chatcmpl_synthetic",
                    ["object"] = "chat.completion",
                    ["created"] = 1700000000,
                    ["model"] = model,
                    ["choices"] = new JsonArray(
                        new JsonObject
                        {
                            ["index"] = 0,
                            ["finish_reason"] = "stop",
                            ["message"] = new JsonObject
                            {
                                ["role"] = "assistant",
                                ["content"] = "{\"Answer\":\"OK\"}",
                            },
                        }
                    ),
                    ["usage"] = new JsonObject
                    {
                        ["prompt_tokens"] = 1,
                        ["completion_tokens"] = 1,
                        ["total_tokens"] = 2,
                    },
                };
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    response.ToJsonString(),
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        }
    }

    sealed class SnapshotSink : IExtractionSink
    {
        internal List<ExtractionResult> Items { get; } = [];

        public ValueTask OnExtractedAsync(
            ExtractionResult result,
            CancellationToken cancellationToken
        )
        {
            Items.Add(result);
            return ValueTask.CompletedTask;
        }
    }
}
