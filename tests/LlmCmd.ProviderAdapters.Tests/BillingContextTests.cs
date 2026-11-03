using System.Text.Json;
using Microsoft.Extensions.AI;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.LlmCmd.Providers;
using Zeeq.LlmCmd.Providers.Platforms;

namespace LlmCmd.ProviderAdapters.Tests;

public class BillingContextTests
{
    private static ResponseSnapshot Extract(string provider, string json)
    {
        using var wire = JsonDocument.Parse(json);
        var extractor = new ProviderExtractor(
            new ProviderAdapterRegistry([
                new OpenAiChatAdapter("azureopenai"),
                new AnthropicMessagesAdapter(),
            ])
        );
        var api = provider == "anthropic" ? ApiKind.Messages : ApiKind.ChatCompletions;
        return extractor
            .Extract(
                new ProviderIdentity(provider, "test", api),
                new ChatResponse(),
                "attempt",
                wire.RootElement
            )
            .Snapshot;
    }

    [Test]
    public async Task Anthropic_preserves_known_billing_categories()
    {
        var snapshot = Extract(
            "anthropic",
            """{"usage":{"input_tokens":1,"output_tokens":1,"service_tier":"standard","inference_geo":"global","speed":"fast"}}"""
        );
        await Assert
            .That(snapshot.BillingContext)
            .IsEqualTo(new ProviderBillingContext("standard", "global", "fast"));
        await Assert
            .That(snapshot.Diagnostics.Any(d => d.Code == "unknown-billing-category"))
            .IsFalse();
    }

    [Test]
    public async Task Unrecognized_values_are_present_but_never_echoed()
    {
        var snapshot = Extract(
            "anthropic",
            """{"usage":{"input_tokens":1,"output_tokens":1,"service_tier":"private-value","inference_geo":17,"speed":null}}"""
        );
        await Assert
            .That(snapshot.BillingContext)
            .IsEqualTo(new ProviderBillingContext("unknown", "unknown", "unknown"));
        await Assert
            .That(snapshot.Diagnostics.Count(d => d.Code == "unknown-billing-category"))
            .IsEqualTo(3);
        await Assert
            .That(
                JsonSerializer
                    .Serialize(snapshot)
                    .Contains("private-value", StringComparison.Ordinal)
            )
            .IsFalse();
    }

    [Test]
    public async Task Azure_top_level_tier_and_absence_are_distinct()
    {
        var snapshot = Extract(
            "azureopenai",
            """{"service_tier":"priority","usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}"""
        );
        await Assert
            .That(snapshot.BillingContext)
            .IsEqualTo(new ProviderBillingContext("priority", null, null));
        var absent = Extract(
            "azureopenai",
            """{"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}"""
        );
        await Assert.That(absent.BillingContext).IsNull();
        var unknown = Extract(
            "azureopenai",
            """{"service_tier":"private-tier","usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}"""
        );
        await Assert.That(unknown.BillingContext?.ServiceTier).IsEqualTo("unknown");
        await Assert
            .That(
                JsonSerializer.Serialize(unknown).Contains("private-tier", StringComparison.Ordinal)
            )
            .IsFalse();
    }
}
