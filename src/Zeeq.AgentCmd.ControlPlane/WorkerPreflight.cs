using System.Text.Json;

namespace Zeeq.AgentCmd.ControlPlane;

/// <summary>Application-owned, side-effect-free typed validation of one exact pinned worker request.</summary>
/// <remarks>The host registers this port only for its own compiled commands and evaluation runtime. Implementations must not call providers or mutate external dependencies.</remarks>
public interface ITypedWorkerPreflight
{
    /// <summary>Validates the claimed build, exact candidate, input, rendering and host execution policy before provider admission.</summary>
    Task<WorkerPreflightResult> CheckAsync(
        WorkspaceScope scope,
        WorkClaim claim,
        CancellationToken cancellationToken
    );
}

/// <summary>Declares which application build a worker can preflight before any work is claimed.</summary>
public interface IWorkerCapability : ITypedWorkerPreflight
{
    /// <summary>Returns whether this worker can execute the named build or evaluator route.</summary>
    bool SupportsBuild(string buildId);
}

/// <summary>Typed preflight evidence for a single immutable request.</summary>
/// <param name="Passed">Whether the exact typed invocation can execute without fallback.</param>
/// <param name="RequestHash">Content identity of the exact worker request inspected.</param>
/// <param name="Reason">Sanitized stable rejection reason, when not passed.</param>
public sealed record WorkerPreflightResult(bool Passed, string RequestHash, string? Reason = null);

public sealed partial class RelationalControlPlaneStore
{
    private static string PreflightId(string workId, string owner, long epoch) =>
        ContentIdentity.Hash(JsonSerializer.Serialize(new object[] { workId, owner, epoch }));

    /// <summary>Records successful typed preflight against the exact current claim and immutable request.</summary>
    public Task RecordPreflightAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        WorkClaim claim,
        WorkerPreflightResult result,
        CancellationToken cancellationToken = default
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                if (
                    actor.Subject != claim.Owner
                    || !result.Passed
                    || result.RequestHash
                        != ContentIdentity.Hash(ContentIdentity.Normalize(claim.Request))
                )
                {
                    throw new ManagementException("worker_preflight_invalid", 409);
                }
                await using var command = Command(
                    db,
                    tx,
                    "UPDATE cp_work SET status=status WHERE scope=@scope AND id=@id AND owner=@owner AND epoch=@epoch AND status='Claimed' AND lease_until>@now RETURNING request",
                    ("scope", scope.Key),
                    ("id", claim.WorkId),
                    ("owner", claim.Owner),
                    ("epoch", claim.Epoch),
                    ("now", Timestamp(time.GetUtcNow()))
                );
                var request = await command.ExecuteScalarAsync(cancellationToken) as string;
                if (request is null || ContentIdentity.Hash(request) != result.RequestHash)
                {
                    throw new ManagementException("claim_stale", 409);
                }
                var body = ContentIdentity.Normalize(
                    JsonSerializer.SerializeToElement(
                        new
                        {
                            workId = claim.WorkId,
                            claim.Owner,
                            claim.Epoch,
                            result.RequestHash,
                        },
                        Json
                    )
                );
                await InsertAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    "worker-preflights",
                    PreflightId(claim.WorkId, claim.Owner, claim.Epoch),
                    "Typed preflight",
                    "Passed",
                    body,
                    cancellationToken
                );
                return true;
            },
            cancellationToken
        );

    /// <summary>Holds a failed preflight claim without admitting any provider send.</summary>
    public Task HoldPreflightFailureAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        WorkClaim claim,
        string reason,
        CancellationToken cancellationToken = default
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                var updated = await ExecuteAsync(
                    db,
                    tx,
                    "UPDATE cp_work SET status='Blocked' WHERE scope=@scope AND id=@id AND owner=@owner AND epoch=@epoch AND status='Claimed' AND NOT EXISTS (SELECT 1 FROM cp_attempts a WHERE a.scope=cp_work.scope AND a.work_id=cp_work.id)",
                    cancellationToken,
                    ("scope", scope.Key),
                    ("id", claim.WorkId),
                    ("owner", claim.Owner),
                    ("epoch", claim.Epoch)
                );
                if (updated != 1)
                {
                    throw new ManagementException("claim_stale", 409);
                }
                await EventAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    "work.preflight_blocked",
                    "work",
                    claim.WorkId,
                    claim.Epoch,
                    new { reason },
                    cancellationToken
                );
                return true;
            },
            cancellationToken
        );
}
