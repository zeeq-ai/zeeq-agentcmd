using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Zeeq.AgentCmd.Providers;
using Zeeq.AgentCmd.Providers.Pricing;

namespace Zeeq.AgentCmd.OpenTelemetry;

/// <summary>Exports a completed provider attempt as a Langfuse generation without recording content.</summary>
public static class LangfuseGeneration
{
    /// <summary>Activity source name to register when exporting generations.</summary>
    public const string SourceName = "Zeeq.AgentCmd.Langfuse";
    private static readonly ActivitySource Source = new(SourceName);

    /// <summary>Emits a generation while isolating telemetry faults from the model result.</summary>
    /// <remarks>Pass the captured parent and attempt timestamps. Raw content is excluded; only agreed token totals and complete nonnegative USD estimates are recorded.</remarks>
    public static void EmitSafely(
        ResponseSnapshot snapshot,
        ActivityContext parent,
        DateTimeOffset started,
        DateTimeOffset ended,
        string? promptName = null,
        int? promptVersion = null,
        PricingEstimate? cost = null
    )
    {
        try
        {
            Emit(snapshot, parent, started, ended, promptName, promptVersion, cost);
        }
        catch (Exception)
        { /* Telemetry is observational and must not change the provider result. */
        }
    }

    /// <summary>Emits a generation under the captured parent, even if the current activity has changed.</summary>
    /// <remarks>Listener faults can propagate; use <see cref="EmitSafely"/> at an observer boundary. Prompt versions are integers, and partial cost estimates are omitted.</remarks>
    public static void Emit(
        ResponseSnapshot snapshot,
        ActivityContext parent,
        DateTimeOffset started,
        DateTimeOffset ended,
        string? promptName = null,
        int? promptVersion = null,
        PricingEstimate? cost = null
    )
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var activity = Source.StartActivity(
            "generation",
            ActivityKind.Client,
            parent,
            startTime: started
        );
        if (activity is null)
        {
            return;
        }

        activity.SetTag("langfuse.observation.type", "generation");
        activity.SetTag("langfuse.observation.model.name", snapshot.ModelId);
        activity.SetTag("gen_ai.provider.name", snapshot.Registration.ProviderKey);
        activity.SetTag("langfuse.observation.metadata.attempt_id", snapshot.AttemptId);
        if (snapshot.ResponseId is not null)
        {
            activity.SetTag("langfuse.observation.metadata.response_id", snapshot.ResponseId);
        }

        var endCode = snapshot.End switch
        {
            AttemptEnd.Failed => "failed",
            AttemptEnd.Cancelled => "cancelled",
            AttemptEnd.Abandoned => "abandoned",
            _ => null,
        };
        if (endCode is not null)
        {
            activity.SetStatus(ActivityStatusCode.Error, endCode);
            activity.SetTag("error.type", endCode);
        }
        if (promptName is not null)
        {
            activity.SetTag("langfuse.observation.prompt.name", promptName);
        }

        if (promptVersion is not null)
        {
            activity.SetTag("langfuse.observation.prompt.version", promptVersion.Value);
        }
        // Each total is emitted once. Subsets and conflicted values must not be added to it.
        var usage = new Dictionary<string, long>();
        foreach (var dimension in new[] { Dim.Input, Dim.Output, Dim.Total })
        {
            var measurement = snapshot.Usage.Find(dimension);
            if (
                measurement is { Unit: Unit.Tokens, Status: MeasurementStatus.Agreed }
                && !snapshot.Usage.HasConflict(dimension)
            )
            {
                usage[dimension] = measurement.Value;
            }
        }
        if (usage.Count > 0)
        {
            activity.SetTag("langfuse.observation.usage_details", UsageJson(usage));
        }
        // An explicit cost overrides Langfuse's inference. Send only a complete
        // token estimate; omission lets its own catalog price a known model and
        // allows an independent comparison instead of echoing our calculation.
        if (
            cost is { Status: PricingStatus.Complete, TotalUsd: >= 0 }
            && cost.TotalUsd is decimal total
        )
        {
            activity.SetTag("langfuse.observation.cost_details", CostJson(total));
            activity.SetTag("langfuse.observation.metadata.cost_source", "zeeq_pricing_estimate");
        }
        activity.SetEndTime(ended.UtcDateTime);
    }

    // These bounded numeric tags are protocol fields, not arbitrary DTOs. Write
    // them directly so optional telemetry needs no reflection serializer roots.
    private static string UsageJson(IReadOnlyDictionary<string, long> usage)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, count) in usage)
            {
                writer.WriteNumber(name, count);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string CostJson(decimal total)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("total", total);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
