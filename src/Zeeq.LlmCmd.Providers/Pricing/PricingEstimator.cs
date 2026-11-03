namespace Zeeq.LlmCmd.Providers.Pricing;

/// <summary>Caller-asserted billing scope. Values must describe the actual request.</summary>
/// <param name="Sku">Exact supported provider SKU.</param>
/// <param name="ServiceTier">Actual service tier, such as standard.</param>
/// <param name="Geography">Actual routing geography, such as global.</param>
/// <param name="ContextBand">Caller-established Azure context band when required; null leaves it unknown.</param>
/// <param name="TargetBinding">The host mapping also used by model policy; a target or reported-model mismatch is unpriced.</param>
/// <param name="AsOf">Request pricing date, required for a time-limited card.</param>
public sealed record PricingScope(
    string Sku,
    string ServiceTier,
    string Geography,
    string? ContextBand = null,
    ModelTargetBinding? TargetBinding = null,
    DateOnly? AsOf = null
);

/// <summary>Identity and source of a static first-party token rate card.</summary>
/// <param name="Id">Rate-card identity, including its scope and retrieval revision.</param>
/// <param name="Currency">Currency used by the rates.</param>
/// <param name="Unit">Unit to which the published rates apply.</param>
/// <param name="SourceUrl">First-party source for the published rates.</param>
/// <param name="RetrievedOn">Date the source was reviewed; not an effective date.</param>
public sealed record RateProvenance(
    string Id,
    string Currency,
    string Unit,
    string SourceUrl,
    DateOnly RetrievedOn
);

/// <summary>Completeness of the supported token-charge estimate.</summary>
public enum PricingStatus
{
    /// <summary>All supported token partitions are priced under the asserted scope.</summary>
    Complete,

    /// <summary>Only some observed token charges can be estimated.</summary>
    Partial,

    /// <summary>No supported token charge can be estimated.</summary>
    Unpriced,
}

/// <summary>
/// Estimate of supported token charges under the declared billing scope.
/// Complete covers the known token partitions, not every possible invoice item.
/// Missing scope metadata remains a caller assertion, never a provider guarantee.
/// </summary>
/// <param name="Status">Completeness of the supported token estimate.</param>
/// <param name="TotalUsd">Complete token estimate in USD, or null when incomplete.</param>
/// <param name="KnownUsd">Known token component in USD, or null when none can be priced.</param>
/// <param name="Reason">Explanation of completeness and any caller assumptions.</param>
/// <param name="Rate">Rate-card provenance, if a card was selected.</param>
public sealed record PricingEstimate(
    PricingStatus Status,
    decimal? TotalUsd,
    decimal? KnownUsd,
    string Reason,
    RateProvenance? Rate
);

/// <summary>Estimates token charges using explicitly selected provider-specific rate cards.</summary>
public static class PricingEstimator
{
    /// <summary>Prices observed usage under the declared scope; the result is not a provider invoice.</summary>
    public static PricingEstimate Estimate(ResponseSnapshot snapshot, PricingScope scope)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(scope);
        return TokenPricingCatalog.Default.Estimate(snapshot, scope);
    }
}
