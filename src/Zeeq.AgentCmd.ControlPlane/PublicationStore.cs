using System.Text.Json;

namespace Zeeq.AgentCmd.ControlPlane;

public sealed partial class RelationalControlPlaneStore
{
    /// <summary>Saves a manual decision over exact evidence; it never asserts that automated acceptance passed.</summary>
    public Task<ManagementResource> SavePublicationDecisionAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        PublicationTargetProfile profile,
        PublicationDecisionRequest request,
        CancellationToken cancellationToken = default
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                if (
                    profile.Scope != scope
                    || profile.Id != request.TargetId
                    || !profile.AllowManualDecisions
                )
                {
                    throw new ManagementException("manual_publication_decision_not_permitted", 403);
                }
                if (
                    request.Action
                        is not (
                            "Activate"
                            or "ChangeAllocation"
                            or "ChangeControl"
                            or "Rollback"
                            or "RestoreConfiguredDefaults"
                        )
                    || request.Verdict is not ("Pass" or "Fail" or "Inconclusive")
                    || string.IsNullOrWhiteSpace(request.Rationale)
                    || request.Rationale.Length > 4000
                )
                {
                    throw new ManagementException("publication_decision_invalid");
                }
                var publication = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "publications",
                    request.PublicationId,
                    cancellationToken
                ) ?? throw new ManagementException("publication_not_found", 404);
                RequirePublication(
                    publication,
                    request.PublicationRevision,
                    request.PublicationHash,
                    request.BuildId
                );
                if (profile.BuildId != request.BuildId)
                {
                    throw new ManagementException("publication_target_build_mismatch", 409);
                }
                var snapshot = PublicationCodec.Snapshot(PublicationCodec.Content(publication));
                var registeredBuild = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "builds",
                    request.BuildId,
                    cancellationToken
                ) ?? throw new ManagementException("build_not_found", 404);
                if (
                    snapshot.Commands.Keys.Any(id =>
                        !MatchesBuild(registeredBuild.Body, publication.Body, id)
                    )
                )
                {
                    throw new ManagementException("publication_contract_mismatch", 409);
                }
                var restoring = request.Action == "RestoreConfiguredDefaults";
                if (
                    restoring != (snapshot.Commands.Count == 0)
                    || !restoring && request.EvidencePublicationId is not null
                )
                {
                    throw new ManagementException("publication_action_content_mismatch");
                }
                var evidencePublication = publication;
                if (restoring)
                {
                    evidencePublication = await ReadAsync(
                        db,
                        tx,
                        scope,
                        "publications",
                        request.EvidencePublicationId
                            ?? throw new ManagementException(
                                "restore_evidence_publication_required"
                            ),
                        cancellationToken
                    ) ?? throw new ManagementException("publication_not_found", 404);
                    if (
                        evidencePublication.State != "Frozen"
                        || Text(evidencePublication.Body, "buildId") != request.BuildId
                    )
                    {
                        throw new ManagementException("publication_report_candidate_mismatch", 409);
                    }
                }
                var comparison = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "comparisons",
                    request.ComparisonId,
                    cancellationToken
                ) ?? throw new ManagementException("comparison_not_found", 404);
                var benchmarkHash = Text(comparison.Body, "benchmarkHash");
                var datasetHash = Text(comparison.Body, "datasetHash");
                if (
                    comparison.State != "Saved"
                    || string.IsNullOrWhiteSpace(benchmarkHash)
                    || string.IsNullOrWhiteSpace(datasetHash)
                    || !comparison.Body.TryGetProperty("assessmentIds", out var ids)
                    || ids.ValueKind != JsonValueKind.Array
                )
                {
                    throw new ManagementException("publication_report_invalid", 409);
                }
                var matched = false;
                foreach (var id in ids.EnumerateArray())
                {
                    var assessment = await ReadAsync(
                        db,
                        tx,
                        scope,
                        "assessments",
                        id.GetString()!,
                        cancellationToken
                    ) ?? throw new ManagementException("assessment_not_found", 404);
                    if (Text(assessment.Body, "candidateId") != request.CandidateId)
                    {
                        continue;
                    }
                    var jobId = Text(assessment.Body, "jobId")
                        ?? throw new ManagementException("publication_report_invalid");
                    var job = await ReadAsync(db, tx, scope, "jobs", jobId, cancellationToken)
                        ?? throw new ManagementException("job_not_found", 404);
                    if (
                        Text(job.Body, "benchmarkHash") != benchmarkHash
                        || Text(job.Body, "datasetHash") != datasetHash
                        || Text(assessment.Body, "benchmarkHash") != benchmarkHash
                        || Text(assessment.Body, "datasetHash") != datasetHash
                    )
                    {
                        throw new ManagementException("publication_report_protocol_mismatch", 409);
                    }
                    var benchmark = await ReadAsync(
                        db,
                        tx,
                        scope,
                        "benchmarks",
                        Text(job.Body, "benchmarkId")!,
                        cancellationToken
                    );
                    var dataset = await ReadAsync(
                        db,
                        tx,
                        scope,
                        "datasets",
                        Text(job.Body, "datasetId")!,
                        cancellationToken
                    );
                    if (
                        benchmark?.State != "Frozen"
                        || benchmark.ContentHash != benchmarkHash
                        || dataset?.State != "Frozen"
                        || dataset.ContentHash != datasetHash
                    )
                    {
                        throw new ManagementException("publication_report_protocol_mismatch", 409);
                    }
                    // A reassessment has new evaluation pins but retains the original candidate generation pins.
                    var generationJob = job;
                    if (Text(job.Body, "sourceJobId") is { } sourceJob)
                    {
                        generationJob = await ReadAsync(
                            db,
                            tx,
                            scope,
                            "jobs",
                            sourceJob,
                            cancellationToken
                        ) ?? throw new ManagementException("job_not_found", 404);
                    }
                    if (
                        !generationJob.Body.TryGetProperty("pins", out var pins)
                        || pins.ValueKind != JsonValueKind.Array
                        || !pins.EnumerateArray()
                            .Any(pin =>
                                pin.TryGetProperty("candidate", out var candidate)
                                && Text(candidate, "id") == request.CandidateId
                                && Text(candidate, "publicationId") == evidencePublication.Id
                                && Text(candidate, "buildId") == request.BuildId
                                && Text(pin, "publicationHash") == evidencePublication.ContentHash
                            )
                    )
                    {
                        throw new ManagementException("publication_report_candidate_mismatch", 409);
                    }
                    matched = true;
                }
                if (!matched)
                {
                    throw new ManagementException("publication_report_candidate_missing", 409);
                }
                var body = ContentIdentity.Normalize(
                    JsonSerializer.SerializeToElement(
                        new
                        {
                            request.TargetId,
                            request.PublicationId,
                            request.PublicationRevision,
                            request.PublicationHash,
                            request.BuildId,
                            request.Action,
                            request.Verdict,
                            request.ComparisonId,
                            request.CandidateId,
                            request.Rationale,
                            author = actor.Subject,
                            decisionKind = "Manual",
                            automatedAcceptance = "NotAsserted",
                            comparisonHash = comparison.ContentHash,
                            benchmarkHash,
                            datasetHash,
                            evidencePublicationId = evidencePublication.Id,
                            evidencePublicationHash = evidencePublication.ContentHash,
                            bindingId = profile.BindingId,
                            bindingRevision = profile.BindingRevision,
                        },
                        Json
                    )
                );
                return await InsertAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    "decisions",
                    Guid.NewGuid().ToString("N"),
                    request.Action + " decision",
                    "Saved",
                    body,
                    cancellationToken
                );
            },
            cancellationToken
        );

    /// <summary>Persists reviewed exact pins and the target's observed precondition before any external write.</summary>
    internal Task<ManagementResource> SavePublicationOperationAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        PublicationTargetProfile profile,
        PublicationReviewRequest request,
        PublicationPointer? expected,
        CancellationToken cancellationToken
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                if (
                    !request.WholePartitionReplacementReviewed
                    || profile.Scope != scope
                    || !profile.AllowManualDecisions
                )
                {
                    throw new ManagementException("publication_review_required", 409);
                }
                var decision = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "decisions",
                    request.DecisionId,
                    cancellationToken
                ) ?? throw new ManagementException("decision_not_found", 404);
                if (
                    decision.State != "Saved"
                    || decision.ContentHash != request.DecisionHash
                    || Text(decision.Body, "verdict") != "Pass"
                    || Text(decision.Body, "targetId") != profile.Id
                    || Text(decision.Body, "bindingId") != profile.BindingId
                    || Text(decision.Body, "bindingRevision") != profile.BindingRevision
                    || Text(decision.Body, "buildId") != profile.BuildId
                )
                {
                    throw new ManagementException("publication_decision_mismatch", 409);
                }
                var publication = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "publications",
                    Text(decision.Body, "publicationId")!,
                    cancellationToken
                ) ?? throw new ManagementException("publication_not_found", 404);
                RequirePublication(
                    publication,
                    decision.Body.GetProperty("publicationRevision").GetInt64(),
                    Text(decision.Body, "publicationHash")!,
                    profile.BuildId
                );
                var operation = new PublicationOperation(
                    profile,
                    decision.Id,
                    decision.ContentHash,
                    publication.Id,
                    publication.Revision,
                    Text(decision.Body, "action")!,
                    PublicationCodec.Content(publication),
                    expected
                );
                // InsertAsync atomically appends the audit event and Pending outbox intent.
                return await InsertAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    "publication-operations",
                    Guid.NewGuid().ToString("N"),
                    operation.Action + " publication",
                    "Reviewed",
                    ContentIdentity.Normalize(JsonSerializer.SerializeToElement(operation, Json)),
                    cancellationToken
                );
            },
            cancellationToken
        );

    /// <summary>Claims a reviewed operation once; unknown or in-flight work cannot be resent.</summary>
    internal Task<ManagementResource> ClaimPublicationOperationAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string id,
        long expectedRevision,
        PublicationTargetProfile profile,
        CancellationToken cancellationToken
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                var resource = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "publication-operations",
                    id,
                    cancellationToken
                ) ?? throw new ManagementException("publication_operation_not_found", 404);
                RequireRevision(resource, expectedRevision);
                var operation = Operation(resource);
                if (resource.State != "Reviewed" || operation.Profile != profile)
                {
                    throw new ManagementException("publication_operation_not_deliverable", 409);
                }
                return await ReplaceAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    resource,
                    resource.Name,
                    "Delivering",
                    ContentIdentity.Normalize(resource.Body),
                    "publication.delivery_started",
                    cancellationToken
                );
            },
            cancellationToken
        );

    internal Task<ManagementResource> FinishPublicationOperationAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        ManagementResource resource,
        string state,
        PublicationPointer? observed,
        CancellationToken cancellationToken
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                var current = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "publication-operations",
                    resource.Id,
                    cancellationToken
                ) ?? throw new ManagementException("publication_operation_not_found", 404);
                RequireRevision(current, resource.Revision);
                var operation = Operation(current) with { Observed = observed };
                var result = await ReplaceAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    current,
                    current.Name,
                    state,
                    ContentIdentity.Normalize(JsonSerializer.SerializeToElement(operation, Json)),
                    "publication." + state.ToLowerInvariant(),
                    cancellationToken
                );
                // The operation remains the durable authority. Outbox rows are receipts, never a resend queue.
                await ExecuteAsync(
                    db,
                    tx,
                    "UPDATE cp_outbox SET state=@state,receipt=@receipt WHERE scope=@scope AND resource_id=@id AND kind LIKE 'publication%'",
                    cancellationToken,
                    ("state", state),
                    ("receipt", JsonSerializer.Serialize(new { state, observed }, Json)),
                    ("scope", scope.Key),
                    ("id", resource.Id)
                );
                return result;
            },
            cancellationToken
        );

    /// <summary>Stores an authenticated host report; late sequence numbers cannot replace newer evidence.</summary>
    public Task<ManagementResource> ReportPublicationHostAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        PublicationTargetProfile profile,
        PublicationHostReport request,
        CancellationToken cancellationToken = default
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                if (
                    profile.Scope != scope
                    || request.TargetId != profile.Id
                    || string.IsNullOrWhiteSpace(request.BindingId)
                    || string.IsNullOrWhiteSpace(request.BindingRevision)
                    || request.Sequence < 1
                    || string.IsNullOrWhiteSpace(request.BuildId)
                    || string.IsNullOrWhiteSpace(actor.Subject)
                    || (request.PublicationRevision is null) != (request.PublicationHash is null)
                    || request.PublicationRevision is not null
                        && (
                            !ExperimentRevision.TryCreate(request.PublicationRevision, out _)
                            || request.PublicationHash!.Length != 64
                            || request.PublicationHash.Any(c => !char.IsAsciiHexDigit(c))
                        )
                )
                {
                    throw new ManagementException("host_report_invalid");
                }
                _ = await ReadAsync(db, tx, scope, "builds", request.BuildId, cancellationToken)
                    ?? throw new ManagementException("build_not_found", 404);
                var id = ContentIdentity.Hash(
                    JsonSerializer.Serialize(new[] { actor.Subject, profile.Id })
                );
                var existing = await ReadAsync(db, tx, scope, "hosts", id, cancellationToken);
                var body = ContentIdentity.Normalize(
                    JsonSerializer.SerializeToElement(
                        new { hostId = actor.Subject, report = request },
                        Json
                    )
                );
                if (existing is not null)
                {
                    var prior = existing
                        .Body.GetProperty("report")
                        .Deserialize<PublicationHostReport>(Json)!;
                    if (request.Sequence <= prior.Sequence)
                    {
                        if (
                            request.Sequence == prior.Sequence
                            && existing.ContentHash == ContentIdentity.Hash(body)
                        )
                        {
                            return existing;
                        }
                        throw new ManagementException("host_report_stale", 409);
                    }
                    return await ReplaceAsync(
                        db,
                        tx,
                        scope,
                        actor,
                        existing,
                        existing.Name,
                        "Reported",
                        body,
                        "host.reported",
                        cancellationToken
                    );
                }
                return await InsertAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    "hosts",
                    id,
                    "Host report",
                    "Reported",
                    body,
                    cancellationToken
                );
            },
            cancellationToken
        );

    /// <summary>Shows only reported-host adoption; absent or stale reports remain unknown.</summary>
    public async Task<PublicationAdoption> ReadPublicationAdoptionAsync(
        WorkspaceScope scope,
        string operationId,
        CancellationToken cancellationToken = default
    )
    {
        var resource = await ReadAsync(
            scope,
            "publication-operations",
            operationId,
            cancellationToken
        ) ?? throw new ManagementException("publication_operation_not_found", 404);
        var operation = Operation(resource);
        var reports = await ListAsync(scope, "hosts", cancellationToken);
        var cutoff = time.GetUtcNow() - TimeSpan.FromMinutes(5);
        var statuses = new List<PublicationHostStatus>();
        foreach (var stored in reports)
        {
            var report = stored
                .Body.GetProperty("report")
                .Deserialize<PublicationHostReport>(Json)!;
            if (report.TargetId != operation.Profile.Id)
            {
                continue;
            }
            var state =
                stored.UpdatedAt < cutoff || report.PublicationRevision is null ? "Unknown"
                : report.BuildId == operation.Profile.BuildId
                && report.BindingId == operation.Profile.BindingId
                && report.BindingRevision == operation.Profile.BindingRevision
                && report.PublicationRevision == operation.Content.Revision
                && report.PublicationHash == operation.Content.ContentHash
                    ? "Reported"
                : "OtherPublication";
            statuses.Add(new(Text(stored.Body, "hostId")!, state, stored.UpdatedAt, report));
        }
        return new(
            statuses.Any(s => s.State != "Unknown") ? "ReportingHostsOnly" : "Unknown",
            statuses
        );
    }

    internal static PublicationOperation Operation(ManagementResource resource) =>
        resource.Body.Deserialize<PublicationOperation>(Json)
        ?? throw new ManagementException("publication_operation_invalid", 409);

    private static void RequirePublication(
        ManagementResource resource,
        long revision,
        string hash,
        string buildId
    )
    {
        if (
            resource.State != "Frozen"
            || resource.Revision != revision
            || resource.ContentHash != hash
            || Text(resource.Body, "buildId") != buildId
        )
        {
            throw new ManagementException("publication_pin_mismatch", 409);
        }
    }
}
