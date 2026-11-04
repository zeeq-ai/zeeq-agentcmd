using System.Diagnostics;
using System.Text.Json;
using Zeeq.AgentCmd.OpenTelemetry;
using Zeeq.AgentCmd.Providers;
using Zeeq.AgentCmd.Providers.Pricing;

internal enum CrossCheckStatus
{
    Match,
    MissingModel,
    UnsupportedPartitions,
    MissingCost,
    UnverifiedModel,
    UsageMismatch,
    CostMismatch,
    Unpriced,
    Unavailable,
}

internal sealed record CrossCheckResult(
    CrossCheckStatus Status,
    decimal? Expected = null,
    decimal? Actual = null
);

internal static class PricingCrossCheck
{
    // This comparison deliberately sends no cost: Langfuse must select its own definition and calculate it.
    // The normal emitter sends only inclusive input and output; cached partitions cannot be checked by it.
    // Langfuse matches usage keys to model rates at ingestion: https://langfuse.com/docs/observability/features/token-and-cost-tracking
    // A Langfuse-managed definition is checked through its public models API; a matching total is not an invoice.
    internal static CrossCheckResult Compare(
        ResponseSnapshot snapshot,
        PricingEstimate? estimate,
        JsonElement observation
    )
    {
        if (estimate is not { Status: PricingStatus.Complete, TotalUsd: not null })
        {
            return new(CrossCheckStatus.Unpriced);
        }

        var u = snapshot.Usage;
        if (
            new[] { Dim.Cached, Dim.CacheWrite }.Any(d =>
                u.Find(d) is not { Value: 0, Status: MeasurementStatus.Agreed } || u.HasConflict(d)
            )
        )
        {
            return new(CrossCheckStatus.UnsupportedPartitions);
        }

        if (
            !observation.TryGetProperty("model", out var model)
            || model.ValueKind != JsonValueKind.String
            || model.GetString() != snapshot.ModelId
        )
        {
            return new(CrossCheckStatus.UsageMismatch);
        }

        if (
            !observation.TryGetProperty("usageDetails", out var usage)
            || usage.ValueKind != JsonValueKind.Object
            || new[] { Dim.Input, Dim.Output }.Any(d =>
                u.Find(d) is not { Status: MeasurementStatus.Agreed, Unit: Unit.Tokens } m
                || u.HasConflict(d)
                || !usage.TryGetProperty(d, out var v)
                || (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var n))
                || v.GetInt64() != m.Value
            )
        )
        {
            return new(CrossCheckStatus.UsageMismatch);
        }

        if (
            !observation.TryGetProperty("internalModelId", out var id)
            || id.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(id.GetString())
        )
        {
            return new(CrossCheckStatus.MissingModel);
        }

        if (
            !observation.TryGetProperty("costDetails", out var cost)
            || cost.ValueKind != JsonValueKind.Object
            || !cost.TryGetProperty("input", out var input)
            || (input.ValueKind != JsonValueKind.Number || !input.TryGetDecimal(out _))
            || !cost.TryGetProperty("output", out var output)
            || (output.ValueKind != JsonValueKind.Number || !output.TryGetDecimal(out _))
            || !cost.TryGetProperty("total", out var total)
            || (total.ValueKind != JsonValueKind.Number || !total.TryGetDecimal(out _))
        )
        {
            return new(CrossCheckStatus.MissingCost);
        }

        var actual = total.GetDecimal();
        return new(
            actual == estimate.TotalUsd.Value && input.GetDecimal() + output.GetDecimal() == actual
                ? CrossCheckStatus.Match
                : CrossCheckStatus.CostMismatch,
            estimate.TotalUsd,
            actual
        );
    }

    // Requires a loopback-authenticated client with BaseAddress and a live OTLP listener.
    internal static async Task<int> RunAsync(
        HttpClient client,
        ResponseSnapshot snapshot,
        PricingEstimate? estimate,
        ActivityContext parent,
        DateTimeOffset start,
        DateTimeOffset end
    )
    {
        LangfuseGeneration.Emit(snapshot, parent, start, end, cost: null);
        for (var i = 0; i < 60; i++)
        {
            using var response = await client.GetAsync(
                "/api/public/v2/observations?fields=model,usage&traceId=" + parent.TraceId
            );
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (
                    doc.RootElement.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Array
                )
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        if (
                            item.TryGetProperty("type", out var type)
                            && type.GetString()
                                ?.Equals("GENERATION", StringComparison.OrdinalIgnoreCase) == true
                            && item.TryGetProperty("traceId", out var trace)
                            && trace.GetString() == parent.TraceId.ToString()
                        )
                        {
                            var result = Compare(snapshot, estimate, item);
                            if (
                                result.Status
                                is CrossCheckStatus.Match
                                    or CrossCheckStatus.CostMismatch
                            )
                            {
                                var id = item.GetProperty("internalModelId").GetString()!;
                                using var modelResponse = await client.GetAsync(
                                    "/api/public/models/" + Uri.EscapeDataString(id)
                                );
                                if (!modelResponse.IsSuccessStatusCode)
                                {
                                    result = new(
                                        CrossCheckStatus.UnverifiedModel,
                                        result.Expected,
                                        result.Actual
                                    );
                                }
                                else
                                {
                                    using var definition = JsonDocument.Parse(
                                        await modelResponse.Content.ReadAsStringAsync()
                                    );
                                    if (
                                        !definition.RootElement.TryGetProperty(
                                            "isLangfuseManaged",
                                            out var managed
                                        )
                                        || managed.ValueKind != JsonValueKind.True
                                    )
                                    {
                                        result = new(
                                            CrossCheckStatus.UnverifiedModel,
                                            result.Expected,
                                            result.Actual
                                        );
                                    }
                                }
                            }
                            Console.WriteLine(
                                $"Independent Langfuse token comparison: {result.Status}; expected USD {result.Expected?.ToString() ?? "unavailable"}; Langfuse USD {result.Actual?.ToString() ?? "unavailable"}."
                            );
                            return result.Status == CrossCheckStatus.Match ? 0 : 4;
                        }
                    }
                }
            }
            await Task.Delay(2000);
        }
        Console.Error.WriteLine(
            "Langfuse independent comparison: observation unavailable within timeout."
        );
        return 3;
    }
}
