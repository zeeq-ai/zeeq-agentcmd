namespace Zeeq.AgentCmd.Providers.Pricing;

public sealed partial class TokenPricingCatalog
{
    /// <summary>Estimates observed token partitions under an exact card and caller-established scope.</summary>
    public PricingEstimate Estimate(ResponseSnapshot snapshot, PricingScope scope)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(scope);
        var card = Cards.SingleOrDefault(card =>
            string.Equals(
                card.Model.ProviderKey,
                snapshot.Registration.ProviderKey,
                StringComparison.OrdinalIgnoreCase
            )
            && card.Api == snapshot.Registration.Api
            && card.Model.Matches(snapshot.ModelId)
            && card.Scope.Sku == scope.Sku
            && card.Scope.ServiceTier == scope.ServiceTier
            && card.Scope.Geography == scope.Geography
            && (
                card.LongContextRates is not null
                || card.Scope.ContextBand is null
                || card.Scope.ContextBand == scope.ContextBand
            )
            && (
                (card.ValidFrom is null && card.ValidThrough is null)
                || scope.AsOf is not null
                    && (card.ValidFrom is null || scope.AsOf >= card.ValidFrom)
                    && (card.ValidThrough is null || scope.AsOf <= card.ValidThrough)
            )
        );
        if (card is null)
        {
            return None(
                "No reviewed rate card matches this provider, model, API, billing scope, context band and pricing date."
            );
        }

        if (
            scope.TargetBinding is { } binding
            && (
                binding.Model is null
                || binding.Target != snapshot.RequestedModelId
                || !string.Equals(
                    binding.Model.ProviderKey,
                    snapshot.Registration.ProviderKey,
                    StringComparison.OrdinalIgnoreCase
                )
                || !binding.Model.Matches(snapshot.ModelId)
                || !card.Model.Matches(binding.Model.ModelId)
            )
        )
        {
            return None("The reported model conflicts with the host's explicit target mapping.");
        }

        if (
            scope.TargetBinding is null
            && snapshot.RequestedModelId is not null
            && string.Equals(
                snapshot.Registration.ProviderKey,
                "azureopenai",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return None(
                "A captured Azure target requires its explicit host model binding for pricing."
            );
        }

        var billing = snapshot.BillingContext;
        if (
            billing?.ServiceTier is not null
                && billing.ServiceTier != scope.ServiceTier
                && !(card.AcceptDefaultTier && billing.ServiceTier == "default")
            || billing?.InferenceGeo is not null && billing.InferenceGeo != scope.Geography
            || billing?.Speed is not (null or "standard")
        )
        {
            return None(
                "Observed billing metadata conflicts with the supported service, geography or standard speed."
            );
        }

        var assumption =
            billing?.ServiceTier is null || billing.InferenceGeo is null || billing.Speed is null
                ? " Missing billing metadata is supplied by caller assertion."
                : "";
        var input = Read(snapshot.Usage, Dim.Input);
        var rates = card.Rates;
        var band = "single";
        if (card.LongContextRates is not null)
        {
            if (card.LongContextThreshold is long threshold)
            {
                if (input is null)
                {
                    return None(
                        "Inclusive input is missing or conflicted; the rate tier is unknown.",
                        card.Provenance
                    );
                }

                band = input > threshold ? "long" : "short";
            }
            else if (scope.ContextBand is "short" or "long")
            {
                band = scope.ContextBand;
                assumption += " Context band is caller-asserted.";
            }
            else
            {
                return None("The context band must be explicitly established by the caller.");
            }

            rates = band == "long" ? card.LongContextRates : card.Rates;
        }
        else if (card.Scope.ContextBand is not null)
        {
            band = card.Scope.ContextBand;
            assumption += " Context band is caller-asserted.";
        }

        if (scope.TargetBinding is null && snapshot.RequestedModelId is null)
        {
            assumption +=
                " Requested target binding was not captured; model identity comes only from the response.";
        }

        var provenance = card.Provenance with { Id = card.Provenance.Id + ":" + band };
        try
        {
            var output = Read(snapshot.Usage, Dim.Output);
            decimal? known = output is null ? null : output.Value * rates.Output / 1_000_000m;
            var inputCost = PriceInput(snapshot.Usage, card.Accounting, rates, input);
            if (inputCost is not null)
            {
                known = (known ?? 0m) + inputCost;
            }

            bool unsupported =
                snapshot.Usage.Conflicts.Count != 0
                || snapshot.Usage.Measurements.Any(measurement =>
                    (
                        measurement.Relation is Relation.Independent or Relation.Unknown
                        || measurement.Dimension is Dim.InputAudio or Dim.OutputAudio
                        || measurement.Dimension.StartsWith(
                            Dim.AdditionalPrefix,
                            StringComparison.Ordinal
                        )
                    ) && (measurement.Value != 0 || measurement.Status != MeasurementStatus.Agreed)
                )
                || snapshot.Diagnostics.Any(diagnostic =>
                    diagnostic.Severity == Severity.Error
                    || card.Accounting == InputTokenAccounting.UncachedWithTtlWrites
                        && diagnostic.Code == "ttl-split-incomplete"
                );
            if (inputCost is not null && output is not null && !unsupported)
            {
                return new(
                    PricingStatus.Complete,
                    known,
                    known,
                    "Complete token estimate; excludes taxes and contractual adjustments."
                        + assumption,
                    provenance
                );
            }

            return new(
                known is null ? PricingStatus.Unpriced : PricingStatus.Partial,
                null,
                known,
                "Missing, inconsistent or unsupported usage prevents a complete estimate."
                    + assumption,
                provenance
            );
        }
        catch (OverflowException)
        {
            return None("Usage arithmetic overflow.", provenance);
        }
    }

