namespace Zeeq.AgentCmd.Providers.Pricing;

// Rates and capability rules have different sources and revision schedules.
// Keep prices as reviewed data, not subclasses that duplicate usage arithmetic.
internal static class BuiltInRateCards
{
    const string Anthropic = "https://platform.claude.com/docs/en/about-claude/pricing";
    const string AzureSix =
        "https://azure.microsoft.com/en-us/blog/gpt-6-astra-sol-and-luna-for-production-agents-in-microsoft-foundry/";
    const string AzureFive =
        "https://azure.microsoft.com/en-us/blog/gpt-5-6-now-available-in-microsoft-foundry/";

    internal static IEnumerable<TokenRateCard> Create()
    {
        yield return Claude(
            "claude-haiku-5-5",
            new(.10m, .50m, .01m, .125m, .20m),
            new(.50m, 2.50m, .05m, .625m, 1m),
            100_000
        );
        // The Anthropic pricing page explicitly gives Sonnet/Opus 5.5 standard
        // rates across the full 1M context window; only Haiku has a 100k tier.
        yield return Claude("claude-sonnet-5-5", new(2m, 10m, .10m, 2.50m, 4m));
        yield return Claude("claude-opus-5-5", new(4m, 20m, .20m, 5m, 8m));
        yield return Azure("gpt-6-luna", new(.10m, .50m, .01m, .125m), new(.20m, .75m, .02m, .25m));
        yield return Azure("gpt-6-sol", new(2m, 10m, .20m, 2.50m), new(4m, 15m, .40m, 5m));
        yield return Azure("gpt-6-astra", new(10m, 50m, 1m, 12.50m), new(20m, 75m, 2m, 25m));

        // Microsoft's announcement publishes only short-context 5.6 rates.
        // Do not derive long rates from direct OpenAI or extrapolate a promotion.
        yield return Azure("gpt-5.6-terra", new(2m, 12m, .20m, 2.50m), null, AzureFive);
        yield return Azure("gpt-5.6-sol", new(4m, 20m, .50m, 6.25m), null, AzureFive) with
        {
            ValidFrom = new DateOnly(2026, 9, 1),
            ValidThrough = new DateOnly(2026, 11, 30),
        };
        // Azure 6.1 has no numeric first-party rate in the retrieved page. A
        // verified request profile does not authorize copying direct API prices.
    }

    static TokenRateCard Claude(
        string model,
        TokenRates rates,
        TokenRates? longRates = null,
        long? threshold = null
    ) =>
        new(
            KnownProviderModels.Get("anthropic", model),
            ApiKind.Messages,
            new("anthropic-direct", "standard", "global"),
            rates,
            new(
                "anthropic-" + model + "-standard-global-2026-11-02",
                "USD",
                "per million tokens",
                Anthropic,
                new(2026, 11, 2)
            ),
            InputTokenAccounting.UncachedWithTtlWrites,
            longRates,
            threshold
        );

    static TokenRateCard Azure(
        string model,
        TokenRates rates,
        TokenRates? longRates,
        string source = AzureSix
    ) =>
        new(
            KnownProviderModels.Get("azureopenai", model),
            ApiKind.ChatCompletions,
            new(
                "azure-openai-global-standard",
                "standard",
                "global",
                longRates is null ? "short" : null
            ),
            rates,
            new(
                "azure-" + model + "-global-standard-2026-11-02",
                "USD",
                "per million tokens",
                source,
                new(2026, 11, 2)
            ),
            InputTokenAccounting.InclusiveWithCacheSubsets,
            longRates,
            AcceptDefaultTier: true
        );
}
