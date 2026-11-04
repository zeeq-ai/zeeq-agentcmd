using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd.Providers;
using Zeeq.AgentCmd.Providers.Pricing;

namespace AgentCmd.ProviderAdapters.Tests;

public class PricingTests
{
    static readonly PricingScope Scope = new("anthropic-direct", "standard", "global");

    static UsageMeasurement M(string d, long v) =>
        new(d, v, Unit.Tokens, Relation.Total, null, MeasurementStatus.Agreed, []);

    static ResponseSnapshot Snapshot(
        long input = 100_000,
        long output = 10,
        long plain = 99_970,
        long cached = 10,
        long write = 20,
        long five = 15,
        long hour = 5
    ) =>
        new(
            "a",
            Fx.Anthropic,
            "anthropic.messages",
            null,
            null,
            "test",
            false,
            AttemptEnd.Completed,
            null,
            "claude-haiku-5-5",
            null,
            null,
            new(
                [
                    M(Dim.Input, input),
                    M(Dim.Output, output),
                    M(Dim.Uncached, plain),
                    M(Dim.Cached, cached),
                    M(Dim.CacheWrite, write),
                    M(Dim.CacheWrite5m, five),
                    M(Dim.CacheWrite1h, hour),
                ],
                []
            ),
            new(0, 0, 0, 0),
            null,
            []
        );

    [Test]
    public async Task Rates_and_threshold_use_inclusive_input_and_exact_decimal()
    {
        var shortResult = PricingEstimator.Estimate(Snapshot(), Scope);
        await Assert.That(shortResult.TotalUsd).IsEqualTo((decimal?)0.010004975m);
        var longResult = PricingEstimator.Estimate(Snapshot(input: 100_001, plain: 99_971), Scope);
        await Assert.That(longResult.TotalUsd).IsEqualTo((decimal?)0.050025375m);
    }

    [Test]
    public async Task Missing_split_and_conflicts_are_not_complete()
    {
        var s = Snapshot();
        var noTtl = s with
        {
            Usage = s.Usage with
            {
                Measurements = s
                    .Usage.Measurements.Where(m => m.Dimension != Dim.CacheWrite1h)
                    .ToArray(),
            },
        };
        await Assert
            .That(PricingEstimator.Estimate(noTtl, Scope).Status)
            .IsEqualTo(PricingStatus.Partial);
        var noInput = s with
        {
            Usage = s.Usage with
            {
                Measurements = s.Usage.Measurements.Where(m => m.Dimension != Dim.Input).ToArray(),
            },
        };
        await Assert
            .That(PricingEstimator.Estimate(noInput, Scope).Status)
            .IsEqualTo(PricingStatus.Unpriced);
        var conflict = s with
        {
            Usage = s.Usage with
            {
                Conflicts = [new(Dim.Cached, ConflictKind.ValueMismatch, "conflict", [])],
            },
        };
        await Assert.That(PricingEstimator.Estimate(conflict, Scope).TotalUsd).IsNull();
    }

    [Test]
    public async Task Unsupported_scope_and_invalid_counts_are_not_priced_as_complete()
    {
        await Assert
            .That(
                PricingEstimator.Estimate(Snapshot(), Scope with { ServiceTier = "batch" }).Status
            )
            .IsEqualTo(PricingStatus.Unpriced);
        await Assert
            .That(PricingEstimator.Estimate(Snapshot() with { ModelId = "other" }, Scope).Status)
            .IsEqualTo(PricingStatus.Unpriced);
        await Assert.That(PricingEstimator.Estimate(Snapshot(plain: -1), Scope).TotalUsd).IsNull();
        await Assert
            .That(
                PricingEstimator.Estimate(Snapshot(plain: long.MaxValue, cached: 1), Scope).TotalUsd
            )
            .IsNull();
    }