    static decimal? PriceInput(
        UsageSnapshot usage,
        InputTokenAccounting accounting,
        TokenRates rates,
        long? input
    )
    {
        var cached = Read(usage, Dim.Cached);
        var write = Read(usage, Dim.CacheWrite);
        if (
            input is null
            || cached is null
            || write is null
            || checked(cached.Value + write.Value) > input
        )
        {
            return null;
        }

        var plain = input.Value - cached.Value - write.Value;
        if (accounting == InputTokenAccounting.InclusiveWithCacheSubsets)
        {
            return (
                    plain * rates.Input
                    + cached.Value * rates.CachedInput
                    + write.Value * rates.CacheWrite
                ) / 1_000_000m;
        }

        // The protocol reports uncached input separately. Require agreement and
        // a complete TTL split; do not invent residuals from missing counters.
        if (Read(usage, Dim.Uncached) != plain)
        {
            return null;
        }

        var five = Read(usage, Dim.CacheWrite5m);
        var hour = Read(usage, Dim.CacheWrite1h);
        bool validTtl =
            write == 0
                ? !usage.Measurements.Any(m => m.Dimension is Dim.CacheWrite5m or Dim.CacheWrite1h)
                    || five == 0 && hour == 0
                : five is not null && hour is not null && checked(five.Value + hour.Value) == write;
        if (!validTtl || hour > 0 && rates.CacheWrite1h is null)
        {
            return null;
        }

        return (
                plain * rates.Input
                + cached.Value * rates.CachedInput
                + (five ?? 0) * rates.CacheWrite
                + (hour ?? 0) * (rates.CacheWrite1h ?? 0)
            ) / 1_000_000m;
    }

    static long? Read(UsageSnapshot usage, string dimension)
    {
        var values = usage.Measurements.Where(m => m.Dimension == dimension).ToArray();
        return
            values.Length == 1
            && values[0].Status == MeasurementStatus.Agreed
            && values[0].Unit == Unit.Tokens
            && values[0].Value >= 0
            && !usage.HasConflict(dimension)
            ? values[0].Value
            : null;
    }

    static PricingEstimate None(string reason, RateProvenance? provenance = null) =>
        new(PricingStatus.Unpriced, null, null, reason, provenance);
}
