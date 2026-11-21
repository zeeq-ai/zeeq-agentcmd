namespace Zeeq.AgentCmd.Providers.Pricing;

/// <summary>
/// Host setup for a rate catalog. Start from the reviewed built-in cards, then add,
/// replace or remove cards for negotiated rates, private deployments or new models.
/// </summary>
/// <remarks>
/// A host override is explicit data, not a heuristic: it replaces only the cards it
/// overlaps, and the finished catalog still rejects ambiguous cards. Provenance on
/// a custom card should name the contract or source that justifies its rates.
/// </remarks>
public sealed class TokenPricingOptions
{
    readonly List<TokenRateCard> cards;

    /// <summary>Starts from the supplied cards; <see cref="TokenPricingCatalog.Create"/> passes the built-ins.</summary>
    internal TokenPricingOptions(IEnumerable<TokenRateCard> baseline)
    {
        cards = [.. baseline];
    }

    /// <summary>Adds a card. The catalog rejects it at build time if it overlaps an existing card.</summary>
    /// <param name="card">Reviewed rates for one exact model, scope and date interval.</param>
    public TokenPricingOptions Add(TokenRateCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        cards.Add(card);
        return this;
    }

    /// <summary>
    /// Adds a card and removes every existing card it overlaps, so a negotiated or corrected rate
    /// wins over the built-in one for the same model, scope, context band and dates.
    /// </summary>
    /// <param name="card">Replacement rates.</param>
    public TokenPricingOptions Override(TokenRateCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        cards.RemoveAll(existing => TokenPricingCatalog.CardsOverlap(existing, card));
        cards.Add(card);
        return this;
    }

    /// <summary>Removes every card for an exact canonical model, so its usage becomes unpriced.</summary>
    /// <param name="providerKey">Platform identity, such as anthropic or azureopenai.</param>
    /// <param name="modelId">Canonical model ID.</param>
    public TokenPricingOptions Remove(string providerKey, string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        cards.RemoveAll(card =>
            string.Equals(card.Model.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase)
            && card.Model.ModelId == modelId
        );
        return this;
    }

    /// <summary>Drops every built-in card so the catalog contains only cards the host adds.</summary>
    public TokenPricingOptions Clear()
    {
        cards.Clear();
        return this;
    }

    internal IReadOnlyList<TokenRateCard> Cards => cards;
}
