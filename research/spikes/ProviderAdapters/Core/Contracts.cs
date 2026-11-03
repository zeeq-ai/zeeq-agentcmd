using System.Text.Json;

namespace ProviderAdapters;

public enum ApiKind { ChatCompletions, Responses, Messages }
public enum StreamUsageMode { Undeclared, LatestSnapshot }

/// <summary>Explicit client registration. The registry never infers the provider from a model name or SDK type.</summary>
public sealed record ProviderRegistration
{
    public ProviderRegistration(string providerKey, string clientRegistrationId, ApiKind api, string? protocolRevision = null, Uri? endpoint = null)
    {
        if (string.IsNullOrWhiteSpace(providerKey)) throw new ArgumentException("Provider key is required", nameof(providerKey));
        if (string.IsNullOrWhiteSpace(clientRegistrationId)) throw new ArgumentException("Registration id is required", nameof(clientRegistrationId));
        ProviderKey = providerKey; ClientRegistrationId = clientRegistrationId; Api = api; ProtocolRevision = protocolRevision;
        EndpointIdentity = endpoint is null ? null : new UriBuilder(endpoint) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.AbsoluteUri;
    }
    public string ProviderKey { get; }
    public string ClientRegistrationId { get; }
    public ApiKind Api { get; }
    public string? ProtocolRevision { get; }
    /// <summary>Scheme, host, port and path only. Credentials, query and fragment are removed.</summary>
    public string? EndpointIdentity { get; }
}

public sealed record AdditionalCountRule(string Dimension, Unit Unit, Relation Relation, string? Parent);

/// <summary>Provider/protocol enrichment. Adapters read wire-shaped JSON only; they never reference a vendor SDK or a sink.</summary>
public interface IProviderResponseAdapter
{
    string Id { get; }
    string Version { get; }
    string RulesRevision { get; }
    /// <summary>Null means a protocol-generic rule that the host explicitly opted into.</summary>
    string? ProviderKey { get; }
    ApiKind Api { get; }
    StreamUsageMode StreamUsage { get; }
    /// <summary>Maps MEAI <c>UsageDetails.AdditionalCounts</c> keys to canonical dimensions. Unmapped keys are kept as unknown.</summary>
    IReadOnlyDictionary<string, AdditionalCountRule> AdditionalCounts { get; }
    /// <summary>Pure and synchronous. May throw on a defect; the extractor isolates that.</summary>
    void Enrich(AdapterContext context);
}

/// <summary>
/// Optional bridge from a native SDK object to the provider wire JSON of the response body. Lives in an optional package so the core has no SDK dependency.
/// </summary>
public interface INativeResponseProjector
{
    string Id { get; }
    ApiKind Api { get; }
    bool CanProject(object native);
    JsonElement Project(object native);
}

public sealed class AdapterContext
{
    readonly List<Diagnostic> diagnostics;
    internal AdapterContext(JsonElement wire, UsageBuilder usage, List<Diagnostic> diagnostics)
    { Wire = wire; Usage = usage; this.diagnostics = diagnostics; }
    /// <summary>The wire response body (a JSON object).</summary>
    public JsonElement Wire { get; }
    public UsageBuilder Usage { get; }
    public string? NativeStopReason { get; set; }
    public void Report(Severity severity, string code, string path, string message) => diagnostics.Add(new(severity, code, path, message));

    /// <summary>Reads a non-negative integer property. Missing or null is unknown. A wrong type, fraction, overflow or negative number is reported and treated as unknown.</summary>
    public long? ReadCount(JsonElement owner, string name, string ownerPath)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(name, out var p) || p.ValueKind is JsonValueKind.Null) return null;
        var path = ownerPath + "." + name;
        if (p.ValueKind != JsonValueKind.Number || !p.TryGetInt64(out var v)) { Report(Severity.Error, "invalid-count", path, $"Expected an integer count but found {p.ValueKind}"); return null; }
        if (v < 0) { Report(Severity.Error, "negative-count", path, "Counts must not be negative"); return null; }
        return v;
    }
    /// <summary>Returns a child object, or null when missing/null. A non-object child is reported.</summary>
    public JsonElement? ReadObject(JsonElement owner, string name, string ownerPath)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(name, out var p) || p.ValueKind is JsonValueKind.Null) return null;
        if (p.ValueKind != JsonValueKind.Object) { Report(Severity.Error, "invalid-shape", ownerPath + "." + name, $"Expected an object but found {p.ValueKind}"); return null; }
        return p;
    }
    /// <summary>Reports field names that the adapter does not understand. Names and JSON kinds only; values are never retained.</summary>
    public void ReportUnknownFields(JsonElement owner, string ownerPath, params string[] known)
    {
        if (owner.ValueKind != JsonValueKind.Object) return;
        foreach (var p in owner.EnumerateObject())
            if (!known.Contains(p.Name, StringComparer.Ordinal))
                Report(Severity.Warning, "unknown-field", ownerPath + "." + p.Name, $"Unrecognized field of kind {p.Value.ValueKind}; its value is not extracted");
    }
}
