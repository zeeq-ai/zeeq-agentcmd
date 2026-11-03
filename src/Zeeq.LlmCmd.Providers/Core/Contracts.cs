using System.Text.Json;

namespace Zeeq.LlmCmd.Providers;

/// <summary>Provider wire protocol selected at registration.</summary>
public enum ApiKind
{
    /// <summary>Chat Completions protocol.</summary>
    ChatCompletions,

    /// <summary>Responses protocol.</summary>
    Responses,

    /// <summary>Messages protocol.</summary>
    Messages,
}

/// <summary>How a provider reports streaming usage frames.</summary>
public enum StreamUsageMode
{
    /// <summary>No supported streaming usage semantics declared.</summary>
    Undeclared,

    /// <summary>Each frame replaces the prior cumulative usage snapshot.</summary>
    LatestSnapshot,
}

/// <summary>Explicit client registration. The registry never infers the provider from a model name or SDK type.</summary>
public sealed record ProviderIdentity
{
    /// <summary>Registers a provider and API explicitly; endpoint credentials and query are removed.</summary>
    public ProviderIdentity(
        string providerKey,
        string clientRegistrationId,
        ApiKind api,
        string? protocolRevision = null,
        Uri? endpoint = null
    )
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            throw new ArgumentException("Provider key is required", nameof(providerKey));
        }

        if (string.IsNullOrWhiteSpace(clientRegistrationId))
        {
            throw new ArgumentException(
                "Registration id is required",
                nameof(clientRegistrationId)
            );
        }

        ProviderKey = providerKey;
        ClientRegistrationId = clientRegistrationId;
        Api = api;
        ProtocolRevision = protocolRevision;
        EndpointIdentity = endpoint is null
            ? null
            : new UriBuilder(endpoint)
            {
                UserName = "",
                Password = "",
                Query = "",
                Fragment = "",
            }
                .Uri
                .AbsoluteUri;
    }

    /// <summary>Provider identifier used for adapter selection.</summary>
    public string ProviderKey { get; }

    /// <summary>Host-defined identifier for the registered client.</summary>
    public string ClientRegistrationId { get; }

    /// <summary>Wire protocol used by the client.</summary>
    public ApiKind Api { get; }

    /// <summary>Optional provider protocol revision.</summary>
    public string? ProtocolRevision { get; }

    /// <summary>Scheme, host, port and path only. Credentials, query and fragment are removed.</summary>
    public string? EndpointIdentity { get; }
}

/// <summary>Maps an additional provider count to its canonical dimension and relationship.</summary>
/// <param name="Dimension">Canonical destination dimension.</param>
/// <param name="Unit">Unit expected for the count.</param>
/// <param name="Relation">Relationship of this count to its parent.</param>
/// <param name="Parent">Parent dimension, or null when independent.</param>
public sealed record AdditionalCountRule(
    string Dimension,
    Unit Unit,
    Relation Relation,
    string? Parent
);

/// <summary>Provider/protocol enrichment. Adapters read wire-shaped JSON only; they never reference a vendor SDK or a sink.</summary>
public interface IProviderResponseAdapter
{
    /// <summary>Stable identifier for this implementation.</summary>
    string Id { get; }

    /// <summary>Version of the adapter implementation.</summary>
    string Version { get; }

    /// <summary>Revision of the extraction rules used for provenance.</summary>
    string RulesRevision { get; }

    /// <summary>Null means a protocol-generic rule that the host explicitly opted into.</summary>
    string? ProviderKey { get; }

    /// <summary>Protocol supported by this implementation.</summary>
    ApiKind Api { get; }

    /// <summary>Semantics of usage frames in a stream.</summary>
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
    /// <summary>Stable identifier for this implementation.</summary>
    string Id { get; }

    /// <summary>Protocol supported by this implementation.</summary>
    ApiKind Api { get; }

    /// <summary>Tests whether the native response can be projected.</summary>
    bool CanProject(object native);

