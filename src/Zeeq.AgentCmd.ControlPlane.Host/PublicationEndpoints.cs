using Zeeq.AgentCmd.ControlPlane;

namespace Zeeq.AgentCmd.ControlPlane.Host;

/// <summary>Typed target capability data; credential references stay in server configuration.</summary>
/// <param name="Profile">Authorized binding metadata.</param>
/// <param name="Capabilities">Implemented and qualified capabilities.</param>
public sealed record PublicationTargetView(
    PublicationTargetProfile Profile,
    PublicationTargetCapabilities Capabilities
);

/// <summary>Explicitly configured targets; absence means publishing is unconfigured.</summary>
/// <param name="State">Configured or Unconfigured.</param>
/// <param name="Targets">Configured targets in the authorized scope.</param>
/// <param name="FileDevelopment">File source has no supported CAS writer.</param>
public sealed record PublicationTargetsView(
    string State,
    IReadOnlyList<PublicationTargetView> Targets,
    PublicationTargetCapabilities FileDevelopment
);

/// <summary>Routes reviewed publication delivery and authenticated adoption reports.</summary>
internal sealed class PublicationEndpoints : IEndpoint
{
    public void MapEndpoints(IEndpointRouteBuilder api) => Map(api);

    internal static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/publication-targets", (HttpContext context, PublicationHandler handler) =>
                handler.Targets(context)
            )
            .WithName("ListPublicationTargets")
            .Produces<PublicationTargetsView>();
        api.MapPost(
                "/decisions/publication",
                (
                    HttpContext context,
                    PublicationDecisionRequest request,
                    PublicationHandler handler,
                    CancellationToken ct
                ) => handler.DecisionAsync(context, request, ct)
            )
            .WithName("SavePublicationDecision")
            .Produces<ManagementResource>();
        api.MapPost(
                "/publication-targets/{id}/review",
                (
                    HttpContext context,
                    string id,
                    PublicationReviewRequest request,
                    PublicationHandler handler,
                    CancellationToken ct
                ) => handler.ReviewAsync(context, id, request, ct)
            )
            .WithName("ReviewPublication")
            .Produces<ManagementResource>();
        api.MapGet(
                "/publication-operations",
                (HttpContext context, PublicationHandler handler, CancellationToken ct) =>
                    handler.ListAsync(context, ct)
            )
            .WithName("ListPublicationOperations")
            .Produces<IReadOnlyList<ManagementResource>>();
        api.MapGet(
                "/publication-operations/{id}",
                (
                    HttpContext context,
                    string id,
                    PublicationHandler handler,
                    CancellationToken ct
                ) => handler.ReadAsync(context, id, ct)
            )
            .WithName("GetPublicationOperation")
            .Produces<ManagementResource>();
        api.MapPost(
                "/publication-operations/{id}/deliver",
                (
                    HttpContext context,
                    string id,
                    PublicationHandler handler,
                    CancellationToken ct
                ) => handler.DeliverAsync(context, id, ct)
            )
            .WithName("DeliverPublication")
            .Produces<ManagementResource>();
        api.MapPost(
                "/publication-operations/{id}/reconcile",
                (
                    HttpContext context,
                    string id,
                    PublicationHandler handler,
                    CancellationToken ct
                ) => handler.ReconcileAsync(context, id, ct)
            )
            .WithName("ReconcilePublication")
            .Produces<ManagementResource>();
        api.MapGet(
                "/publication-operations/{id}/adoption",
                (
                    HttpContext context,
                    string id,
                    PublicationHandler handler,
                    CancellationToken ct
                ) => handler.AdoptionAsync(context, id, ct)
            )
            .WithName("GetPublicationAdoption")
            .Produces<PublicationAdoption>();
        api.MapPost(
                "/hosts/report",
                (
                    HttpContext context,
                    PublicationHostReport request,
                    PublicationHandler handler,
                    CancellationToken ct
                ) => handler.ReportAsync(context, request, ct)
            )
            .WithName("ReportHostAdoption")
            .Produces<ManagementResource>();
    }
}

