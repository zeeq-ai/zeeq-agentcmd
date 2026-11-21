using System.Text.Json;
using System.Text.Json.Nodes;
using Zeeq.AgentCmd.ControlPlane;

namespace Zeeq.AgentCmd.ControlPlane.Host;

internal sealed record WorkerClaimRequest(string BuildId, int LeaseSeconds);

internal sealed record WorkerAttemptRequest(
    string WorkId,
    long Epoch,
    string AttemptId,
    JsonElement Receipt
);

internal sealed record WorkerCompletionRequest(string WorkId, long Epoch, JsonElement Receipt);

/// <summary>Routes scoped job and worker requests to the job handler.</summary>
internal sealed class JobsEndpoints : IEndpoint
{
    public void MapEndpoints(IEndpointRouteBuilder api) => Map(api);

    internal static void Map(IEndpointRouteBuilder api)
    {
        api.MapPost(
                "/jobs",
                (
                    HttpContext context,
                    SaveResource input,
                    JobsHandler handler,
                    CancellationToken ct
                ) => handler.CreateAsync(context, input, ct)
            )
            .WithName("CreateJob");
        api.MapGet(
                "/jobs/{id}",
                (
                    HttpContext context,
                    string id,
                    int? offset,
                    int? limit,
                    JobsHandler handler,
                    CancellationToken ct
                ) => handler.ReadAsync(context, id, offset, limit, ct)
            )
            .WithName("GetJob");
        api.MapPost(
                "/jobs/{id}/cancel",
                (HttpContext context, string id, JobsHandler handler, CancellationToken ct) =>
                    handler.CancelAsync(context, id, ct)
            )
            .WithName("CancelJob");
        api.MapPost(
                "/jobs/{id}/resume",
                (HttpContext context, string id, JobsHandler handler, CancellationToken ct) =>
                    handler.ResumeAsync(context, id, ct)
            )
            .WithName("ResumeJob");
        api.MapPost(
                "/workers/claim",
                (
                    HttpContext context,
                    WorkerClaimRequest input,
                    JobsHandler handler,
                    CancellationToken ct
                ) => handler.ClaimAsync(context, input, ct)
            )
            .WithName("ClaimWorker");
        api.MapPost(
                "/workers/attempt-start",
                (
                    HttpContext context,
                    WorkerAttemptRequest input,
                    JobsHandler handler,
                    CancellationToken ct
                ) => handler.StartAsync(context, input, ct)
            )
            .WithName("StartWorkerAttempt");
        api.MapPost(
                "/workers/attempt-finish",
                (
                    HttpContext context,
                    WorkerAttemptRequest input,
                    JobsHandler handler,
                    CancellationToken ct
                ) => handler.FinishAsync(context, input, ct)
            )
            .WithName("FinishWorkerAttempt");
        api.MapPost(
                "/workers/complete-work",
                (
                    HttpContext context,
                    WorkerCompletionRequest input,
                    JobsHandler handler,
                    CancellationToken ct
                ) => handler.CompleteAsync(context, input, ct)
            )
            .WithName("CompleteWorkerWork");
    }
}

