using Microsoft.Extensions.AI;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.LlmCmd.Providers;

namespace LlmCmd.ProviderAdapters.Tests;

public class ModelPolicyTests
{
    [Test]
    public async Task Platform_key_casing_cannot_bypass_azure_binding_requirements()
    {
        foreach (var provider in new[] { "azureopenai", "AzureOpenAI", "AZUREOPENAI" })
        {
            var rejected = false;
            try
            {
                using var policy = new ModelPolicyChatClient(
                    new ScriptedClient(),
                    new(provider, "test", ApiKind.ChatCompletions)
                );
            }
            catch (ArgumentException)
            {
                rejected = true;
            }
            await Assert.That(rejected).IsTrue();
        }
    }

    [Test]
    public async Task Streaming_policy_rejects_before_enumerating_the_inner_transport()
    {
        var enumerated = false;
        var provider = new ScriptedClient
        {
            Stream = _ =>
            {
                enumerated = true;
                return ScriptedClient.Updates([]);
            },
        };
        using var policy = new ModelPolicyChatClient(provider, Fx.OpenAi);
        var rejected = false;
        try
        {
            await foreach (
                var update in policy.GetStreamingResponseAsync(
                    [],
                    new()
                    {
                        ModelId = "gpt-6-astra",
                        Reasoning = new() { Effort = ReasoningEffort.None },
                    }
                )
            )
            {
                _ = update;
            }
        }
        catch (ModelPolicyViolationException)
        {
            rejected = true;
        }
        await Assert.That(rejected).IsTrue();
        await Assert.That(enumerated).IsFalse();
    }

    static ModelRequestProfile Profile(string model, string provider = "azureopenai") =>
        ModelRequestCatalog.Default.Find(
            provider,
            provider == "anthropic" ? ApiKind.Messages : ApiKind.ChatCompletions,
            model
        )!;

    [Test, Arguments("gpt-6-astra"), Arguments("gpt-6.1-sol")]
    public async Task Reasoning_required_models_reject_none_and_all_sampling_values(string model)
    {
        var profile = Profile(model);
        var invalid = profile.Validate(
            new()
            {
                Reasoning = new() { Effort = ReasoningEffort.None },
                Temperature = 1,
                TopP = 1,
                TopK = 1,
            }
        );
        await Assert.That(invalid.Contains("reasoning.effort")).IsTrue();
        await Assert
            .That(
                profile
                    .Validate(
                        new()
                        {
                            Reasoning = new() { Effort = ReasoningEffort.Low },
                            Temperature = 1,
                        }
                    )
                    .Contains("temperature")
            )
            .IsTrue();
        await Assert
            .That(
                profile
                    .Validate(
                        new()
                        {
                            Reasoning = new() { Effort = ReasoningEffort.Low },
                            MaxOutputTokens = 256,
                        }
                    )
                    .Count
            )
            .IsEqualTo(0);
    }

    [
        Test,
        Arguments("gpt-5.6-sol"),
        Arguments("gpt-5.6-terra"),
        Arguments("gpt-6-sol"),
        Arguments("gpt-6-luna")
    ]
    public async Task Sampling_requires_no_reasoning_and_finite_values(string model)
    {
        var profile = Profile(model);
        await Assert
            .That(
                profile
                    .Validate(
                        new()
                        {
                            Reasoning = new() { Effort = ReasoningEffort.None },
                            Temperature = .5f,
                            TopP = .9f,
                        }
                    )
                    .Count
            )
            .IsEqualTo(0);
        await Assert
            .That(profile.Validate(new() { Temperature = .5f }).Contains("temperature"))
            .IsTrue();
        await Assert
            .That(
                profile
                    .Validate(
                        new()
                        {
                            Reasoning = new() { Effort = ReasoningEffort.None },
                            Temperature = float.NaN,
                        }
                    )
                    .Contains("temperature")
            )
            .IsTrue();
        await Assert
            .That(profile.Validate(new() { MaxOutputTokens = 0 }).Contains("max_output_tokens"))
            .IsTrue();
    }

    [Test]
    public async Task Fixed_temperature_and_custom_platform_profiles_need_no_subclass()
    {
        var identity = new ProviderModelIdentity("custom-platform", "fixed-model");
        var profile = new ModelRequestProfile(
            identity,
            ApiKind.ChatCompletions,
            SamplingConstraint.Fixed(1),
            SamplingConstraint.Unsupported,
            ["none"],
            "none"
        );
        await Assert.That(profile.Validate(new() { Temperature = 1 }).Count).IsEqualTo(0);
        await Assert
            .That(profile.Validate(new() { Temperature = 0 }).Contains("temperature"))
            .IsTrue();
        await Assert.That(profile.Validate(new()).Count).IsEqualTo(0);
        await Assert
            .That(
                new ModelRequestCatalog([profile]).Find(
                    "custom-platform",
                    ApiKind.ChatCompletions,
                    "fixed-model"
                )
            )
            .IsEqualTo(profile);
    }

