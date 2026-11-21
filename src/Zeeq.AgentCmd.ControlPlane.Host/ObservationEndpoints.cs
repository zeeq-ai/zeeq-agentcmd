using Zeeq.AgentCmd.ControlPlane;

namespace Zeeq.AgentCmd.ControlPlane.Host;

/// <summary>Authenticated host reports and owned incident curation use the same scoped evidence identities.</summary>
internal sealed class ObservationEndpoints : IEndpoint
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapPost(
                "/executions/observe",
                (
                    ExecutionObservation request,
                    HttpContext context,
                    ObservationHandler handler,
                    CancellationToken ct
                ) =>
                    handler.ObserveAsync(
                        ApiAccess.Authorize(context, "Operations.Manage"),
                        request,
                        ct
                    )
            )
            .WithName("RecordExecutionObservation")
            .Produces<ManagementResource>();
        endpoints
            .MapPost(
                "/executions/{id}/capture-incident",
                (
                    string id,
                    CaptureIncidentRequest request,
                    HttpContext context,
                    ObservationHandler handler,
                    CancellationToken ct
                ) =>
                    handler.CaptureAsync(
                        ApiAccess.Authorize(context, "Data.Curate"),
                        id,
                        request,
                        ct
                    )
            )
            .WithName("CaptureExecutionIncident")
            .Produces<IncidentCapture>();
        endpoints
            .MapPost(
                "/reviews/{id}/accept-incident",
                (
                    string id,
                    HttpContext context,
                    ObservationHandler handler,
                    CancellationToken ct
                ) =>
                {
                    _ = ApiAccess.Authorize(context, "Data.Curate");
                    return handler.AcceptAsync(
                        ApiAccess.Authorize(context, "Evaluation.Review"),
                        id,
                        ApiAccess.Revision(context),
                        context,
                        ct
                    );
                }
            )
            .WithName("AcceptIncidentReview")
            .Produces<ManagementResource>();
        endpoints
            .MapPost(
                "/reviews/{id}/release",
                (
                    string id,
                    HttpContext context,
                    ObservationHandler handler,
                    CancellationToken ct
                ) =>
                    handler.ReleaseAsync(
                        ApiAccess.Authorize(context, "Evaluation.Review"),
                        id,
                        ApiAccess.Revision(context),
                        context,
                        ct
                    )
            )
            .WithName("ReleaseReview")
            .Produces<ManagementResource>();
    }
}

/// <summary>Host intake remains observational; these handlers never schedule or retry providers.</summary>
internal sealed class ObservationHandler(RelationalControlPlaneStore store) : IEndpointHandler
{
    internal async Task<IResult> ObserveAsync(
        RequestAuthority authority,
        ExecutionObservation request,
        CancellationToken ct
    )
    {
        using var activity = ControlPlaneTelemetry.Start("executions.observe", authority);
        return Results.Ok(
            await store.RecordObservationAsync(authority.Scope, authority.Actor, request, ct)
        );
    }

    internal async Task<IResult> CaptureAsync(
        RequestAuthority authority,
        string id,
        CaptureIncidentRequest request,
        CancellationToken ct
    )
    {
        using var activity = ControlPlaneTelemetry.Start("incidents.capture", authority);
        return Results.Ok(
            await store.CaptureIncidentAsync(authority.Scope, authority.Actor, id, request, ct)
        );
    }

    internal async Task<IResult> AcceptAsync(
        RequestAuthority authority,
        string id,
        long revision,
        HttpContext context,
        CancellationToken ct
    )
    {
        using var activity = ControlPlaneTelemetry.Start("incidents.accept", authority);
        return ApiAccess.Result(
            await store.AcceptIncidentAsync(authority.Scope, authority.Actor, id, revision, ct),
            context
        );
    }

    internal async Task<IResult> ReleaseAsync(
        RequestAuthority authority,
        string id,
        long revision,
        HttpContext context,
        CancellationToken ct
    )
    {
        using var activity = ControlPlaneTelemetry.Start("reviews.release", authority);
        return ApiAccess.Result(
            await store.ReleaseReviewAsync(authority.Scope, authority.Actor, id, revision, ct),
            context
        );
    }
}
