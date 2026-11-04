using System.Net;
using CommandRoot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd;
using Zeeq.AgentCmd.Generated;

namespace AgentCmd.CommandRoot.Tests;

/// <summary>Exercises the public DI seam without credentials or provider calls.</summary>
public sealed class NamedClientRegistrationTests
{
    [Test]
    public async Task Generated_command_selects_keyed_clients_and_multiple_models_with_host_middleware()
    {
        var azure = new RecordingClient(fixedModel: "invoice-deployment");
        var claude = new RecordingClient();
        var middlewareCalls = 0;
        var actor = new Actor("tenant", "reviewer");
        var services = new ServiceCollection();
        services
            .AddKeyedChatClient("azure", _ => azure)
            .Use(inner => new TrackingClient(inner, () => middlewareCalls++));
        services.AddKeyedChatClient("claude", _ => claude);
        var deep = ProfileKey.Declare("deep");
        services.AddAgentCmd(builder =>
            builder
                .AddChatClient("azure", fixedModel: "invoice-deployment")
                .AddChatClient("claude")
                .AddProfile(
                    "extraction",
                    "azure",
                    "invoice-deployment",
                    new(MaxOutputTokens: 64, ReasoningEffort: ReasoningEffort.None)
                )
                .AddProfile(
                    deep,
                    "claude",
                    "claude-deep",
                    new(MaxOutputTokens: 512, ReasoningEffort: ReasoningEffort.Low)
                )
                .AddProfile(
                    "fast",
                    "claude",
                    "claude-fast",
                    new(MaxOutputTokens: 32, ReasoningEffort: ReasoningEffort.None)
                )
                .UseDefaultProfile("extraction")
                .UseActor(_ => actor)
        );
        services.AddLlmCommands<Zeeq_AgentCmd_TestFixturesCommands>();
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<ExtractInvoice>();

        var first = (Outcome<Invoice>.Success)
            await command.WithParameters(new("document")).ExecuteAsync();
        var second = (Outcome<Invoice>.Success)
            await command.WithParameters(new("document")).WithProfile(deep).ExecuteAsync();
        var third = (Outcome<Invoice>.Success)
            await command.WithParameters(new("document")).WithProfile("fast").ExecuteAsync();

        await Assert.That(azure.Inputs.Single().ModelId).IsEqualTo("invoice-deployment");
        await Assert
            .That(
                claude
                    .Inputs.Select(options => options.ModelId)
                    .SequenceEqual(["claude-deep", "claude-fast"])
            )
            .IsTrue();
        await Assert.That(azure.Inputs.Single().Reasoning?.Effort).IsEqualTo(ReasoningEffort.None);
        await Assert.That(claude.Inputs[0].Reasoning?.Effort).IsEqualTo(ReasoningEffort.Low);
        await Assert.That(claude.Inputs[0].MaxOutputTokens).IsEqualTo(512);
        await Assert.That(claude.Inputs[1].MaxOutputTokens).IsEqualTo(32);
        await Assert
            .That(
                azure
                    .Inputs.Concat(claude.Inputs)
                    .All(options => options.ResponseFormat is ChatResponseFormatJson)
            )
            .IsTrue();
        await Assert.That(middlewareCalls).IsEqualTo(1);
        await Assert.That(first.Snapshot.Routing!.ServedProfile).IsEqualTo("extraction");
        await Assert.That(second.Snapshot.Routing!.Attempts.Single().Provider).IsEqualTo("claude");
        await Assert.That(third.Delivery is OutputDelivery.Stored).IsTrue();

        provider.GetRequiredService<CommandRuntime>().Dispose();
        await Assert.That(azure.Disposals + claude.Disposals).IsEqualTo(0);
        provider.Dispose();
        await Assert.That(azure.Disposals).IsEqualTo(1);
        await Assert.That(claude.Disposals).IsEqualTo(1);
    }

