using Zeeq.AgentCmd.ControlPlane;

namespace Zeeq.AgentCmd.ControlPlane.Host;

/// <summary>Workspace and readiness routes stay thin; the handler reports actual configured capabilities.</summary>
internal sealed class WorkspaceEndpoint : IEndpoint
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapGet(
                "/workspace",
                (HttpContext context, WorkspaceHandler handler, CancellationToken ct) =>
                    handler.HandleAsync(ApiAccess.Authorize(context, "Read"), context, ct)
            )
            .WithName("GetWorkspace")
            .Produces<WorkspaceDefinition>();
        endpoints
            .MapGet(
                "/health",
                (HttpContext context, WorkspaceHandler handler, CancellationToken ct) =>
                    handler.HealthAsync(ApiAccess.Authorize(context, "Read"), ct)
            )
            .WithName("GetReadiness")
            .Produces<HealthDefinition>();
    }
}

/// <summary>Readiness is observed from configured storage and trusted worker capabilities, not from arbitrary artifact existence.</summary>
internal sealed class WorkspaceHandler(
    RelationalControlPlaneStore store,
    ControlPlaneOptions options,
    IServiceProvider services
) : IEndpointHandler
{
    internal async Task<IResult> HandleAsync(
        RequestAuthority authority,
        HttpContext context,
        CancellationToken ct
    )
    {
        using var activity = ControlPlaneTelemetry.Start("workspace.read", authority);
        return Results.Ok(
            new WorkspaceDefinition(
                new ScopeDefinition(
                    authority.Scope.Application,
                    authority.Scope.Environment,
                    authority.Scope.Partition
                ),
                new IdentityDefinition(
                    authority.Actor.Subject,
                    context
                        .User.FindAll("agentcmd.permission")
                        .Select(claim => claim.Value)
                        .ToArray()
                ),
                new AuthenticationDefinition(options.LocalMode ? "Local" : "External"),
                new StorageDefinition(
                    store.Provider,
                    store.Provider == "Sqlite" ? "SingleInstance" : "Shared"
                ),
                new CapabilityDefinition(
                    true,
                    services.GetServices<ITypedWorkerPreflight>().Any(),
                    services
                        .GetService<PublicationTargetRegistry>()
                        ?.ForScope(authority.Scope)
                        .Select(target => target.Profile.Id)
                        .ToArray()
                        ?? [],
                    false
                ),
                await store.ListAsync(authority.Scope, "workers", ct)
            )
        );
    }

    internal async Task<IResult> HealthAsync(RequestAuthority authority, CancellationToken ct)
    {
        using var activity = ControlPlaneTelemetry.Start("workspace.health", authority);
        await using var connection = await store.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        _ = await command.ExecuteScalarAsync(ct);
        return Results.Ok(new HealthDefinition("Ready"));
    }
}
