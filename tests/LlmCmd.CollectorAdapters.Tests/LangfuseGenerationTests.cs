using System.Diagnostics;
using System.Text;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.LlmCmd.OpenTelemetry;
using Zeeq.LlmCmd.Providers;
using Zeeq.LlmCmd.Providers.Pricing;

namespace LlmCmd.CollectorAdapters.Tests;

public class LangfuseGenerationTests
{
    [Test]
    [Arguments(AttemptEnd.Failed, "failed")]
    [Arguments(AttemptEnd.Cancelled, "cancelled")]
    [Arguments(AttemptEnd.Abandoned, "abandoned")]
    public async Task Failed_attempts_have_bounded_status(AttemptEnd end, string code)
    {
        var captured = new AsyncLocal<Activity?>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == LangfuseGeneration.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = a => captured.Value = a,
        };
        ActivitySource.AddActivityListener(listener);
        var snapshot = new ResponseSnapshot(
            "attempt",
            new("azure", "test", ApiKind.ChatCompletions),
            "test",
            null,
            null,
            "test",
            false,
            end,
            "response-id",
            "model",
            null,
            null,
            UsageSnapshot.Empty,
            new(0, 0, 0, 0),
            null,
            []
        );
        var now = DateTimeOffset.UtcNow;
        LangfuseGeneration.Emit(snapshot, default, now, now);
        await Assert.That(captured.Value?.Status == ActivityStatusCode.Error).IsTrue();
        await Assert.That(captured.Value?.StatusDescription).IsEqualTo(code);
        await Assert
            .That(
                captured.Value?.GetTagItem("langfuse.observation.metadata.response_id")?.ToString()
            )
            .IsEqualTo("response-id");
    }

    [Test]
    public async Task Partial_estimate_is_not_exported_as_total()
    {
        var captured = new AsyncLocal<Activity?>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == LangfuseGeneration.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = a => captured.Value = a,
        };
        ActivitySource.AddActivityListener(listener);
        var snapshot = new ResponseSnapshot(
            "attempt",
            new("azure", "test", ApiKind.ChatCompletions),
            "test",
            null,
            null,
            "test",
            false,
            AttemptEnd.Completed,
            null,
            "model",
            null,
            null,
            UsageSnapshot.Empty,
            new(0, 0, 0, 0),
            null,
            []
        );
        var now = DateTimeOffset.UtcNow;
        LangfuseGeneration.Emit(
            snapshot,
            default,
            now,
            now,
            cost: new(PricingStatus.Partial, null, 0.01m, "partial", null)
        );
        await Assert.That(captured.Value is not null).IsTrue();
        await Assert
            .That(captured.Value?.GetTagItem("langfuse.observation.cost_details") is null)
            .IsTrue();
    }

    [Test]
    public async Task Listener_fault_is_isolated()
    {
        var fault = new AsyncLocal<bool> { Value = true };
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == LangfuseGeneration.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                fault.Value
                    ? throw new InvalidOperationException("synthetic observer fault")
                    : ActivitySamplingResult.None,
        };
        ActivitySource.AddActivityListener(listener);
        var snapshot = new ResponseSnapshot(
            "attempt",
            new("azure", "test", ApiKind.ChatCompletions),
            "test",
            null,
            null,
            "test",
            false,
            AttemptEnd.Completed,
            null,
            "model",
            null,
            null,
            UsageSnapshot.Empty,
            new(0, 0, 0, 0),
            null,
            []
        );
        var now = DateTimeOffset.UtcNow;
        LangfuseGeneration.EmitSafely(snapshot, default, now, now);
        await Assert
            .That(
                Profiles.LangfusePromptAttributes("prompt", 2)[
                    "langfuse.observation.prompt.version"
                ]
                    is int version
                    && version == 2
            )
            .IsTrue();
    }

    [Test]
    public async Task Exporter_receives_generation_without_content_or_overlapping_usage()
    {
        var handler = new CaptureHandler();
        using var provider = Sdk.CreateTracerProviderBuilder()
            .SetSampler(new AlwaysOnSampler())
            .AddLlmCmdInstrumentation()
            .AddOtlpExporter(o =>
            {
                o.Protocol = OtlpExportProtocol.HttpProtobuf;
                o.Endpoint = new Uri("http://localhost:8087/api/public/otel/v1/traces");
                o.HttpClientFactory = () => new HttpClient(handler, false);
                o.ExportProcessorType = ExportProcessorType.Simple;
            })
            .Build();
        var usage = new UsageSnapshot(
            [
                new(Dim.Input, 12, Unit.Tokens, Relation.Total, null, MeasurementStatus.Agreed, []),
                new(
                    Dim.Cached,
                    4,
                    Unit.Tokens,
                    Relation.SubsetOfParent,
                    Dim.Input,
                    MeasurementStatus.Agreed,
                    []
                ),
                new(Dim.Output, 3, Unit.Tokens, Relation.Total, null, MeasurementStatus.Agreed, []),
            ],
            []
        );
        var snapshot = new ResponseSnapshot(
            "attempt",
            new("azure", "test", ApiKind.ChatCompletions),
            "test",
            null,
            null,
            "test",
            false,
            AttemptEnd.Completed,
            null,
            "model",
            null,
            null,
            usage,
            new(1, 0, 0, 0),
            [new("text", "secret-content-marker", false)],
            []
        );
        var parent = new ActivityContext(
            ActivityTraceId.CreateRandom(),
            ActivitySpanId.CreateRandom(),
            ActivityTraceFlags.Recorded
        );
        var now = DateTimeOffset.UtcNow;
        LangfuseGeneration.Emit(snapshot, parent, now.AddSeconds(-1), now, "prompt", 2);
        provider.ForceFlush();
        var request = await handler.Result.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var payload = Encoding.UTF8.GetString(request.Item3);
        await Assert.That(payload.Contains("langfuse.observation.type")).IsTrue();
        await Assert.That(payload.Contains("generation")).IsTrue();
        await Assert.That(payload.Contains("langfuse.observation.usage_details")).IsTrue();
        await Assert.That(payload.Contains("secret-content-marker")).IsFalse();
        await Assert.That(payload.Contains("input.cached")).IsFalse();
    }
}
