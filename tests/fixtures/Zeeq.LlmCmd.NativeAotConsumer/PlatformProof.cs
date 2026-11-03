using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Zeeq.LlmCmd;
using Zeeq.LlmCmd.OpenTelemetry;
using Zeeq.LlmCmd.Providers;
using Zeeq.LlmCmd.Providers.Platforms;
using Zeeq.LlmCmd.Providers.Pricing;

/// <summary>Exercises optional package paths using synthetic local evidence only.</summary>
internal static class PlatformProof
{
    public static void Run()
    {
        var wire = JsonNode.Parse(
            """{"id":"local","model":"offline","service_tier":"priority","usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}"""
        )!;
        var response = new ChatResponse
        {
            RawRepresentation = wire,
            Usage = new UsageDetails
            {
                InputTokenCount = 3,
                OutputTokenCount = 2,
                TotalTokenCount = 5,
            },
        };
        var extractor = new ProviderExtractor(
            new ProviderAdapterRegistry([new OpenAiChatAdapter("azureopenai")])
        );
        var snapshot = extractor
            .Extract(
                new ProviderIdentity("azureopenai", "local", ApiKind.ChatCompletions),
                response,
                "native-attempt"
            )
            .Snapshot;
        Require(
            snapshot.Usage.Input == 3
                && snapshot.Usage.Output == 2
                && snapshot.BillingContext?.ServiceTier == "priority",
            "Provider JsonNode extraction lost usage."
        );

        Activity? captured = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == LangfuseGeneration.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = activity => captured = activity,
        };
        ActivitySource.AddActivityListener(listener);
        var now = DateTimeOffset.UtcNow;
        LangfuseGeneration.Emit(
            snapshot,
            default,
            now,
            now,
            cost: new(PricingStatus.Complete, 0.12m, 0.12m, "local", null)
        );
        Require(captured is not null, "Langfuse generation was not emitted.");
        using var usage = JsonDocument.Parse(
            (string)captured!.GetTagItem("langfuse.observation.usage_details")!
        );
        using var cost = JsonDocument.Parse(
            (string)captured.GetTagItem("langfuse.observation.cost_details")!
        );
        Require(
            usage.RootElement.GetProperty("input").GetInt64() == 3
                && cost.RootElement.GetProperty("total").GetDecimal() == 0.12m,
            "Numeric telemetry JSON differs."
        );

        var context = OperationContext.ForRun(Guid.NewGuid());
        var adapter = new PropagationAdapter();
        var codec = new TextCarrierCodec();
        var injected = adapter.Inject(context, default, HeaderValues.Empty, codec, true);
        Require(injected is InjectionResult<HeaderValues>.Injected, "Context injection failed.");
        using var payload = JsonDocument.Parse("""{"document":"local"}""");
        var envelope = adapter.EncodeWork(context, Guid.NewGuid(), null, payload.RootElement);
        var parsed = adapter.Extract(
            ((InjectionResult<HeaderValues>.Injected)injected).Carrier,
            codec,
            envelope
        );
        Require(
            parsed is ParseResult.Parsed ok
                && ok.Incoming.Reference == context.Reference
                && ok.Incoming.Work?.Payload.GetProperty("document").GetString() == "local",
            "Propagation round-trip failed."
        );
        Console.WriteLine(
            "Native provider extraction, telemetry generation and propagation passed."
        );
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