    /// <summary>Projects a supported response to its provider wire shape.</summary>
    JsonElement Project(object native);
}

/// <summary>Request-local wire data and evidence accumulator passed to an adapter.</summary>
public sealed class AdapterContext
{
    readonly List<Diagnostic> diagnostics;

    internal AdapterContext(JsonElement wire, UsageBuilder usage, List<Diagnostic> diagnostics)
    {
        Wire = wire;
        Usage = usage;
        this.diagnostics = diagnostics;
    }

    /// <summary>The wire response body (a JSON object).</summary>
    public JsonElement Wire { get; }

    /// <summary>Accumulator for observations of usage dimensions.</summary>
    public UsageBuilder Usage { get; }

    /// <summary>Provider-specific completion reason, when observed.</summary>
    public string? NativeStopReason { get; set; }

    /// <summary>Validated billing categories captured from the response.</summary>
    public ProviderBillingContext? BillingContext { get; set; }

    /// <summary>Allow only known categories into snapshots. Unknown values never escape the adapter.</summary>
    public string? ReadCategory(
        JsonElement owner,
        string name,
        string path,
        params string[] allowed
    )
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (
            value.ValueKind == JsonValueKind.String
            && value.GetString() is { } category
            && allowed.Contains(category, StringComparer.Ordinal)
        )
        {
            return category;
        }

        Report(
            Severity.Warning,
            "unknown-billing-category",
            path + "." + name,
            "Billing metadata is invalid or unrecognized; pricing cannot assume a standard value"
        );
        return "unknown";
    }

    /// <summary>Records an extraction diagnostic without retaining arbitrary wire values.</summary>
    public void Report(Severity severity, string code, string path, string message) =>
        diagnostics.Add(new(severity, code, path, message));

    /// <summary>Reads a non-negative integer property. Missing or null is unknown. A wrong type, fraction, overflow or negative number is reported and treated as unknown.</summary>
    public long? ReadCount(JsonElement owner, string name, string ownerPath)
    {
        if (
            owner.ValueKind != JsonValueKind.Object
            || !owner.TryGetProperty(name, out var p)
            || p.ValueKind is JsonValueKind.Null
        )
        {
            return null;
        }

        var path = ownerPath + "." + name;
        if (p.ValueKind != JsonValueKind.Number || !p.TryGetInt64(out var v))
        {
            Report(
                Severity.Error,
                "invalid-count",
                path,
                $"Expected an integer count but found {p.ValueKind}"
            );
            return null;
        }
        if (v < 0)
        {
            Report(Severity.Error, "negative-count", path, "Counts must not be negative");
            return null;
        }
        return v;
    }

    /// <summary>Returns a child object, or null when missing/null. A non-object child is reported.</summary>
    public JsonElement? ReadObject(JsonElement owner, string name, string ownerPath)
    {
        if (
            owner.ValueKind != JsonValueKind.Object
            || !owner.TryGetProperty(name, out var p)
            || p.ValueKind is JsonValueKind.Null
        )
        {
            return null;
        }

        if (p.ValueKind != JsonValueKind.Object)
        {
            Report(
                Severity.Error,
                "invalid-shape",
                ownerPath + "." + name,
                $"Expected an object but found {p.ValueKind}"
            );
            return null;
        }
        return p;
    }

    /// <summary>Reports field names that the adapter does not understand. Names and JSON kinds only; values are never retained.</summary>
    public void ReportUnknownFields(JsonElement owner, string ownerPath, params string[] known)
    {
        if (owner.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var p in owner.EnumerateObject())
        {
            if (!known.Contains(p.Name, StringComparer.Ordinal))
            {
                Report(
                    Severity.Warning,
                    "unknown-field",
                    ownerPath + "." + p.Name,
                    $"Unrecognized field of kind {p.Value.ValueKind}; its value is not extracted"
                );
            }
        }
    }
}
