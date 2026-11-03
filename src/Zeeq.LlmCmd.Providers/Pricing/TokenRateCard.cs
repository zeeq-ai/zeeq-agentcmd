namespace Zeeq.LlmCmd.Providers.Pricing;

/// <summary>How protocol counters partition inclusive input; independent of the selling platform.</summary>
public enum InputTokenAccounting
{
    /// <summary>Inclusive input minus observed cache reads and untimed writes yields uncached input.</summary>
    InclusiveWithCacheSubsets,

    /// <summary>Uncached input, cache reads and TTL-partitioned writes must sum to inclusive input.</summary>
    UncachedWithTtlWrites,
}

/// <summary>USD per million tokens for disjoint billable parts. A zero price never supplies a missing count.</summary>
/// <param name="Input">Uncached input rate.</param>
/// <param name="Output">Inclusive output rate; reasoning is already within output.</param>
/// <param name="CachedInput">Cache-read rate.</param>
/// <param name="CacheWrite">Untimed or five-minute cache-write rate, according to the accounting policy.</param>
/// <param name="CacheWrite1h">One-hour write rate, or null when that partition is unsupported.</param>
public sealed record TokenRates(
    decimal Input,
    decimal Output,
    decimal CachedInput,
    decimal CacheWrite,
    decimal? CacheWrite1h = null
);

/// <summary>
/// Immutable reviewed data for one platform SKU and exact model. Add models by
/// supplying cards; the arithmetic and protocol partition rules do not change.
/// </summary>
/// <param name="Model">Exact platform/model identity and accepted response versions.</param>
/// <param name="Api">Protocol whose counters this card prices.</param>
/// <param name="Scope">Supported SKU, service tier and geography.</param>
/// <param name="Rates">Single-band or short-context rates.</param>
/// <param name="Provenance">Published source and reviewed revision.</param>
/// <param name="Accounting">Protocol partition semantics.</param>
/// <param name="LongContextRates">Long-context rates; null means one band.</param>
/// <param name="LongContextThreshold">Inclusive input above this threshold selects long rates; null with two bands requires caller selection.</param>
/// <param name="ValidFrom">First supported pricing date, if time-limited.</param>
/// <param name="ValidThrough">Last supported pricing date, if time-limited.</param>
/// <param name="AcceptDefaultTier">Whether an observed default tier means the card's standard tier.</param>
public sealed record TokenRateCard(
    ProviderModelIdentity Model,
    ApiKind Api,
    PricingScope Scope,
    TokenRates Rates,
    RateProvenance Provenance,
    InputTokenAccounting Accounting,
    TokenRates? LongContextRates = null,
    long? LongContextThreshold = null,
    DateOnly? ValidFrom = null,
    DateOnly? ValidThrough = null,
    bool AcceptDefaultTier = false
);

/// <summary>
/// Frozen rate cards and shared estimation mechanics. Unknown or overlapping
/// identities never pick a card by registration order or model-name prefix.
/// </summary>
public sealed partial class TokenPricingCatalog
{
    /// <summary>Creates an immutable catalog and rejects invalid or overlapping cards.</summary>
    public TokenPricingCatalog(IEnumerable<TokenRateCard> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        var copy = cards.ToArray();
        var reviewed = new List<TokenRateCard>();
        foreach (var card in copy)
        {
            ArgumentNullException.ThrowIfNull(card);
            ArgumentNullException.ThrowIfNull(card.Model);
            ArgumentNullException.ThrowIfNull(card.Scope);
            ArgumentNullException.ThrowIfNull(card.Provenance);
            ValidateRates(card.Rates);
            if (card.LongContextRates is not null)
            {
                ValidateRates(card.LongContextRates);
            }

            if (
                !Enum.IsDefined(card.Accounting)
                || card.LongContextThreshold is < 0
                || card.LongContextThreshold is not null && card.LongContextRates is null
                || card.ValidFrom > card.ValidThrough
                || card.Provenance.Currency != "USD"
                || card.Provenance.Unit != "per million tokens"
                || string.IsNullOrWhiteSpace(card.Scope.Sku)
                || string.IsNullOrWhiteSpace(card.Scope.ServiceTier)
                || string.IsNullOrWhiteSpace(card.Scope.Geography)
                || string.IsNullOrWhiteSpace(card.Provenance.Id)
            )
            {
                throw new ArgumentException("Invalid rate-card policy.", nameof(cards));
            }

            if (reviewed.Any(prior => Overlaps(prior, card)))
            {
                throw new ArgumentException(
                    "Overlapping exact model, scope, date interval and context band.",
                    nameof(cards)
                );
            }
            reviewed.Add(card);
        }

        Cards = Array.AsReadOnly(copy);
    }

    /// <summary>Reviewed built-in cards. Hosts can construct a separate catalog with their own cards.</summary>
    public static TokenPricingCatalog Default { get; } = new(BuiltInRateCards.Create());

    /// <summary>Frozen card definitions, including their source revisions.</summary>
    public IReadOnlyList<TokenRateCard> Cards { get; }

    static bool Overlaps(TokenRateCard left, TokenRateCard right) =>
        string.Equals(
            left.Model.ProviderKey,
            right.Model.ProviderKey,
            StringComparison.OrdinalIgnoreCase
        )
        && left.Api == right.Api
        && left.Scope.Sku == right.Scope.Sku
        && left.Scope.ServiceTier == right.Scope.ServiceTier
        && left.Scope.Geography == right.Scope.Geography
        && left.Model.ResponseIds.Intersect(right.Model.ResponseIds, StringComparer.Ordinal).Any()
        && (left.ValidFrom ?? DateOnly.MinValue) <= (right.ValidThrough ?? DateOnly.MaxValue)
        && (right.ValidFrom ?? DateOnly.MinValue) <= (left.ValidThrough ?? DateOnly.MaxValue)
        && (
            left.LongContextRates is not null
            || right.LongContextRates is not null
            || left.Scope.ContextBand is null
            || right.Scope.ContextBand is null
            || left.Scope.ContextBand == right.Scope.ContextBand
        );

    static void ValidateRates(TokenRates rates)
    {
        ArgumentNullException.ThrowIfNull(rates);
        if (
            rates.Input < 0
            || rates.Output < 0
            || rates.CachedInput < 0
            || rates.CacheWrite < 0
            || rates.CacheWrite1h < 0
        )
        {
            throw new ArgumentException("Token rates cannot be negative.", nameof(rates));
        }
    }
}