    [Test]
    [Arguments("missing-client")]
    [Arguments("fixed-model")]
    [Arguments("missing-fallback")]
    [Arguments("cycle")]
    [Arguments("defaults")]
    [Arguments("budget")]
    public async Task Invalid_routes_are_rejected_before_services_or_factories_are_used(
        string invalid
    )
    {
        var factories = 0;
        var services = new ServiceCollection();
        await Assert
            .That(() =>
                services.AddAgentCmd(builder =>
                {
                    builder
                        .AddChatClient(
                            "provider",
                            _ =>
                            {
                                factories++;
                                return new RecordingClient();
                            },
                            fixedModel: invalid == "fixed-model" ? "other-model" : null
                        )
                        .AddProfile(
                            "extraction",
                            invalid == "missing-client" ? "missing" : "provider",
                            "model",
                            defaults: invalid == "defaults" ? new(MaxOutputTokens: 0) : null,
                            fallback: invalid is "missing-fallback" or "cycle"
                                ? ProfileKey.Declare("second")
                                : null
                        )
                        .UseActor(_ => new("tenant", "actor"));
                    if (invalid == "cycle")
                    {
                        builder.AddProfile(
                            "second",
                            "provider",
                            "model",
                            fallback: ProfileKey.Declare("extraction")
                        );
                    }
                    if (invalid == "budget")
                    {
                        builder.UseBudget(new(MaxAttempts: 0));
                    }
                })
            )
            .Throws<InvalidOperationException>();
        await Assert.That(factories).IsEqualTo(0);
        await Assert.That(services.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Unknown_authored_profile_is_rejected_before_named_factories_run()
    {
        var factories = 0;
        var services = new ServiceCollection().AddAgentCmd(builder =>
            builder
                .AddChatClient(
                    "provider",
                    _ =>
                    {
                        factories++;
                        return new RecordingClient();
                    }
                )
                .AddProfile("other", "provider", "model")
                .UseActor(_ => new("tenant", "actor"))
        );
        await Assert
            .That(() => services.AddLlmCommands<Zeeq_AgentCmd_TestFixturesCommands>())
            .Throws<InvalidOperationException>();
        await Assert.That(factories).IsEqualTo(0);
    }

    [Test]
    public async Task Two_registration_names_can_borrow_one_keyed_client_without_disposing_it_twice()
    {
        var client = new RecordingClient();
        var services = new ServiceCollection();
        services.AddKeyedChatClient("shared", _ => client);
        services.AddAgentCmd(builder =>
            builder
                .AddChatClient("one", serviceKey: "shared")
                .AddChatClient("two", serviceKey: "shared")
                .AddProfile("extraction", "one", "model-a")
                .AddProfile("other", "two", "model-b")
                .UseDefaultProfile("extraction")
                .UseActor(_ => new("tenant", "actor"))
        );
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<CommandRuntime>().Dispose();
        await Assert.That(client.Disposals).IsEqualTo(0);
        provider.Dispose();
        await Assert.That(client.Disposals).IsEqualTo(1);
    }

    [Test]
    public async Task Owned_aliases_are_disposed_once_and_partial_factory_failure_releases_created_clients()
    {
        var client = new RecordingClient();
        var services = new ServiceCollection().AddAgentCmd(builder =>
            builder
                .AddChatClient("one", _ => client)
                .AddChatClient("two", _ => client)
                .AddProfile("extraction", "one", "model-a")
                .AddProfile("other", "two", "model-b")
                .UseDefaultProfile("extraction")
                .UseActor(_ => new("tenant", "actor"))
        );
        using (var provider = services.BuildServiceProvider())
        {
            provider.GetRequiredService<CommandRuntime>();
        }
        await Assert.That(client.Disposals).IsEqualTo(1);

        var created = new RecordingClient();
        var failed = new ServiceCollection().AddAgentCmd(builder =>
            builder
                .AddChatClient("one", _ => created)
                .AddChatClient("two", _ => throw new InvalidOperationException("synthetic failure"))
                .AddProfile("extraction", "one", "model-a")
                .AddProfile("other", "two", "model-b")
                .UseDefaultProfile("extraction")
                .UseActor(_ => new("tenant", "actor"))
        );
        using var failedProvider = failed.BuildServiceProvider();
        await Assert
            .That(() => failedProvider.GetRequiredService<CommandRuntime>())
            .Throws<InvalidOperationException>();
        await Assert.That(created.Disposals).IsEqualTo(1);
    }

    [Test]
    public async Task Actual_fixed_client_metadata_must_agree_with_its_declared_binding()
    {
        var client = new RecordingClient(fixedModel: "actual-model");
        var services = new ServiceCollection().AddAgentCmd(builder =>
            builder
                .AddChatClient("fixed", _ => client, fixedModel: "declared-model")
                .AddProfile("extraction", "fixed", "declared-model")
                .UseActor(_ => new("tenant", "actor"))
        );
        using var provider = services.BuildServiceProvider();
        await Assert
            .That(() => provider.GetRequiredService<CommandRuntime>())
            .Throws<InvalidOperationException>();
        await Assert.That(client.Inputs.Count).IsEqualTo(0);
        await Assert.That(client.Disposals).IsEqualTo(1);
    }

    [Test]
    public async Task Fallback_uses_its_own_defaults_and_shared_budget()
    {
        var primary = new RecordingClient(fail: true);
        var fallback = new RecordingClient();
        var services = new ServiceCollection().AddAgentCmd(builder =>
            builder
                .AddChatClient("openai", _ => primary)
                .AddChatClient("claude", _ => fallback)
                .AddProfile(
                    "extraction",
                    "openai",
                    "model-a",
                    new(MaxOutputTokens: 64, ReasoningEffort: ReasoningEffort.None),
                    fallback: ProfileKey.Declare("fallback")
                )
                .AddProfile(
                    "fallback",
                    "claude",
                    "model-b",
                    new(MaxOutputTokens: 512, ReasoningEffort: ReasoningEffort.Low)
                )
                .UseBudget(new(MaxAttempts: 2))
                .UseDefaultProfile("extraction")
                .UseActor(_ => new("tenant", "actor"))
        );
        services.AddLlmCommands<Zeeq_AgentCmd_TestFixturesCommands>();
        using var provider = services.BuildServiceProvider();
        var result = (Outcome<Invoice>.Success)
            await provider
                .GetRequiredService<ExtractInvoice>()
                .WithParameters(new("document"))
                .ExecuteAsync();
        await Assert.That(result.Snapshot.Routing!.ServedProfile).IsEqualTo("fallback");
        await Assert.That(primary.Inputs.Single().MaxOutputTokens).IsEqualTo(64);
        await Assert.That(fallback.Inputs.Single().MaxOutputTokens).IsEqualTo(512);
        await Assert
            .That(fallback.Inputs.Single().Reasoning?.Effort)
            .IsEqualTo(ReasoningEffort.Low);
    }

    [Test]
    public async Task Request_overrides_win_and_nested_reasoning_does_not_leak_between_calls()
    {
        var client = new RecordingClient(mutateReasoning: true);
        var key = ProfileKey.Declare("profile");
        var built = ProfileRouter.TryCreate(
            [new(key, "provider", "model", Defaults: new(.2f, 64, ReasoningEffort.Low, .7f, 10))],
            new Dictionary<string, ProviderRegistration> { ["provider"] = new(() => client) }
        );
        using var router = ((RouterBuildResult.Ready)built).Router;
        var reasoning = new ReasoningOptions { Effort = ReasoningEffort.High };
        var request = new ChatOptions
        {
            Temperature = .4f,
            MaxOutputTokens = 128,
            TopP = .8f,
            TopK = 20,
            Reasoning = reasoning,
        };
        await router.For(key).GetResponseAsync([new(ChatRole.User, "document")], request);
        await router.For(key).GetResponseAsync([new(ChatRole.User, "document")]);
        await Assert.That(client.Inputs[0].Temperature).IsEqualTo(.4f);
        await Assert.That(client.Inputs[0].MaxOutputTokens).IsEqualTo(128);
        await Assert.That(client.Inputs[0].TopK).IsEqualTo(20);
        await Assert.That(client.Inputs[1].Reasoning?.Effort).IsEqualTo(ReasoningEffort.Low);
        await Assert.That(client.Inputs[1].MaxOutputTokens).IsEqualTo(64);
        await Assert.That(reasoning.Effort).IsEqualTo(ReasoningEffort.High);
    }

    [Test]
    public async Task Legacy_route_fingerprint_is_unchanged_and_new_defaults_change_identity()
    {
        // Golden value from the old length-prefixed fields. Adding this API
        // must not invalidate in-memory or host-persisted legacy draft keys.
        const string legacy = "8b55966a40c1e0f756861cb05aa69eedd721c8b1e8bd3468b4485a1c59a38062";
        var key = ProfileKey.Declare("legacy");
        var registrations = new Dictionary<string, ProviderRegistration>
        {
            ["provider"] = new(() => new RecordingClient()),
        };
        using var original = (
            (RouterBuildResult.Ready)
                ProfileRouter.TryCreate([new(key, "provider", "model-a", .2f)], registrations)
        ).Router;
        using var empty = (
            (RouterBuildResult.Ready)
                ProfileRouter.TryCreate(
                    [new(key, "provider", "model-a", .2f, Defaults: new())],
                    registrations
                )
        ).Router;
        using var changed = (
            (RouterBuildResult.Ready)
                ProfileRouter.TryCreate(
                    [new(key, "provider", "model-a", .2f, Defaults: new(MaxOutputTokens: 64))],
                    registrations
                )
        ).Router;
        await Assert.That(original.GetRouteFingerprint(key)).IsEqualTo(legacy);
        await Assert.That(empty.GetRouteFingerprint(key)).IsEqualTo(legacy);
        await Assert.That(changed.GetRouteFingerprint(key) == legacy).IsFalse();
    }

    [Test]
    public async Task Legacy_and_named_configuration_cannot_be_accidentally_mixed()
    {
        var services = new ServiceCollection();
        await Assert
            .That(() =>
                services.AddAgentCmd(builder =>
                    builder
                        .UseChatClient(_ => new RecordingClient())
                        .AddChatClient("other", _ => new RecordingClient())
                )
            )
            .Throws<InvalidOperationException>();
        await Assert.That(services.Count).IsEqualTo(0);
    }

    sealed class TrackingClient(IChatClient inner, Action observe) : DelegatingChatClient(inner)
    {
        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            observe();
            return base.GetResponseAsync(messages, options, cancellationToken);
        }
    }

    sealed class RecordingClient(
        string? fixedModel = null,
        bool fail = false,
        bool mutateReasoning = false
    ) : IChatClient
    {
        internal List<ChatOptions> Inputs { get; } = [];
        internal int Disposals { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            var captured = options!.Clone();
            captured.Reasoning = options.Reasoning is { } reasoning
                ? new() { Effort = reasoning.Effort, Output = reasoning.Output }
                : null;
            Inputs.Add(captured);
            if (mutateReasoning && options.Reasoning is not null)
            {
                options.Reasoning.Effort = ReasoningEffort.ExtraHigh;
            }
            return fail
                ? Task.FromException<ChatResponse>(
                    new HttpRequestException(
                        "synthetic unavailable",
                        null,
                        HttpStatusCode.ServiceUnavailable
                    )
                )
                : Task.FromResult(
                    new ChatResponse(
                        new ChatMessage(ChatRole.Assistant, """{"Total":12.5,"Currency":"USD"}""")
                    )
                );
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata) && fixedModel is not null
                ? new ChatClientMetadata("synthetic", defaultModelId: fixedModel)
                : null;

        public void Dispose() => Disposals++;
    }
}