/// <summary>Thin authenticated handlers; no trace contains publication content or credentials.</summary>
internal sealed class PublicationHandler(
    RelationalControlPlaneStore store,
    IServiceProvider services
) : IEndpointHandler
{
    private PublicationTargetRegistry Registry =>
        services.GetService<PublicationTargetRegistry>() ?? new([]);
    private PublicationCoordinator Coordinator => new(store, Registry);

    internal IResult Targets(HttpContext context)
    {
        var authority = ApiAccess.Authorize(context, "Read");
        using var activity = ControlPlaneTelemetry.Start("publications.targets", authority);
        var targets = Registry
            .ForScope(authority.Scope)
            .Select(t => new PublicationTargetView(t.Profile, t.Capabilities))
            .ToArray();
        return Results.Ok(
            new PublicationTargetsView(
                targets.Length == 0 ? "Unconfigured" : "Configured",
                targets,
                new("FileDevelopment", false, false, false)
            )
        );
    }

    internal async Task<IResult> DecisionAsync(
        HttpContext context,
        PublicationDecisionRequest request,
        CancellationToken ct
    )
    {
        var authority = ApiAccess.Authorize(context, "Publication.Publish");
        using var activity = ControlPlaneTelemetry.Start("publications.decision", authority);
        var target = Registry.Resolve(authority.Scope, request.TargetId);
        return ApiAccess.Result(
            await store.SavePublicationDecisionAsync(
                authority.Scope,
                authority.Actor,
                target.Profile,
                request,
                ct
            ),
            context
        );
    }

    internal async Task<IResult> ReviewAsync(
        HttpContext context,
        string id,
        PublicationReviewRequest request,
        CancellationToken ct
    )
    {
        var authority = ApiAccess.Authorize(context, "Publication.Publish");
        using var activity = ControlPlaneTelemetry.Start("publications.review", authority);
        return ApiAccess.Result(
            await Coordinator.ReviewAsync(authority.Scope, authority.Actor, id, request, ct),
            context
        );
    }

    internal async Task<IResult> ListAsync(HttpContext context, CancellationToken ct)
    {
        var authority = ApiAccess.Authorize(context, "Read");
        using var activity = ControlPlaneTelemetry.Start("publications.list", authority);
        context.Response.Headers["X-AgentCmd-List-Limit"] = "250";
        return Results.Ok(await store.ListAsync(authority.Scope, "publication-operations", ct));
    }

    internal async Task<IResult> ReadAsync(HttpContext context, string id, CancellationToken ct)
    {
        var authority = ApiAccess.Authorize(context, "Read");
        using var activity = ControlPlaneTelemetry.Start("publications.read", authority);
        return ApiAccess.Result(
            await store.ReadAsync(authority.Scope, "publication-operations", id, ct)
                ?? throw new ManagementException("publication_operation_not_found", 404),
            context
        );
    }

    internal async Task<IResult> DeliverAsync(HttpContext context, string id, CancellationToken ct)
    {
        var authority = ApiAccess.Authorize(context, "Publication.Publish");
        using var activity = ControlPlaneTelemetry.Start("publications.deliver", authority);
        return ApiAccess.Result(
            await Coordinator.DeliverAsync(
                authority.Scope,
                authority.Actor,
                id,
                ApiAccess.Revision(context),
                ct
            ),
            context
        );
    }

    internal async Task<IResult> ReconcileAsync(
        HttpContext context,
        string id,
        CancellationToken ct
    )
    {
        var authority = ApiAccess.Authorize(context, "Operations.Manage");
        using var activity = ControlPlaneTelemetry.Start("publications.reconcile", authority);
        return ApiAccess.Result(
            await Coordinator.ReconcileAsync(
                authority.Scope,
                authority.Actor,
                id,
                ApiAccess.Revision(context),
                ct
            ),
            context
        );
    }

    internal async Task<IResult> AdoptionAsync(HttpContext context, string id, CancellationToken ct)
    {
        var authority = ApiAccess.Authorize(context, "Read");
        using var activity = ControlPlaneTelemetry.Start("publications.adoption", authority);
        return Results.Ok(await store.ReadPublicationAdoptionAsync(authority.Scope, id, ct));
    }

    internal async Task<IResult> ReportAsync(
        HttpContext context,
        PublicationHostReport request,
        CancellationToken ct
    )
    {
        var authority = ApiAccess.Authorize(context, "Operations.Manage");
        using var activity = ControlPlaneTelemetry.Start("publications.host_report", authority);
        var target = Registry.Resolve(authority.Scope, request.TargetId);
        return ApiAccess.Result(
            await store.ReportPublicationHostAsync(
                authority.Scope,
                authority.Actor,
                target.Profile,
                request,
                ct
            ),
            context
        );
    }
}
