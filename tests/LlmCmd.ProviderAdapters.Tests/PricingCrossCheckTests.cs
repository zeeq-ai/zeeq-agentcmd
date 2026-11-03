using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.LlmCmd.Providers;
using Zeeq.LlmCmd.Providers.Pricing;

namespace LlmCmd.ProviderAdapters.Tests;

public class PricingCrossCheckTests
{
    static UsageMeasurement M(string d, long n) =>
        new(d, n, Unit.Tokens, Relation.Total, null, MeasurementStatus.Agreed, []);

    static ResponseSnapshot Snapshot =>
        new(
            "a",
            new("azureopenai", "test", ApiKind.ChatCompletions),
            "test",
            null,
            null,
            "test",
            false,
            AttemptEnd.Completed,
            null,
            "gpt-6-luna",
            null,
            null,
            new([M(Dim.Input, 10), M(Dim.Output, 4), M(Dim.Cached, 0), M(Dim.CacheWrite, 0)], []),
            new(0, 0, 0, 0),
            null,
            []
        );
    static PricingEstimate Estimate =>
        PricingEstimator.Estimate(
            Snapshot,
            new("azure-openai-global-standard", "standard", "global", "short")
        );

    static string Observation(string modelId, string inputCost, string totalCost) =>
        "{\"model\":\"gpt-6-luna\",\"internalModelId\":"
        + modelId
        + ",\"usageDetails\":{\"input\":10,\"output\":4},\"costDetails\":{\"input\":"
        + inputCost
        + ",\"output\":0.000002,\"total\":"
        + totalCost
        + "}}";

    [Test]
    public async Task Matches_inferred_cost_and_rejects_wrong_cost_and_missing_model()
    {
        await Assert
            .That(
                PricingCrossCheck
                    .Compare(
                        Snapshot,
                        Estimate,
                        Fx.Json(Observation("\"builtin\"", "0.000001", "0.000003"))
                    )
                    .Status
            )
            .IsEqualTo(CrossCheckStatus.Match);
        await Assert
            .That(
                PricingCrossCheck
                    .Compare(
                        Snapshot,
                        Estimate,
                        Fx.Json(Observation("\"builtin\"", "0.000004", "0.000006"))
                    )
                    .Status
            )
            .IsEqualTo(CrossCheckStatus.CostMismatch);
        await Assert
            .That(
                PricingCrossCheck
                    .Compare(
                        Snapshot,
                        Estimate,
                        Fx.Json(Observation("\"builtin\"", "0.000004", "0.000003"))
                    )
                    .Status
            )
            .IsEqualTo(CrossCheckStatus.CostMismatch);
        await Assert
            .That(
                PricingCrossCheck
                    .Compare(Snapshot, Estimate, Fx.Json(Observation("null", "0", "0")))
                    .Status
            )
            .IsEqualTo(CrossCheckStatus.MissingModel);
        await Assert
            .That(
                PricingCrossCheck
                    .Compare(
                        Snapshot,
                        Estimate,
                        Fx.Json(
                            """{"model":"gpt-6-luna","internalModelId":"builtin","usageDetails":{"input":10,"output":4},"costDetails":{}}"""
                        )
                    )
                    .Status
            )
            .IsEqualTo(CrossCheckStatus.MissingCost);
    }

    [Test]
    public async Task Cannot_compare_when_cache_partitions_are_not_emitted()
    {
        var s = Snapshot;
        var u = s.Usage;
        s = s with
        {
            Usage = u with
            {
                Measurements = u
                    .Measurements.Select(m => m.Dimension == Dim.Cached ? m with { Value = 1 } : m)
                    .ToArray(),
            },
        };
        await Assert
            .That(
                PricingCrossCheck
                    .Compare(
                        s,
                        Estimate,
                        Fx.Json(Observation("\"builtin\"", "0.000001", "0.000003"))
                    )
                    .Status
            )
            .IsEqualTo(CrossCheckStatus.UnsupportedPartitions);
    }
}
