using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd;
using Zeeq.AgentCmd.OpenTelemetry;
using Zeeq.AgentCmd.Providers;
using Zeeq.AgentCmd.Providers.Pricing;

namespace AgentCmd.ProviderAdapters.Tests;

public class LangfuseGenerationSinkTests
{
    static readonly TokenRateCard Card = TokenPricingCatalog.Default.Cards.First(c =>
        c.Model.ProviderKey == "anthropic" && c.ValidFrom is null && c.ValidThrough is null
    );

    static readonly ActivityContext AttemptSpan = new(
        ActivityTraceId.CreateRandom(),
        ActivitySpanId.CreateRandom(),
        ActivityTraceFlags.Recorded
    );

    static AttemptRecord Record(bool correlated = true)
    {
        var snapshot = new ResponseSnapshot(
            "call-1",
            new(Card.Model.ProviderKey, "claude", Card.Api),
            "test",
            null,
            null,
            "test",
            false,
            AttemptEnd.Completed,
            null,
            Card.Model.ModelId,
            null,
            null,
            new(
                [
                    M(Dim.Input, 100),
                    M(Dim.Uncached, 100),
                    M(Dim.Output, 40),
                    M(Dim.Cached, 0),
                    M(Dim.CacheWrite, 0),
                ],
                []
            ),
            new(1, 0, 0, 0),
            null,
            []
        )
        {
            Correlation = correlated
                ? new(
                    new CommandExecutionContext(
                        Guid.NewGuid(),
                        "invoice.extract",
                        3,
                        "hash",
                        AgentCmdContext.Capture()
                    ),
                    Guid.NewGuid(),
                    2,
                    "extract",
                    AttemptSpan
                )
                : null,
        };
        var started = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);

        return new(ExtractionResult.From(snapshot), started, started.AddSeconds(2));
    }

    static UsageMeasurement M(string dimension, long value) =>
        new(dimension, value, Unit.Tokens, Relation.Total, null, MeasurementStatus.Agreed, []);

    static (ActivityListener Listener, List<Activity> Stopped) Listen()
    {
        var stopped = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == LangfuseGeneration.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                lock (stopped)
                {
                    stopped.Add(a);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        return (listener, stopped);
    }

    static Activity Emitted(List<Activity> stopped, string attemptId) =>
        stopped.Single(a =>
            (string?)a.GetTagItem("langfuse.observation.metadata.execution_id") == attemptId
        );

    [Test]
    public async Task Default_sink_parents_on_the_attempt_and_records_the_real_interval()
    {
        var (listener, stopped) = Listen();
        using var _ = listener;
        var record = Record();

        await new LangfuseGenerationSink().OnAttemptAsync(record, default);

        var generation = Emitted(stopped, record.Correlation!.Command.ExecutionId.ToString());
        await Assert.That(generation.ParentSpanId).IsEqualTo(AttemptSpan.SpanId);
        await Assert.That(generation.TraceId).IsEqualTo(AttemptSpan.TraceId);
        await Assert.That(generation.StartTimeUtc).IsEqualTo(record.Started.UtcDateTime);
        await Assert.That(generation.Duration).IsEqualTo(TimeSpan.FromSeconds(2));
        await Assert
            .That(generation.GetTagItem("langfuse.observation.metadata.command_id"))
            .IsEqualTo("invoice.extract");
        await Assert
            .That(generation.GetTagItem("langfuse.observation.metadata.attempt_number"))
            .IsEqualTo(2);

        // Prompt links and cost are opt-in; without them Langfuse keeps a plain generation.
        await Assert.That(generation.GetTagItem("langfuse.observation.prompt.name")).IsNull();
        await Assert.That(generation.GetTagItem("langfuse.observation.cost_details")).IsNull();
    }

    [Test]
    public async Task Options_link_the_command_prompt_and_price_with_the_host_scope()
    {
        var (listener, stopped) = Listen();
        using var _ = listener;
        var record = Record();
        var sink = new LangfuseGenerationSink(
            new() { LinkPrompts = true, PricingScope = _ => Card.Scope },
            TokenPricingCatalog.Default
        );

        await sink.OnAttemptAsync(record, default);

        var generation = Emitted(stopped, record.Correlation!.Command.ExecutionId.ToString());
        await Assert
            .That(generation.GetTagItem("langfuse.observation.prompt.name"))
            .IsEqualTo("invoice.extract");
        await Assert
            .That(generation.GetTagItem("langfuse.observation.prompt.version"))
            .IsEqualTo(3);
        await Assert.That(generation.GetTagItem("langfuse.observation.cost_details")).IsNotNull();
    }

    [Test]
    public async Task A_faulty_scope_leaves_cost_unknown_and_still_emits()
    {
        var (listener, stopped) = Listen();
        using var _ = listener;
        var record = Record();
        var sink = new LangfuseGenerationSink(
            new() { PricingScope = _ => throw new InvalidOperationException("scope down") },
            TokenPricingCatalog.Default
        );

        await sink.OnAttemptAsync(record, default);

        var generation = Emitted(stopped, record.Correlation!.Command.ExecutionId.ToString());
        await Assert.That(generation.GetTagItem("langfuse.observation.cost_details")).IsNull();
    }
}