    static ResponseSnapshot Azure(
        long input = 100,
        long cached = 20,
        long write = 10,
        long output = 40
    ) =>
        Snapshot() with
        {
            Registration = new ProviderIdentity("azureopenai", "azure", ApiKind.ChatCompletions),
            ModelId = "gpt-6-luna",
            Usage = new(
                [
                    M(Dim.Input, input),
                    M(Dim.Cached, cached),
                    M(Dim.CacheWrite, write),
                    M(Dim.Output, output),
                    M(Dim.Reasoning, 15),
                ],
                []
            ),
        };

    static readonly PricingScope AzureShort = new(
        "azure-openai-global-standard",
        "standard",
        "global",
        "short"
    );

    [Test]
    public async Task Azure_bands_use_published_distinct_rates_and_do_not_add_reasoning()
    {
        var shortEstimate = PricingEstimator.Estimate(Azure(), AzureShort);
        await Assert.That(shortEstimate.TotalUsd).IsEqualTo((decimal?)0.00002845m);
        await Assert.That(shortEstimate.Rate!.Id.Contains("short")).IsTrue();
        var longEstimate = PricingEstimator.Estimate(
            Azure(),
            AzureShort with
            {
                ContextBand = "long",
            }
        );
        await Assert.That(longEstimate.TotalUsd).IsEqualTo((decimal?)0.0000469m);
    }

    [Test]
    public async Task Azure_requires_scope_and_disjoint_known_partitions()
    {
        await Assert
            .That(PricingEstimator.Estimate(Azure(), AzureShort with { ContextBand = null }).Status)
            .IsEqualTo(PricingStatus.Unpriced);
        await Assert
            .That(PricingEstimator.Estimate(Azure(), AzureShort with { Geography = "us" }).Status)
            .IsEqualTo(PricingStatus.Unpriced);
        await Assert
            .That(PricingEstimator.Estimate(Azure(cached: 95, write: 10), AzureShort).TotalUsd)
            .IsNull();
        var s = Azure();
        var missing = s with
        {
            Usage = s.Usage with
            {
                Measurements = s
                    .Usage.Measurements.Where(m => m.Dimension != Dim.CacheWrite)
                    .ToArray(),
            },
        };
        await Assert
            .That(PricingEstimator.Estimate(missing, AzureShort).Status)
            .IsEqualTo(PricingStatus.Partial);
        var conflict = s with
        {
            Usage = s.Usage with
            {
                Conflicts = [new(Dim.Input, ConflictKind.ValueMismatch, "conflict", [])],
            },
        };
        await Assert.That(PricingEstimator.Estimate(conflict, AzureShort).TotalUsd).IsNull();
    }

    [Test]
    public async Task Ttl_sum_mismatch_missing_output_and_other_charges_stay_partial()
    {
        await Assert.That(PricingEstimator.Estimate(Snapshot(hour: 6), Scope).TotalUsd).IsNull();
        var s = Snapshot();
        var noOutput = s with
        {
            Usage = s.Usage with
            {
                Measurements = s.Usage.Measurements.Where(m => m.Dimension != Dim.Output).ToArray(),
            },
        };
        await Assert
            .That(PricingEstimator.Estimate(noOutput, Scope).Status)
            .IsEqualTo(PricingStatus.Partial);
        var tool = new UsageMeasurement(
            Dim.WebSearchRequests,
            1,
            Unit.Requests,
            Relation.Independent,
            null,
            MeasurementStatus.Agreed,
            []
        );
        var withTool = s with
        {
            Usage = s.Usage with { Measurements = s.Usage.Measurements.Append(tool).ToArray() },
        };
        await Assert.That(PricingEstimator.Estimate(withTool, Scope).TotalUsd).IsNull();
    }

    [Test]
    public async Task Zero_writes_require_consistent_ttl_counters()
    {
        var s = Snapshot(plain: 99_990, cached: 10, write: 0, five: 0, hour: 0);
        await Assert
            .That(PricingEstimator.Estimate(s, Scope).Status)
            .IsEqualTo(PricingStatus.Complete);
        await Assert
            .That(
                PricingEstimator
                    .Estimate(
                        Snapshot(plain: 99_990, cached: 10, write: 0, five: 1, hour: 0),
                        Scope
                    )
                    .TotalUsd
            )
            .IsNull();
    }

