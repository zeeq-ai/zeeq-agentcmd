using System.Text.Json;

namespace Zeeq.AgentCmd.ControlPlane;

/// <summary>A durable work item for an already authorized and pinned job.</summary>
/// <param name="JobId">Owning run identity.</param>
/// <param name="BuildId">Required application build.</param>
/// <param name="CandidateId">Exact candidate identity.</param>
/// <param name="CaseId">Frozen case identity.</param>
/// <param name="RepeatIndex">Zero-based generation repeat.</param>
/// <param name="Request">Immutable worker request with its pinned inputs.</param>
public sealed record WorkRequest(
    string JobId,
    string BuildId,
    string CandidateId,
    string CaseId,
    int RepeatIndex,
    JsonElement Request
);

/// <summary>A claim authorizes starts only until its lease expires or its epoch changes.</summary>
/// <param name="WorkId">Durable work identity.</param>
/// <param name="Owner">Authenticated worker identity.</param>
/// <param name="Epoch">Fencing epoch.</param>
/// <param name="LeaseUntil">Expiry of start authority.</param>
/// <param name="Request">Pinned request.</param>
public sealed record WorkClaim(
    string WorkId,
    string Owner,
    long Epoch,
    DateTimeOffset LeaseUntil,
    JsonElement Request
);

