using System.Text.Json;

namespace Zeeq.AgentCmd.ControlPlane;

/// <summary>Binds provider send admission to one authenticated worker claim.</summary>
/// <param name="store">Durable management store.</param>
/// <param name="scope">Authorized workload boundary.</param>
/// <param name="actor">Authenticated worker, retained for audit.</param>
/// <param name="claim">Exact fenced work claim.</param>
public sealed class WorkExecutionJournal(
    RelationalControlPlaneStore store,
    WorkspaceScope scope,
    ManagementActor actor,
    WorkClaim claim
) : IExecutionJournal
{
    internal async Task VerifyClaimAsync(WorkClaim requested, CancellationToken cancellationToken)
    {
        if (
            actor.Subject != claim.Owner
            || requested.WorkId != claim.WorkId
            || requested.Owner != claim.Owner
            || requested.Epoch != claim.Epoch
            || ContentIdentity.Normalize(requested.Request)
                != ContentIdentity.Normalize(claim.Request)
        )
        {
            throw new ManagementException("claim_stale", 409);
        }
        await using var db = await store.OpenAsync(cancellationToken);
        await using var command = RelationalControlPlaneStore.Command(
            db,
            null,
            "SELECT request FROM cp_work WHERE scope=@scope AND id=@id AND owner=@owner AND epoch=@epoch AND status='Claimed'",
            ("scope", scope.Key),
            ("id", claim.WorkId),
            ("owner", claim.Owner),
            ("epoch", claim.Epoch)
        );
        if (
            (await command.ExecuteScalarAsync(cancellationToken)) as string
            != ContentIdentity.Normalize(requested.Request)
        )
        {
            throw new ManagementException("claim_stale", 409);
        }
    }

    /// <inheritdoc />
    public async ValueTask AppendAsync(ExecutionEvent record, CancellationToken cancellationToken)
    {
        if (record.AttemptId is not { } attempt)
        {
            throw new ManagementException("attempt_identity_invalid");
        }
        if (record.Kind == "attempt.started")
        {
            await store.StartAttemptAsync(
                scope,
                actor,
                claim.WorkId,
                claim.Owner,
                claim.Epoch,
                attempt.ToString("D"),
                cancellationToken
            );
        }
        else if (record.Kind == "attempt.finished")
        {
            await store.FinishAttemptAsync(
                scope,
                actor,
                claim.WorkId,
                claim.Owner,
                claim.Epoch,
                attempt.ToString("D"),
                JsonSerializer.SerializeToElement(record),
                cancellationToken
            );
        }
        else
        {
            throw new ManagementException("journal_event_unsupported");
        }
    }
}
