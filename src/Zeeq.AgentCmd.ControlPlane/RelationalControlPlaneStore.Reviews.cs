using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zeeq.AgentCmd.ControlPlane;

/// <summary>A reviewer decision with an explanation and optional draft destination.</summary>
/// <param name="Decision">Reject or NeedsEvidence; accepted draft edits need adjudication support.</param>
/// <param name="Note">Reviewer rationale.</param>
/// <param name="DatasetDraftId">Explicit destination for a future accepted reference.</param>
public sealed record ReviewDecision(string Decision, string Note, string? DatasetDraftId = null);

public sealed partial class RelationalControlPlaneStore
{
    /// <summary>Creates an owned review task linked to an existing scoped execution or assessment.</summary>
    public Task<ManagementResource> CreateReviewAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        SaveResource request,
        CancellationToken cancellationToken = default
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                if (Text(request.Body, "kind") is not ("Grading" or "ReferenceGap" or "JudgeAudit"))
                {
                    throw new ManagementException("review_kind_invalid");
                }
                var executionId = Text(request.Body, "executionId");
                var assessmentId = Text(request.Body, "assessmentId");
                if (executionId is null && assessmentId is null)
                {
                    throw new ManagementException("review_evidence_required");
                }
                ManagementResource? assessment = null;
                if (assessmentId is not null)
                {
                    assessment = await ReadAsync(
                        db,
                        tx,
                        scope,
                        "assessments",
                        assessmentId,
                        cancellationToken
                    ) ?? throw new ManagementException("review_evidence_not_found", 404);
                    var assessedExecution = Text(assessment.Body, "executionId");
                    if (executionId is not null && assessedExecution != executionId)
                    {
                        throw new ManagementException("review_evidence_mismatch", 409);
                    }
                    executionId ??= assessedExecution;
                }
                var execution = executionId is null
                    ? null
                    : await ReadAsync(db, tx, scope, "executions", executionId, cancellationToken);
                if (execution is null)
                {
                    throw new ManagementException("review_evidence_not_found", 404);
                }
                var requestedCase = Text(request.Body, "caseId");
                var requestedDataset = Text(request.Body, "datasetId");
                if (requestedCase is not null && requestedCase != Text(execution.Body, "caseId"))
                {
                    throw new ManagementException("review_evidence_mismatch", 409);
                }
                if (requestedDataset is not null)
                {
                    var job = await ReadAsync(
                        db,
                        tx,
                        scope,
                        "jobs",
                        Text(execution.Body, "jobId")!,
                        cancellationToken
                    ) ?? throw new ManagementException("review_evidence_not_found", 404);
                    if (requestedDataset != Text(job.Body, "datasetId"))
                    {
                        throw new ManagementException("review_evidence_mismatch", 409);
                    }
                }
                var body = JsonNode.Parse(ContentIdentity.Normalize(request.Body))!.AsObject();
                body["executionId"] = executionId;
                body["status"] = "Ready";
                body.Remove("assignee");
                return await InsertAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    "reviews",
                    Guid.NewGuid().ToString("N"),
                    request.Name,
                    "Ready",
                    ContentIdentity.Normalize(JsonSerializer.SerializeToElement(body)),
                    cancellationToken
                );
            },
            cancellationToken
        );

    /// <summary>Claims an unowned review with an optimistic revision and authenticated actor.</summary>
    public Task<ManagementResource> ClaimReviewAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string id,
        long revision,
        CancellationToken cancellationToken = default
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                var resource = await ReadAsync(db, tx, scope, "reviews", id, cancellationToken)
                    ?? throw new ManagementException("not_found", 404);
                RequireRevision(resource, revision);
                if (resource.State != "Ready")
                {
                    throw new ManagementException("review_not_claimable", 409);
                }
                var body = JsonNode.Parse(resource.Body.GetRawText())!.AsObject();
                body["status"] = "Claimed";
                body["assignee"] = actor.Subject;
                return await ReplaceAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    resource,
                    resource.Name,
                    "Claimed",
                    ContentIdentity.Normalize(JsonSerializer.SerializeToElement(body)),
                    "review.claimed",
                    cancellationToken
                );
            },
            cancellationToken
        );

    /// <summary>Records a claimed review decision without changing frozen evidence.</summary>
    public Task<ManagementResource> DecideReviewAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string id,
        long revision,
        ReviewDecision decision,
        CancellationToken cancellationToken = default
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                var resource = await ReadAsync(db, tx, scope, "reviews", id, cancellationToken)
                    ?? throw new ManagementException("not_found", 404);
                RequireRevision(resource, revision);
                if (resource.State != "Claimed" || Text(resource.Body, "assignee") != actor.Subject)
                {
                    throw new ManagementException("review_not_owned", 409);
                }
                if (
                    string.IsNullOrWhiteSpace(decision.Note)
                    || decision.Decision is not ("Reject" or "NeedsEvidence")
                )
                {
                    throw new ManagementException(
                        decision.Decision == "Accept"
                            ? "reference_adjudication_not_supported"
                            : "review_decision_invalid",
                        409
                    );
                }
                var status = decision.Decision == "Reject" ? "Rejected" : "NeedsEvidence";
                var body = JsonNode.Parse(resource.Body.GetRawText())!.AsObject();
                body["status"] = status;
                body["note"] = decision.Note;
                return await ReplaceAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    resource,
                    resource.Name,
                    status,
                    ContentIdentity.Normalize(JsonSerializer.SerializeToElement(body)),
                    "review.decided",
                    cancellationToken
                );
            },
            cancellationToken
        );
}
