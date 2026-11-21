namespace Zeeq.AgentCmd.ControlPlane;

/// <summary>Result of one worker poll; no-work never implies that a job passed.</summary>
/// <param name="Status">Completed, Blocked, or NoWork.</param>
/// <param name="WorkId">Claimed work when one was found.</param>
public sealed record WorkerDispatchResult(string Status, string? WorkId = null);

/// <summary>Application-owned, bounded worker dispatcher for an explicitly registered adapter.</summary>
/// <remarks>The host controls scheduling and lifetime. Journal failures and uncertain sends remain held; the dispatcher never retries them.</remarks>
public sealed class InProcessEvaluationWorker
{
    private readonly RelationalControlPlaneStore store;
    private readonly ManagementActor actor;
    private readonly IWorkerCapability capability;
    private readonly Func<
        WorkspaceScope,
        ManagementActor,
        WorkClaim,
        RelationalControlPlaneStore,
        CancellationToken,
        Task
    > execute;

    private InProcessEvaluationWorker(
        RelationalControlPlaneStore store,
        ManagementActor actor,
        IWorkerCapability capability,
        Func<
            WorkspaceScope,
            ManagementActor,
            WorkClaim,
            RelationalControlPlaneStore,
            CancellationToken,
            Task
        > execute
    )
    {
        this.store = store;
        this.actor = actor;
        this.capability = capability;
        this.execute = execute;
    }

    /// <summary>Creates a worker for one application-registered typed command.</summary>
    public static InProcessEvaluationWorker ForCommand<TInput, TOutput>(
        RelationalControlPlaneStore store,
        ManagementActor actor,
        TypedWorkerAdapter<TInput, TOutput> adapter
    ) => new(store, actor, adapter, async (scope, owner, claim, database, ct) =>
        {
            await adapter.ExecuteAndCompleteAsync(scope, owner, claim, database, ct);
        });

    /// <summary>Creates a worker for one authorized judge route and host-owned execution context.</summary>
    public static InProcessEvaluationWorker ForJudge(
        RelationalControlPlaneStore store,
        ManagementActor actor,
        JudgeWorkerAdapter adapter,
        Func<WorkClaim, CommandExecutionContext> contextFactory
    ) => new(store, actor, adapter, async (scope, owner, claim, database, ct) =>
        {
            await adapter.ExecuteAsync(scope, owner, claim, database, contextFactory(claim), ct);
        });

    /// <summary>Claims and executes at most one compatible work item after durable typed preflight.</summary>
    public async Task<WorkerDispatchResult> DispatchOnceAsync(
        WorkspaceScope scope,
        string buildId,
        TimeSpan lease,
        CancellationToken cancellationToken = default
    )
    {
        if (!capability.SupportsBuild(buildId))
        {
            return new("NoWork");
        }
        var claim = await store.ClaimWorkAsync(
            scope,
            actor,
            buildId,
            actor.Subject,
            lease,
            cancellationToken
        );
        if (claim is null)
        {
            return new("NoWork");
        }
        try
        {
            var preflight = await capability.CheckAsync(scope, claim, cancellationToken);
            if (
                !preflight.Passed
                || preflight.RequestHash
                    != ContentIdentity.Hash(ContentIdentity.Normalize(claim.Request))
            )
            {
                await store.HoldPreflightFailureAsync(
                    scope,
                    actor,
                    claim,
                    "typed_preflight_failed",
                    CancellationToken.None
                );
                return new("Blocked", claim.WorkId);
            }
            await store.RecordPreflightAsync(scope, actor, claim, preflight, cancellationToken);
            await execute(scope, actor, claim, store, cancellationToken);
            return new("Completed", claim.WorkId);
        }
        catch
        {
            try
            {
                // This update succeeds only when no send was admitted. Otherwise the journal owns reconciliation.
                await store.HoldPreflightFailureAsync(
                    scope,
                    actor,
                    claim,
                    "worker_failed_before_send",
                    CancellationToken.None
                );
            }
            catch (ManagementException) { }
            throw;
        }
    }
}
