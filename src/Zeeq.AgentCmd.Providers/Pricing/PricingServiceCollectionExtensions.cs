using Microsoft.Extensions.DependencyInjection;

namespace Zeeq.AgentCmd.Providers.Pricing;

/// <summary>Registers the rate catalog a host injects wherever it prices captured usage.</summary>
public static class PricingServiceCollectionExtensions
{
    /// <summary>
    /// Registers one singleton <see cref="TokenPricingCatalog"/>: the reviewed built-in cards plus the
    /// host's changes. Inject the catalog and call <see cref="TokenPricingCatalog.Estimate"/>.
    /// </summary>
    /// <param name="services">Service collection to extend.</param>
    /// <param name="configure">Adds, overrides or removes cards; omit to use the built-in cards unchanged.</param>
    /// <exception cref="InvalidOperationException">A catalog is already registered. Two setups would silently disagree about rates, so configure once.</exception>
    public static IServiceCollection AddAgentCmdPricing(
        this IServiceCollection services,
        Action<TokenPricingOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(TokenPricingCatalog)))
        {
            throw new InvalidOperationException("A pricing catalog is already registered.");
        }

        // Build eagerly so an overlapping or invalid card fails at startup, not on the first estimate.
        var catalog = configure is null
            ? TokenPricingCatalog.Default
            : TokenPricingCatalog.Create(configure);
        services.AddSingleton(catalog);
        return services;
    }
}
