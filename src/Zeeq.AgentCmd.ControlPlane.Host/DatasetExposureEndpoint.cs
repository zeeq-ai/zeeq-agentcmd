using System.Text.Json;
using Zeeq.AgentCmd.ControlPlane;

namespace Zeeq.AgentCmd.ControlPlane.Host;

internal sealed record RevealCaseDefinition(string Reason);

/// <summary>Reveal is an audited authority transition, separate from reading protected dataset metadata.</summary>
internal sealed class DatasetExposureEndpoint : IEndpoint
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapGet(
                "/datasets/{id}/exposure",
                (
                    string id,
                    HttpContext context,
                    DatasetExposureHandler handler,
                    CancellationToken ct
                ) => handler.ReadAsync(ApiAccess.Authorize(context, "Read"), id, ct)
            )
            .WithName("GetDatasetExposure")
            .Produces<DatasetExposure>();
        endpoints
            .MapPost(
                "/datasets/{id}/cases/{caseId}/reveal",
                (
                    string id,
                    string caseId,
                    RevealCaseDefinition request,
                    HttpContext context,
                    DatasetExposureHandler handler,
                    CancellationToken ct
                ) =>
                    handler.RevealAsync(
                        ApiAccess.Authorize(context, "Data.Curate"),
                        id,
                        caseId,
                        request,
                        ct
                    )
            )
            .WithName("RevealDatasetCase")
            .Produces<JsonElement>();
    }
}

/// <summary>Trusted hosts share these transitions when case contents are used outside this console.</summary>
internal sealed class DatasetExposureHandler(RelationalControlPlaneStore store) : IEndpointHandler
{
    internal async Task<IResult> ReadAsync(
        RequestAuthority authority,
        string id,
        CancellationToken ct
    )
    {
        using var activity = ControlPlaneTelemetry.Start("datasets.exposure", authority);
        return Results.Ok(await store.GetDatasetExposureAsync(authority.Scope, id, ct));
    }

    internal async Task<IResult> RevealAsync(
        RequestAuthority authority,
        string id,
        string caseId,
        RevealCaseDefinition request,
        CancellationToken ct
    )
    {
        using var activity = ControlPlaneTelemetry.Start("datasets.reveal", authority);
        return Results.Ok(
            await store.RevealDatasetCaseAsync(
                authority.Scope,
                authority.Actor,
                id,
                caseId,
                request.Reason,
                ct
            )
        );
    }
}