    [Test]
    public async Task Azure_versioned_live_snapshot_replays_offline()
    {
        var root = Fx.Json(
            File.ReadAllText(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "PricingFixtures",
                    "azure-luna-live-pricing.json"
                )
            )
        );
        var usage = System.Text.Json.JsonSerializer.Deserialize<UsageSnapshot>(
            root.GetProperty("Usage")
        )!;
        var diagnostics = System.Text.Json.JsonSerializer.Deserialize<Diagnostic[]>(
            root.GetProperty("Diagnostics")
        )!;
        var snapshot = Azure() with
        {
            ModelId = root.GetProperty("ModelId").GetString(),
            Usage = usage,
            Diagnostics = diagnostics,
        };
        var estimate = PricingEstimator.Estimate(snapshot, AzureShort);
        await Assert.That(estimate.Status).IsEqualTo(PricingStatus.Complete);
        await Assert.That(estimate.TotalUsd).IsEqualTo((decimal?)0.000003m);
        await Assert.That(diagnostics.Single().Path).IsEqualTo("$.usage.latency_checkpoint");
        await Assert
            .That(
                PricingEstimator
                    .Estimate(snapshot with { ModelId = "gpt-6-luna-2026-09-23" }, AzureShort)
                    .Status
            )
            .IsEqualTo(PricingStatus.Unpriced);
        await Assert
            .That(
                PricingEstimator
                    .Estimate(snapshot with { ModelId = "gpt-6-luna-2026-09-22-extra" }, AzureShort)
                    .Status
            )
            .IsEqualTo(PricingStatus.Unpriced);
    }

    [Test]
    public async Task Anthropic_observed_billing_must_match_scope()
    {
        var s = Snapshot();
        var valid = s with { BillingContext = new("standard", "global", "standard") };
        await Assert
            .That(PricingEstimator.Estimate(valid, Scope).Status)
            .IsEqualTo(PricingStatus.Complete);
        await Assert
            .That(
                PricingEstimator
                    .Estimate(
                        s with
                        {
                            BillingContext = new("priority", "global", "standard"),
                        },
                        Scope
                    )
                    .Status
            )
            .IsEqualTo(PricingStatus.Unpriced);
        await Assert
            .That(
                PricingEstimator
                    .Estimate(s with { BillingContext = new("standard", "us", "standard") }, Scope)
                    .Status
            )
            .IsEqualTo(PricingStatus.Unpriced);
        await Assert
            .That(
                PricingEstimator
                    .Estimate(s with { BillingContext = new("standard", "global", "fast") }, Scope)
                    .Status
            )
            .IsEqualTo(PricingStatus.Unpriced);
        await Assert
            .That(
                PricingEstimator
                    .Estimate(
                        s with
                        {
                            BillingContext = new("standard", "unknown", "standard"),
                        },
                        Scope
                    )
                    .Status
            )
            .IsEqualTo(PricingStatus.Unpriced);
        await Assert
            .That(
                PricingEstimator
                    .Estimate(s with { BillingContext = new("auto", null, null) }, Scope)
                    .Status
            )
            .IsEqualTo(PricingStatus.Unpriced);
        await Assert
            .That(PricingEstimator.Estimate(s, Scope).Reason.Contains("caller assertion"))
            .IsTrue();
    }

    [Test]
    public async Task Azure_observed_billing_must_match_scope()
    {
        var s = Azure();
        await Assert
            .That(
                PricingEstimator
                    .Estimate(s with { BillingContext = new("default", null, null) }, AzureShort)
                    .Status
            )
            .IsEqualTo(PricingStatus.Complete);
        await Assert
            .That(
                PricingEstimator
                    .Estimate(s with { BillingContext = new("priority", null, null) }, AzureShort)
                    .Status
            )
            .IsEqualTo(PricingStatus.Unpriced);
        await Assert
            .That(
                PricingEstimator
                    .Estimate(s with { BillingContext = new("unknown", null, null) }, AzureShort)
                    .Status
            )
            .IsEqualTo(PricingStatus.Unpriced);
    }
}
