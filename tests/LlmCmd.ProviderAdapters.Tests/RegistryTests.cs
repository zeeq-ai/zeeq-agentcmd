using Microsoft.Extensions.AI;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.LlmCmd.Providers;
using Zeeq.LlmCmd.Providers.Platforms;

namespace LlmCmd.ProviderAdapters.Tests;

public class RegistryTests
{
    static string Resolved(ProviderAdapterRegistry r, ProviderIdentity p) =>
        r.Resolve(p) switch
        {
            AdapterResolution.Matched m => m.Adapter.Id,
            AdapterResolution.NormalizedOnly => "normalized-only",
            AdapterResolution.Ambiguous a => "ambiguous:"
                + string.Join(",", a.AdapterIds.Order(StringComparer.Ordinal)),
        };

    [Test]
    public async Task Provider_specific_rule_beats_protocol_generic_rule()
    {
        var registry = new ProviderAdapterRegistry([
            new OpenAiChatAdapter(null),
            new DeepSeekChatAdapter(),
        ]);
        await Assert.That(Resolved(registry, Fx.DeepSeek)).IsEqualTo("deepseek.chat-completions");
        await Assert
            .That(Resolved(registry, new("proxy-x", "proxy-1", ApiKind.ChatCompletions)))
            .IsEqualTo("openai.chat-completions");
    }

    [Test]
    public async Task Model_name_never_selects_the_adapter()
    {
        var registry = new ProviderAdapterRegistry([
            new OpenAiChatAdapter(null),
            new DeepSeekChatAdapter(),
        ]);
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "x"))
        {
            ModelId = "deepseek-chat",
            Usage = new()
            {
                InputTokenCount = 100,
                OutputTokenCount = 40,
                TotalTokenCount = 140,
            },
        };
        var wire = Fx.Json(
            """{"usage":{"prompt_tokens":100,"completion_tokens":40,"total_tokens":140,"prompt_cache_hit_tokens":60}}"""
        );
        var r = new ProviderExtractor(registry).Extract(
            new("proxy-x", "proxy-1", ApiKind.ChatCompletions),
            response,
            "a",
            wire
        );
        await Assert.That(r.Snapshot.AdapterId).IsEqualTo("openai.chat-completions");
        await Assert.That(r.Snapshot.Usage.Find(Dim.Cached)).IsNull();
        await Assert
            .That(r.Snapshot.Diagnostics.Single(d => d.Code == "unknown-field").Path)
            .IsEqualTo("$.usage.prompt_cache_hit_tokens");
    }

    [Test]
    public async Task An_openai_adapter_does_not_serve_another_provider_unless_registered_for_it()
    {
        var registry = Fx.Registry();
        await Assert
            .That(Resolved(registry, new("azure-openai", "az-1", ApiKind.ChatCompletions)))
            .IsEqualTo("normalized-only");
        var withAzure = new ProviderAdapterRegistry([new OpenAiChatAdapter("azure-openai")]);
        await Assert
            .That(Resolved(withAzure, new("azure-openai", "az-1", ApiKind.ChatCompletions)))
            .IsEqualTo("openai.chat-completions:azure-openai");
    }

    [Test]
    public async Task Api_kind_must_match_so_responses_does_not_reuse_the_chat_adapter()
    {
        await Assert
            .That(Resolved(Fx.Registry(), new("openai", "openai-resp", ApiKind.Responses)))
            .IsEqualTo("normalized-only");
        await Assert
            .That(Resolved(Fx.Registry(), new("deepseek", "ds-resp", ApiKind.Messages)))
            .IsEqualTo("normalized-only");
    }

    [Test]
    public async Task Equal_precedence_matches_are_ambiguous_and_fall_back_to_normalized_fields()
    {
        var registry = new ProviderAdapterRegistry([
            new OpenAiChatAdapter("openai"),
            new OpenAiChatAdapter("OPENAI"),
        ]);
        await Assert
            .That(Resolved(registry, Fx.OpenAi))
            .IsEqualTo("ambiguous:openai.chat-completions:OPENAI,openai.chat-completions:openai");
        var (response, _, _) = Fx.Load("openai-chat-basic");
        var r = new ProviderExtractor(registry).Extract(Fx.OpenAi, response, "a");
        await Assert.That(Fx.Status(r)).IsEqualTo("Partial");
        await Assert.That(Fx.Codes(r).Contains("ambiguous-adapter")).IsTrue();
        await Assert.That(r.Snapshot.AdapterId).IsEqualTo("normalized-only");
        await Assert.That(r.Snapshot.Usage.Input).IsEqualTo(100L);
    }

    [Test]
    public async Task Registration_validates_identity_and_strips_credentials_from_the_endpoint()
    {
        var reg = new ProviderIdentity(
            "deepseek",
            "ds-1",
            ApiKind.ChatCompletions,
            "2026-11-01",
            new Uri("https://user:secret@api.example.com:8443/v1/chat?key=abc#frag")
        );
        await Assert.That(reg.EndpointIdentity).IsEqualTo("https://api.example.com:8443/v1/chat");
        await Assert.That(reg.ProtocolRevision).IsEqualTo("2026-11-01");
        await Assert
            .That(() => new ProviderIdentity(" ", "x", ApiKind.Messages))
            .Throws<ArgumentException>();
        await Assert
            .That(() => new ProviderIdentity("p", "", ApiKind.Messages))
            .Throws<ArgumentException>();
    }
}
