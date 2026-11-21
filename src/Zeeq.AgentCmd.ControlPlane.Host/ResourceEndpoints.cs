using Zeeq.AgentCmd.ControlPlane;

namespace Zeeq.AgentCmd.ControlPlane.Host;

/// <summary>Thin feature routes resolve transient handlers by convention.</summary>
internal sealed class ResourceEndpoint : IEndpoint
{
    private static readonly string[] Kinds =
    [
        "builds",
        "publications",
        "datasets",
        "benchmarks",
        "jobs",
        "executions",
        "reviews",
        "assessments",
        "comparisons",
        "judge-configurations",
        "calibrations",
        "source-profiles",
        "bindings",
        "workers",
        "decisions",
    ];
    private static readonly string[] Editable =
    [
        "publications",
        "datasets",
        "benchmarks",
        "judge-configurations",
        "source-profiles",
        "bindings",
    ];

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        foreach (var kind in Kinds)
        {
            var collection = kind;
            endpoints
                .MapGet(
                    "/" + collection,
                    async (HttpContext context, ResourceHandler handler, CancellationToken ct) =>
                    {
                        context.Response.Headers["X-AgentCmd-List-Limit"] = "250";
                        return TypedResults.Ok(
                            await handler.ListAsync(
                                ApiAccess.Authorize(context, "Read"),
                                collection,
                                ct
                            )
                        );
                    }
                )
                .WithName("List" + Name(collection))
                .Produces<IReadOnlyList<ManagementResource>>();
            if (collection != "jobs")
            {
                endpoints
                    .MapGet(
                        "/" + collection + "/{id}",
                        async (
                            string id,
                            HttpContext context,
                            ResourceHandler handler,
                            CancellationToken ct
                        ) =>
                            ApiAccess.Result(
                                await handler.ReadAsync(
                                    ApiAccess.Authorize(context, "Read"),
                                    collection,
                                    id,
                                    ct
                                ),
                                context
                            )
                    )
                    .WithName("Get" + Name(collection))
                    .Produces<ManagementResource>();
            }
        }
        endpoints
            .MapPost(
                "/builds",
                async (
                    SaveResource request,
                    HttpContext context,
                    ResourceHandler handler,
                    CancellationToken ct
                ) =>
                {
                    var resource = await handler.RegisterBuildAsync(
                        ApiAccess.Authorize(context, "Configuration.Write"),
                        request,
                        ct
                    );
                    context.Response.Headers.ETag =
                        '"'
                        + resource.Revision.ToString(
                            System.Globalization.CultureInfo.InvariantCulture
                        )
                        + '"';
                    return TypedResults.Created(
                        "/api/v1/builds/" + Uri.EscapeDataString(resource.Id),
                        resource
                    );
                }
            )
            .WithName("RegisterBuild")
            .Produces<ManagementResource>(201);
        foreach (var kind in Editable)
        {
            var collection = kind;
            var permission = collection is "datasets" or "benchmarks" or "judge-configurations"
                ? "Data.Curate"
                : "Configuration.Write";
            var create = endpoints
                .MapPost(
                    "/" + collection,
                    async (
                        SaveResource request,
                        HttpContext context,
                        ResourceHandler handler,
                        CancellationToken ct
                    ) =>
                    {
                        var resource = await handler.SaveAsync(
                            ApiAccess.Authorize(context, permission),
                            collection,
                            request,
                            null,
                            null,
                            ct
                        );
                        context.Response.Headers.ETag =
                            '"'
                            + resource.Revision.ToString(
                                System.Globalization.CultureInfo.InvariantCulture
                            )
                            + '"';
                        return TypedResults.Created(
                            "/api/v1/" + collection + "/" + resource.Id,
                            resource
                        );
                    }
                )
                .WithName("Create" + Name(collection))
                .Produces<ManagementResource>(201);
            var update = endpoints
                .MapPut(
                    "/" + collection + "/{id}",
                    async (
                        string id,
                        SaveResource request,
                        HttpContext context,
                        ResourceHandler handler,
                        CancellationToken ct
                    ) =>
                        ApiAccess.Result(
                            await handler.SaveAsync(
                                ApiAccess.Authorize(context, permission),
                                collection,
                                request,
                                id,
                                ApiAccess.Revision(context),
                                ct
                            ),
                            context
                        )
                )
                .WithName("Update" + Name(collection))
                .Produces<ManagementResource>();
            ResourceSchema.Write(create, collection);
            ResourceSchema.Response(create, collection, 201);
            ResourceSchema.Write(update, collection);
            ResourceSchema.Response(update, collection);
            if (
                collection is "publications" or "datasets" or "benchmarks" or "judge-configurations"
            )
            {
                endpoints
                    .MapPost(
                        "/" + collection + "/{id}/freeze",
                        async (
                            string id,
                            HttpContext context,
                            ResourceHandler handler,
                            CancellationToken ct
                        ) =>
                            ApiAccess.Result(
                                await handler.FreezeAsync(
                                    ApiAccess.Authorize(context, permission),
                                    collection,
                                    id,
                                    ApiAccess.Revision(context),
                                    ct
                                ),
                                context
                            )
                    )
                    .WithName("Freeze" + Name(collection))
                    .Produces<ManagementResource>();
            }
        }
        endpoints
            .MapPost(
                "/assets",
                async (HttpContext context, ResourceHandler handler, CancellationToken ct) =>
                {
                    var asset = await handler.PutAssetAsync(
                        ApiAccess.Authorize(context, "Data.Curate"),
                        context.Request.Body,
                        ct
                    );
                    return TypedResults.Created("/api/v1/assets/" + asset.Hash, asset);
                }
            )
            .WithName("StoreAsset")
            .DisableAntiforgery();
        endpoints
            .MapGet(
                "/assets/{hash}",
                async (
                    string hash,
                    HttpContext context,
                    ResourceHandler handler,
                    CancellationToken ct
                ) =>
                    Results.Stream(
                        await handler.OpenAssetAsync(
                            ApiAccess.Authorize(context, "Read"),
                            hash,
                            ct
                        ),
                        "application/octet-stream"
                    )
            )
            .WithName("ReadAsset");
    }

    private static string Name(string value) =>
        string.Concat(value.Split('-').Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
}

