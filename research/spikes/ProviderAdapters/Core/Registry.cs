namespace ProviderAdapters;

public closed record AdapterResolution
{
    public sealed record Matched(IProviderResponseAdapter Adapter) : AdapterResolution;
    /// <summary>No adapter registered for this provider and API. Normalized fields still work.</summary>
    public sealed record NormalizedOnly(string Reason) : AdapterResolution;
    /// <summary>Equal-precedence matches are a configuration result. There is no first-match behavior.</summary>
    public sealed record Ambiguous(IReadOnlyList<string> AdapterIds) : AdapterResolution;
}

/// <summary>
/// Selects an adapter from explicit registration: provider key plus API kind. A provider-specific rule beats a protocol-generic rule.
/// The model name, the response type and the SDK type are never consulted.
/// </summary>
public sealed class ProviderAdapterRegistry
{
    readonly IReadOnlyList<IProviderResponseAdapter> adapters;
    readonly IReadOnlyList<INativeResponseProjector> projectors;
    public ProviderAdapterRegistry(IEnumerable<IProviderResponseAdapter> adapters, IEnumerable<INativeResponseProjector>? projectors = null)
    { this.adapters = [.. adapters]; this.projectors = [.. projectors ?? []]; }

    public AdapterResolution Resolve(ProviderRegistration registration)
    {
        var candidates = adapters.Where(a => a.Api == registration.Api).ToArray();
        var specific = candidates.Where(a => a.ProviderKey is not null && string.Equals(a.ProviderKey, registration.ProviderKey, StringComparison.OrdinalIgnoreCase)).ToArray();
        var tier = specific.Length > 0 ? specific : candidates.Where(a => a.ProviderKey is null).ToArray();
        return tier.Length switch
        {
            0 => new AdapterResolution.NormalizedOnly($"No adapter is registered for provider '{registration.ProviderKey}' with API {registration.Api}"),
            1 => new AdapterResolution.Matched(tier[0]),
            _ => new AdapterResolution.Ambiguous([.. tier.Select(a => a.Id)]),
        };
    }

    internal INativeResponseProjector? FindProjector(ApiKind api, object native) => projectors.FirstOrDefault(p => p.Api == api && p.CanProject(native));
}
