using System.Text.Json;
using System.Text.Json.Serialization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.LlmCmd.Providers;
using Zeeq.LlmCmd.Providers.Pricing;

namespace LlmCmd.LiveProviders.Tests;

// Local evidence is intentionally outside the CI contract. Replaying it reads
// accounting metadata only and never loads credentials or contacts a provider.
[Explicit, Category("CaptureReplay")]
public class RecordedModelMatrixTests
{
    // ProviderIdentity accepts a Uri but persists only EndpointIdentity. Re-enter
    // its sanitizing constructor rather than using writable endpoint properties.
    sealed class RegistrationReader : JsonConverter<ProviderIdentity>
    {
        public override ProviderIdentity Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options
        )
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var value = document.RootElement;
            string? Optional(string name) =>
                value.TryGetProperty(name, out var property)
                && property.ValueKind == JsonValueKind.String
                    ? property.GetString()
                    : null;
            var endpoint = Optional("EndpointIdentity");
            return new(
                value.GetProperty("ProviderKey").GetString()!,
                value.GetProperty("ClientRegistrationId").GetString()!,
                (ApiKind)value.GetProperty("Api").GetInt32(),
                Optional("ProtocolRevision"),
                endpoint is null ? null : new Uri(endpoint, UriKind.Absolute)
            );
        }

        public override void Write(
            Utf8JsonWriter writer,
            ProviderIdentity value,
            JsonSerializerOptions options
        ) => throw new NotSupportedException();
    }

    [Test]
    public async Task Stored_matrix_snapshots_reprice_with_the_shared_binding_and_original_request_date()
    {
        var inputs = new[]
        {
            ("azureopenai", "gpt-5.6-sol"),
            ("azureopenai", "gpt-5.6-terra"),
            ("azureopenai", "gpt-6-astra"),
            ("azureopenai", "gpt-6-luna"),
            ("azureopenai", "gpt-6-sol"),
            ("azureopenai", "gpt-6.1-sol"),
            ("anthropic", "claude-haiku-5-5"),
            ("anthropic", "claude-sonnet-5-5"),
        };
        foreach (var (provider, model) in inputs)
        {
            using var document = JsonDocument.Parse(
                await File.ReadAllTextAsync(
                    Path.Combine(
                        EvidencePaths.Directory,
                        provider + "." + model + ".matrix.capture.json"
                    )
                )
            );
            var root = document.RootElement;
            await Assert.That(root.GetProperty("Status").GetString()).IsEqualTo("Passed");
            var snapshot = root.GetProperty("Snapshot")
                .Deserialize<ResponseSnapshot>(
                    new JsonSerializerOptions { Converters = { new RegistrationReader() } }
                )!;
            var binding = ModelRequestCatalog.Default.Bind(
                provider,
                snapshot.Registration.Api,
                root.GetProperty("RequestedTarget").GetString()!,
                root.GetProperty("ExpectedModel").GetString()!
            );
            await Assert.That(snapshot.NativeStopReason is "stop" or "end_turn").IsTrue();
            var scope = new PricingScope(
                provider == "anthropic" ? "anthropic-direct" : "azure-openai-global-standard",
                "standard",
                "global",
                provider == "anthropic" ? null : "short",
                binding,
                DateOnly.FromDateTime(root.GetProperty("StartedAt").GetDateTimeOffset().UtcDateTime)
            );
            var estimate = PricingEstimator.Estimate(snapshot, scope);
            await Assert
                .That(estimate.Status)
                .IsEqualTo(
                    (PricingStatus)root.GetProperty("Pricing").GetProperty("Status").GetInt32()
                );
            var previous = root.GetProperty("Pricing").GetProperty("TotalUsd");
            await Assert
                .That(estimate.TotalUsd)
                .IsEqualTo(
                    previous.ValueKind == JsonValueKind.Null
                        ? null
                        : (decimal?)previous.GetDecimal()
                );
        }
    }
}