    [Test]
    public async Task Anthropic_profiles_differ_in_supported_thinking_but_omit_sampling()
    {
        foreach (var model in new[] { "claude-haiku-5-5", "claude-sonnet-5-5", "claude-opus-5-5" })
        {
            var profile = Profile(model, "anthropic");
            await Assert
                .That(
                    profile
                        .Validate(
                            new()
                            {
                                Temperature = 1,
                                TopP = 1,
                                TopK = 1,
                            }
                        )
                        .Count
                )
                .IsEqualTo(3);
            await Assert
                .That(
                    profile
                        .Validate(new() { Reasoning = new() { Effort = ReasoningEffort.Low } })
                        .Count
                )
                .IsEqualTo(0);
            await Assert
                .That(
                    profile
                        .Validate(new() { Reasoning = new() { Effort = ReasoningEffort.None } })
                        .Count
                )
                .IsEqualTo(model == "claude-haiku-5-5" ? 0 : 1);
        }
    }

    [Test]
    public async Task Policy_preserves_options_and_rejects_before_capture_or_transport()
    {
        var calls = 0;
        ChatOptions? observed = null;
        var provider = new ScriptedClient
        {
            Respond = (_, options, _) =>
            {
                calls++;
                observed = options;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "OK")));
            },
        };
        var sink = new CaptureSink();
        using var capture = new ProviderExtractionChatClient(
            provider,
            Fx.Extractor(),
            Fx.OpenAi,
            sink
        );
        using var policy = new ModelPolicyChatClient(capture, Fx.OpenAi);
        var options = new ChatOptions
        {
            ModelId = "gpt-6-astra",
            Reasoning = new() { Effort = ReasoningEffort.Low },
            MaxOutputTokens = 256,
        };
        var response = await policy.GetResponseAsync([], options);
        await Assert.That(ReferenceEquals(options, observed)).IsTrue();
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(sink.Results.Count).IsEqualTo(1);
        await Assert.That(sink.Results[0].Snapshot.RequestedModelId).IsEqualTo("gpt-6-astra");
        await Assert.That(options.Temperature).IsNull();
        options.Temperature = 1;
        var rejected = false;
        try
        {
            await policy.GetResponseAsync([], options);
        }
        catch (ModelPolicyViolationException e)
        {
            rejected = e.Violations.Contains("temperature");
        }
        await Assert.That(rejected).IsTrue();
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(sink.Results.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Deployment_binding_is_explicit_frozen_and_unknown_targets_are_rejected()
    {
        var calls = 0;
        var provider = new ScriptedClient
        {
            Respond = (_, _, _) =>
            {
                calls++;
                return Task.FromResult(new ChatResponse());
            },
        };
        var bindings = new List<ModelTargetBinding>
        {
            ModelRequestCatalog.Default.Bind(
                "azureopenai",
                ApiKind.ChatCompletions,
                "production-slot",
                "gpt-6-luna"
            ),
        };
        using var policy = new ModelPolicyChatClient(
            provider,
            new("azureopenai", "test", ApiKind.ChatCompletions),
            modelBindings: bindings
        );
        bindings[0] = ModelRequestCatalog.Default.Bind(
            "azureopenai",
            ApiKind.ChatCompletions,
            "production-slot",
            "gpt-6-astra"
        );
        await policy.GetResponseAsync(
            [],
            new()
            {
                ModelId = "production-slot",
                Reasoning = new() { Effort = ReasoningEffort.None },
            }
        );
        foreach (var target in new[] { "unknown", "gpt-6-luna", "production-slot-extra" })
        {
            var rejected = false;
            try
            {
                await policy.GetResponseAsync([], new() { ModelId = target });
            }
            catch (ModelPolicyViolationException)
            {
                rejected = true;
            }
            await Assert.That(rejected).IsTrue();
        }
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task Exact_versions_and_opt_in_unknown_model_behavior_are_distinct()
    {
        await Assert.That(Profile("gpt-6-luna-2026-09-22").Model.ModelId).IsEqualTo("gpt-6-luna");
        await Assert
            .That(
                ModelRequestCatalog.Default.Find(
                    "openai",
                    ApiKind.ChatCompletions,
                    "gpt-6-luna-2099-01-01"
                )
            )
            .IsNull();
        var calls = 0;
        var provider = new ScriptedClient
        {
            Respond = (_, _, _) =>
            {
                calls++;
                return Task.FromResult(new ChatResponse());
            },
        };
        using var permissive = new ModelPolicyChatClient(
            provider,
            Fx.OpenAi,
            allowUnknownModels: true
        );
        await permissive.GetResponseAsync([], new() { ModelId = "future-model" });
        await Assert.That(calls).IsEqualTo(1);
    }
}