/// <summary>Authorizes no ambient data; explicit scoped requests govern feature validation and committed mutations.</summary>
internal sealed class ResourceHandler(
    RelationalControlPlaneStore store,
    IControlPlaneAssetStore assets,
    ILogger<ResourceHandler> log
) : IEndpointHandler
{
    internal async Task<IReadOnlyList<ManagementResource>> ListAsync(
        RequestAuthority authority,
        string kind,
        CancellationToken ct
    )
    {
        using var activity = ControlPlaneTelemetry.Start("resources.list", authority);
        var resources = await store.ListAsync(authority.Scope, kind, ct);
        if (kind != "datasets")
        {
            return resources;
        }
        var projected = new List<ManagementResource>();
        foreach (var resource in resources)
        {
            projected.Add(await store.ProjectDatasetAsync(authority.Scope, resource, ct));
        }
        return projected;
    }

    internal async Task<ManagementResource> ReadAsync(
        RequestAuthority authority,
        string kind,
        string id,
        CancellationToken ct
    )
    {
        using var activity = ControlPlaneTelemetry.Start("resources.read", authority);
        var resource = await store.ReadAsync(authority.Scope, kind, id, ct)
            ?? throw new ManagementException("not_found", 404);
        return kind == "datasets"
            ? await store.ProjectDatasetAsync(authority.Scope, resource, ct)
            : resource;
    }

    internal async Task<ManagementResource> RegisterBuildAsync(
        RequestAuthority authority,
        SaveResource request,
        CancellationToken ct
    )
    {
        using var activity = ControlPlaneTelemetry.Start("builds.register", authority);
        ResourceRules.ValidateEnvelope(request);
        _ = ResourceRules.Manifest(request.Body);
        var resource = await store.RegisterBuildAsync(
            authority.Scope,
            authority.Actor,
            request,
            ct
        );
        Committed(resource);
        return resource;
    }

    internal async Task<ManagementResource> SaveAsync(
        RequestAuthority authority,
        string kind,
        SaveResource request,
        string? id,
        long? expected,
        CancellationToken ct
    )
    {
        using var activity = ControlPlaneTelemetry.Start("drafts.save", authority);
        ResourceRules.ValidateEnvelope(request);
        await ResourceRules.ValidateAsync(store, authority.Scope, kind, request.Body, false, ct);
        var resource = await store.SaveDraftAsync(
            authority.Scope,
            authority.Actor,
            kind,
            request,
            id,
            expected,
            ct
        );
        Committed(resource);
        return kind == "datasets"
            ? await store.ProjectDatasetAsync(authority.Scope, resource, ct)
            : resource;
    }

    internal async Task<ManagementResource> FreezeAsync(
        RequestAuthority authority,
        string kind,
        string id,
        long expected,
        CancellationToken ct
    )
    {
        using var activity = ControlPlaneTelemetry.Start("content.freeze", authority);
        var resource = await store.ReadAsync(authority.Scope, kind, id, ct)
            ?? throw new ManagementException("not_found", 404);
        await ResourceRules.ValidateAsync(store, authority.Scope, kind, resource.Body, true, ct);
        if (kind == "datasets")
        {
            foreach (var item in resource.Body.GetProperty("cases").EnumerateArray())
            {
                if (!item.TryGetProperty("assets", out var references))
                {
                    continue;
                }
                foreach (var reference in references.EnumerateArray())
                {
                    var hash =
                        reference.ValueKind == System.Text.Json.JsonValueKind.String
                            ? reference.GetString()
                            : reference.GetProperty("hash").GetString();
                    if (string.IsNullOrWhiteSpace(hash))
                    {
                        throw new ManagementException("asset_identity_required");
                    }
                    await using var retained = await assets.OpenAsync(authority.Scope, hash, ct);
                }
            }
        }
        var frozen = await store.FreezeAsync(
            authority.Scope,
            authority.Actor,
            kind,
            id,
            expected,
            ct
        );
        Committed(frozen);
        return kind == "datasets"
            ? await store.ProjectDatasetAsync(authority.Scope, frozen, ct)
            : frozen;
    }

    internal async Task<StoredAsset> PutAssetAsync(
        RequestAuthority authority,
        Stream source,
        CancellationToken ct
    )
    {
        using var activity = ControlPlaneTelemetry.Start("assets.store", authority);
        return await assets.PutAsync(authority.Scope, source, ct);
    }

    internal Task<Stream> OpenAssetAsync(
        RequestAuthority authority,
        string hash,
        CancellationToken ct
    ) => assets.OpenAsync(authority.Scope, hash, ct);

    private void Committed(ManagementResource resource) =>
        log.LogInformation(
            "Management {Kind} {ResourceId} committed revision {Revision}",
            resource.Kind,
            resource.Id,
            resource.Revision
        );
}