/// <summary>Enforces authority before applying job and worker transitions.</summary>
internal sealed class JobsHandler(RelationalControlPlaneStore store, IControlPlaneAssetStore assets)
    : IEndpointHandler
{
    internal async Task<IResult> CreateAsync(
        HttpContext context,
        SaveResource input,
        CancellationToken ct
    )
    {
        var authority = ApiAccess.Authorize(context, "Evaluation.Launch");
        using var activity = ControlPlaneTelemetry.Start("jobs.create", authority);
        var request = input.Body.Deserialize<CreateJobRequest>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        ) ?? throw new ManagementException("job_request_invalid");
        var job = request.Kind switch
        {
            "Backtest" => await store.CreateBacktestAsync(
                authority.Scope,
                authority.Actor,
                input.Name,
                request,
                assets,
                ct
            ),
            "Reassess" => await store.CreateReassessmentAsync(
                authority.Scope,
                authority.Actor,
                input.Name,
                request,
                ct
            ),
            _ => throw new ManagementException("job_request_invalid"),
        };
        return ApiAccess.Result(job, context);
    }

    internal async Task<IResult> ReadAsync(
        HttpContext context,
        string id,
        int? offset,
        int? limit,
        CancellationToken ct
    )
    {
        var authority = ApiAccess.Authorize(context, "Read");
        using var activity = ControlPlaneTelemetry.Start("jobs.read", authority);
        var progress = await store.GetJobPageAsync(
            authority.Scope,
            id,
            offset ?? 0,
            limit ?? 100,
            ct
        );
        var body = JsonNode.Parse(progress.Job.Body.GetRawText())!.AsObject();
        body["items"] = JsonSerializer.SerializeToNode(
            progress.Work,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        );
        body["status"] = progress.Job.State;
        body["counts"] = JsonSerializer.SerializeToNode(progress.Counts);
        body["offset"] = progress.Offset;
        body["limit"] = progress.Limit;
        return ApiAccess.Result(
            progress.Job with
            {
                Body = JsonSerializer.SerializeToElement(body),
            },
            context
        );
    }

    internal async Task<IResult> CancelAsync(HttpContext context, string id, CancellationToken ct)
    {
        var authority = ApiAccess.Authorize(context, "Evaluation.Launch");
        using var activity = ControlPlaneTelemetry.Start("jobs.cancel", authority);
        return ApiAccess.Result(
            await store.CancelJobAsync(
                authority.Scope,
                authority.Actor,
                id,
                ApiAccess.Revision(context),
                ct
            ),
            context
        );
    }

    internal async Task<IResult> ResumeAsync(HttpContext context, string id, CancellationToken ct)
    {
        var authority = ApiAccess.Authorize(context, "Evaluation.Launch");
        using var activity = ControlPlaneTelemetry.Start("jobs.resume", authority);
        return ApiAccess.Result(
            await store.ResumeJobAsync(
                authority.Scope,
                authority.Actor,
                id,
                ApiAccess.Revision(context),
                ct
            ),
            context
        );
    }

    internal async Task<IResult> ClaimAsync(
        HttpContext context,
        WorkerClaimRequest input,
        CancellationToken ct
    )
    {
        var authority = ApiAccess.Authorize(context, "Evaluation.Work");
        using var activity = ControlPlaneTelemetry.Start("workers.claim", authority);
        var candidates = context
            .RequestServices.GetServices<ITypedWorkerPreflight>()
            .OfType<IWorkerCapability>()
            .Where(worker => worker.SupportsBuild(input.BuildId))
            .ToArray();
        if (candidates.Length != 1)
        {
            throw new ManagementException(
                candidates.Length == 0
                    ? "worker_preflight_unavailable"
                    : "worker_capability_ambiguous",
                503
            );
        }
        var preflight = candidates[0];
        var claim = await store.ClaimWorkAsync(
            authority.Scope,
            authority.Actor,
            input.BuildId,
            authority.Actor.Subject,
            TimeSpan.FromSeconds(input.LeaseSeconds),
            ct
        );
        if (claim is null)
        {
            return Results.Ok((WorkClaim?)null);
        }
        try
        {
            var result = await preflight.CheckAsync(authority.Scope, claim, ct);
            if (
                result.Passed
                && result.RequestHash
                    == ContentIdentity.Hash(ContentIdentity.Normalize(claim.Request))
            )
            {
                await store.RecordPreflightAsync(
                    authority.Scope,
                    authority.Actor,
                    claim,
                    result,
                    ct
                );
                return Results.Ok(claim);
            }
        }
        catch (Exception)
        {
            // Failure, including cancellation, cannot authorize a provider send.
        }
        await store.HoldPreflightFailureAsync(
            authority.Scope,
            authority.Actor,
            claim,
            "typed_preflight_failed",
            CancellationToken.None
        );
        throw new ManagementException("worker_preflight_failed", 409);
    }

    internal async Task<IResult> StartAsync(
        HttpContext context,
        WorkerAttemptRequest input,
        CancellationToken ct
    )
    {
        var authority = ApiAccess.Authorize(context, "Evaluation.Work");
        using var activity = ControlPlaneTelemetry.Start("workers.attempt.start", authority);
        await store.StartAttemptAsync(
            authority.Scope,
            authority.Actor,
            input.WorkId,
            authority.Actor.Subject,
            input.Epoch,
            input.AttemptId,
            ct
        );
        return Results.NoContent();
    }

    internal async Task<IResult> FinishAsync(
        HttpContext context,
        WorkerAttemptRequest input,
        CancellationToken ct
    )
    {
        var authority = ApiAccess.Authorize(context, "Evaluation.Work");
        using var activity = ControlPlaneTelemetry.Start("workers.attempt.finish", authority);
        await store.FinishAttemptAsync(
            authority.Scope,
            authority.Actor,
            input.WorkId,
            authority.Actor.Subject,
            input.Epoch,
            input.AttemptId,
            input.Receipt,
            ct
        );
        return Results.NoContent();
    }

    internal async Task<IResult> CompleteAsync(
        HttpContext context,
        WorkerCompletionRequest input,
        CancellationToken ct
    )
    {
        var authority = ApiAccess.Authorize(context, "Evaluation.Work");
        using var activity = ControlPlaneTelemetry.Start("workers.complete", authority);
        await store.CompleteWorkAsync(
            authority.Scope,
            authority.Actor,
            input.WorkId,
            authority.Actor.Subject,
            input.Epoch,
            input.Receipt,
            ct
        );
        return Results.NoContent();
    }
}
