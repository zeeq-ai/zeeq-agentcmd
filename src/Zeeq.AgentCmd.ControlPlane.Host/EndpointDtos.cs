using System.Text.Json;
using Microsoft.OpenApi;

namespace Zeeq.AgentCmd.ControlPlane.Host;

internal sealed record ScopeDefinition(string Application, string Environment, string Partition);

internal sealed record IdentityDefinition(string Subject, string[] Permissions);

internal sealed record AuthenticationDefinition(string Mode);

internal sealed record StorageDefinition(
    string Provider,
    string Topology,
    string Retention = "RetainAll",
    int SchemaVersion = 1
);

internal sealed record CapabilityDefinition(
    bool ManifestRegistration,
    bool TypedWorker,
    string[] PublicationTargets,
    bool TraceQuery
);

internal sealed record WorkspaceDefinition(
    ScopeDefinition Scope,
    IdentityDefinition Identity,
    AuthenticationDefinition Authentication,
    StorageDefinition Storage,
    CapabilityDefinition Capabilities,
    IReadOnlyList<Zeeq.AgentCmd.ControlPlane.ManagementResource> ActiveWorkers
);

internal sealed record HealthDefinition(string Status);

// Explicit wire DTOs keep OpenAPI/Kubb aware of feature bodies while storage keeps detached immutable JSON.
internal sealed record SaveDefinition<TBody>(string Name, TBody Body, string? Id = null);

internal sealed record ResourceDefinition<TBody>(
    string Id,
    string Kind,
    long Revision,
    string Name,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ContentHash,
    TBody Body
);

internal sealed record DatasetDefinition(
    string Purpose,
    DatasetCaseDefinition[] Cases,
    bool? ExposureApproved = null
);

internal sealed record DatasetCaseDefinition(
    string Id,
    string SourceFamilyId,
    JsonElement Input,
    JsonElement[] Assets,
    string[] Tags,
    ReferenceDefinition? Reference = null,
    bool Exposed = false
);

internal sealed record ReferenceDefinition(JsonElement Value, string Completeness);

internal sealed record BenchmarkDefinition(
    string DatasetId,
    CriterionDefinition[] Criteria,
    string PrimaryCriterionId,
    int Repeats,
    GuardrailDefinition[] Guardrails
);

internal sealed record CriterionDefinition(
    string Id,
    int Version,
    string Name,
    string Scope,
    ScorerDefinition Scorer,
    string? OutputBinding = null,
    string? ReferenceBinding = null,
    string? SourceBinding = null,
    MatchingDefinition? Matching = null,
    Dictionary<string, string>? OutputBindings = null
);

internal sealed record ScorerDefinition(
    string Kind,
    string Version,
    decimal? Tolerance = null,
    string? JudgeConfigId = null
);

internal sealed record MatchingDefinition(
    string Order,
    string Duplicates,
    string Unit,
    bool Paraphrases,
    string? DecompositionConfigId = null
);

internal sealed record GuardrailDefinition(
    string Id,
    string Metric,
    string Operator,
    double Threshold,
    double MinimumCoverage,
    bool Required
);

internal sealed record PublicationDefinition(
    string BuildId,
    PublicationCommandDefinition[] Commands,
    bool RestoreConfiguredDefaults = false
);

internal sealed record PublicationCommandDefinition(
    string CommandId,
    int CommandRevision,
    string ContractSignature,
    string ExperimentId,
    string ControlVariantId,
    VariantDefinition[] Variants
);

internal sealed record VariantDefinition(
    string Id,
    int Weight,
    string UserTemplate,
    Dictionary<string, JsonElement> Parameters,
    string? SystemTemplate = null,
    RouteDefinition? Route = null,
    string? SystemMode = null
);

internal sealed record RouteDefinition(
    ProfileKeyDefinition Key,
    string Provider,
    string Model,
    float? Temperature = null,
    RouteDefaultsDefinition? Defaults = null
);

internal sealed record ProfileKeyDefinition(string Id);

internal sealed record RouteDefaultsDefinition(
    float? Temperature = null,
    int? MaxOutputTokens = null,
    float? TopP = null,
    int? TopK = null,
    string? ReasoningEffort = null,
    string? ReasoningOutput = null
);

/// <summary>Concrete request/response schemas match each feature's wire contract without losing stored serializer-specific JSON.</summary>
internal static class ResourceSchema
{
    internal static RouteHandlerBuilder Write(RouteHandlerBuilder endpoint, string kind)
    {
        var type = kind switch
        {
            "datasets" => typeof(SaveDefinition<DatasetDefinition>),
            "benchmarks" => typeof(SaveDefinition<BenchmarkDefinition>),
            "publications" => typeof(SaveDefinition<PublicationDefinition>),
            _ => null,
        };
        if (type is not null)
        {
            // Preserve the original JSON (including omitted versus null templates) in storage.
            // The feature validator owns semantics; generated clients still need its concrete wire schema.
            endpoint.AddOpenApiOperationTransformer(
                async (operation, context, cancellationToken) =>
                {
                    var content = operation.RequestBody?.Content;
                    if (content is not null && content.ContainsKey("application/json"))
                    {
                        content["application/json"] = new OpenApiMediaType
                        {
                            Schema = await context.GetOrCreateSchemaAsync(
                                type,
                                null,
                                cancellationToken
                            ),
                        };
                    }
                }
            );
        }
        return endpoint;
    }

    internal static RouteHandlerBuilder Response(
        RouteHandlerBuilder endpoint,
        string kind,
        int status = 200
    )
    {
        var type = kind switch
        {
            "datasets" => typeof(ResourceDefinition<DatasetDefinition>),
            "benchmarks" => typeof(ResourceDefinition<BenchmarkDefinition>),
            "publications" => typeof(ResourceDefinition<PublicationDefinition>),
            _ => null,
        };
        if (type is not null)
        {
            endpoint.AddOpenApiOperationTransformer(
                async (operation, context, cancellationToken) =>
                {
                    var code = status.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (
                        operation.Responses?.TryGetValue(code, out var response) == true
                        && response.Content is { } content
                        && content.ContainsKey("application/json")
                    )
                    {
                        content["application/json"] = new OpenApiMediaType
                        {
                            Schema = await context.GetOrCreateSchemaAsync(
                                type,
                                null,
                                cancellationToken
                            ),
                        };
                    }
                }
            );
        }
        return endpoint;
    }
}