/// <summary>Durable attempt admission and receipt transitions for trusted worker hosts.</summary>
public sealed partial class RelationalControlPlaneStore
{
    /// <summary>Enqueues a work item after the caller has validated and authorized the complete job.</summary>
    public Task<string> EnqueueWorkAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        WorkRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (
            string.IsNullOrWhiteSpace(request.JobId)
            || string.IsNullOrWhiteSpace(request.BuildId)
            || string.IsNullOrWhiteSpace(request.CandidateId)
            || string.IsNullOrWhiteSpace(request.CaseId)
            || request.RepeatIndex < 0
        )
        {
            throw new ManagementException("work_identity_invalid");
        }
        var id = ContentIdentity.Hash(
            JsonSerializer.Serialize(
                new[]
                {
                    request.JobId,
                    request.CandidateId,
                    request.CaseId,
                    request.RepeatIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }
            )
        );
        var body = ContentIdentity.Normalize(request.Request);
        return TransactionAsync(
            async (db, tx) =>
            {
                await ExecuteAsync(
                    db,
                    tx,
                    "INSERT INTO cp_work(scope,id,job_id,build_id,candidate_id,case_id,repeat_index,status,request) VALUES(@scope,@id,@job,@build,@candidate,@case,@repeat,'Queued',@request)",
                    cancellationToken,
                    ("scope", scope.Key),
                    ("id", id),
                    ("job", request.JobId),
                    ("build", request.BuildId),
                    ("candidate", request.CandidateId),
                    ("case", request.CaseId),
                    ("repeat", request.RepeatIndex),
                    ("request", body)
                );
                await EventAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    "work.queued",
                    "work",
                    id,
                    1,
                    new { request.JobId },
                    cancellationToken
                );
                return id;
            },
            cancellationToken
        );
    }

    /// <summary>Claims compatible queued work; expired claims with an admitted attempt are held for reconciliation.</summary>
    public Task<WorkClaim?> ClaimWorkAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string buildId,
        string owner,
        TimeSpan lease,
        CancellationToken cancellationToken = default
    )
    {
        if (
            string.IsNullOrWhiteSpace(buildId)
            || string.IsNullOrWhiteSpace(owner)
            || lease <= TimeSpan.Zero
            || lease > TimeSpan.FromHours(1)
        )
        {
            throw new ManagementException("claim_invalid");
        }
        return TransactionAsync<WorkClaim?>(
            async (db, tx) =>
            {
                var now = time.GetUtcNow();
                var until = now.Add(lease);
                // The conditional update is the authority, including when two PostgreSQL workers select the same row.
                await using var command = Command(
                    db,
                    tx,
                    "UPDATE cp_work SET owner=@owner,epoch=epoch+1,lease_until=@until,status='Claimed' WHERE scope=@scope AND id=(SELECT id FROM cp_work WHERE scope=@scope AND build_id=@build AND EXISTS (SELECT 1 FROM cp_documents j WHERE j.scope=cp_work.scope AND j.kind='jobs' AND j.id=cp_work.job_id AND j.state='Queued') AND (status='Queued' OR (status='Claimed' AND lease_until<=@now AND NOT EXISTS (SELECT 1 FROM cp_attempts a WHERE a.scope=cp_work.scope AND a.work_id=cp_work.id))) ORDER BY id LIMIT 1) AND (status='Queued' OR (status='Claimed' AND lease_until<=@now AND NOT EXISTS (SELECT 1 FROM cp_attempts a WHERE a.scope=cp_work.scope AND a.work_id=cp_work.id))) RETURNING id,epoch,request",
                    ("owner", owner),
                    ("until", Timestamp(until)),
                    ("scope", scope.Key),
                    ("build", buildId),
                    ("now", Timestamp(now))
                );
                string id;
                long epoch;
                JsonElement request;
                await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    if (!await reader.ReadAsync(cancellationToken))
                    {
                        return null;
                    }
                    id = reader.GetString(0);
                    epoch = reader.GetInt64(1);
                    request = Element(reader.GetString(2));
                }
                await EventAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    "work.claimed",
                    "work",
                    id,
                    epoch,
                    new { owner, leaseUntil = until },
                    cancellationToken
                );
                return new WorkClaim(id, owner, epoch, until, request);
            },
            cancellationToken
        );
    }

    /// <summary>Durably admits a send only under the current unexpired claim.</summary>
    public Task StartAttemptAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string workId,
        string owner,
        long epoch,
        string attemptId,
        CancellationToken cancellationToken = default
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                if (string.IsNullOrWhiteSpace(attemptId) || string.IsNullOrWhiteSpace(owner))
                {
                    throw new ManagementException("attempt_identity_invalid");
                }
                var now = Timestamp(time.GetUtcNow());
                // Lock and change the work row before admission, so a concurrent re-claim cannot pass the same fence.
                var changed = await ExecuteAsync(
                    db,
                    tx,
                    "UPDATE cp_work SET status='SpendUncertain' WHERE scope=@scope AND id=@id AND status='Claimed' AND owner=@owner AND epoch=@epoch AND lease_until>@now AND EXISTS (SELECT 1 FROM cp_documents j WHERE j.scope=cp_work.scope AND j.kind='jobs' AND j.id=cp_work.job_id AND j.state='Queued') AND EXISTS (SELECT 1 FROM cp_documents p WHERE p.scope=cp_work.scope AND p.kind='worker-preflights' AND p.id=@permit AND p.state='Passed') AND (build_id NOT LIKE 'judge:%' OR NOT EXISTS (SELECT 1 FROM cp_attempts a WHERE a.scope=cp_work.scope AND a.work_id=cp_work.id))",
                    cancellationToken,
                    ("permit", PreflightId(workId, owner, epoch)),
                    ("scope", scope.Key),
                    ("id", workId),
                    ("owner", owner),
                    ("epoch", epoch),
                    ("now", now)
                );
                if (changed != 1)
                {
                    throw new ManagementException("claim_stale", 409);
                }
                await ExecuteAsync(
                    db,
                    tx,
                    "INSERT INTO cp_attempts(scope,id,work_id,epoch,owner,state,started_at) VALUES(@scope,@attempt,@work,@epoch,@owner,'Started',@now)",
                    cancellationToken,
                    ("scope", scope.Key),
                    ("attempt", attemptId),
                    ("work", workId),
                    ("epoch", epoch),
                    ("owner", owner),
                    ("now", now)
                );
                await EventAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    "attempt.started",
                    "work",
                    workId,
                    epoch,
                    new { attemptId },
                    cancellationToken
                );
                return true;
            },
            cancellationToken
        );

    /// <summary>Retains a known attempt's immutable receipt, including after its lease expires.</summary>
    public Task FinishAttemptAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string workId,
        string owner,
        long epoch,
        string attemptId,
        JsonElement receipt,
        CancellationToken cancellationToken = default
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                var body = ContentIdentity.Normalize(receipt);
                var hash = ContentIdentity.Hash(body);
                var changed = await ExecuteAsync(
                    db,
                    tx,
                    "UPDATE cp_attempts SET state='Finished',finished_at=@now,receipt_hash=@hash,receipt=@receipt WHERE scope=@scope AND id=@attempt AND work_id=@work AND owner=@owner AND epoch=@epoch AND state='Started'",
                    cancellationToken,
                    ("scope", scope.Key),
                    ("attempt", attemptId),
                    ("work", workId),
                    ("owner", owner),
                    ("epoch", epoch),
                    ("now", Timestamp(time.GetUtcNow())),
                    ("hash", hash),
                    ("receipt", body)
                );
                if (changed == 0)
                {
                    await using var existing = Command(
                        db,
                        tx,
                        "SELECT receipt_hash FROM cp_attempts WHERE scope=@scope AND id=@attempt AND work_id=@work AND owner=@owner AND epoch=@epoch AND state='Finished'",
                        ("scope", scope.Key),
                        ("attempt", attemptId),
                        ("work", workId),
                        ("owner", owner),
                        ("epoch", epoch)
                    );
                    if ((await existing.ExecuteScalarAsync(cancellationToken)) as string != hash)
                    {
                        throw new ManagementException("receipt_conflict", 409);
                    }
                    return true;
                }
                await ExecuteAsync(
                    db,
                    tx,
                    "UPDATE cp_work SET status=CASE WHEN EXISTS (SELECT 1 FROM cp_documents j WHERE j.scope=cp_work.scope AND j.kind='jobs' AND j.id=cp_work.job_id AND j.state='Queued') THEN 'Claimed' ELSE 'Held' END WHERE scope=@scope AND id=@work AND owner=@owner AND epoch=@epoch AND status='SpendUncertain'",
                    cancellationToken,
                    ("scope", scope.Key),
                    ("work", workId),
                    ("owner", owner),
                    ("epoch", epoch)
                );
                await EventAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    "attempt.finished",
                    "work",
                    workId,
                    epoch,
                    new { attemptId, receiptHash = hash },
                    cancellationToken
                );
                return true;
            },
            cancellationToken
        );
}
