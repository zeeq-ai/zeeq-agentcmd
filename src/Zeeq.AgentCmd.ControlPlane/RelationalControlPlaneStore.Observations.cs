using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zeeq.AgentCmd.ControlPlane;

/// <summary>Normalized host observation; its presence is evidence, while its absence never proves that no provider send occurred.</summary>
/// <param name="AttemptId">Original provider-attempt identity, used once for accounting.</param>
/// <param name="Outcome">Reported outcome category.</param>
/// <param name="Provider">Provider registration when known.</param>
/// <param name="RequestedModel">Requested route model when known.</param>
/// <param name="ReportedModel">Provider-reported model when known.</param>
/// <param name="InputTokens">Reported usage, or unknown.</param>
/// <param name="OutputTokens">Reported usage, or unknown.</param>
/// <param name="DurationSeconds">Captured attempt timing, separate from case elapsed.</param>
public sealed record ObservedAttempt(
    Guid AttemptId,
    string Outcome,
    string? Provider = null,
    string? RequestedModel = null,
    string? ReportedModel = null,
    long? InputTokens = null,
    long? OutputTokens = null,
    double? DurationSeconds = null
);

/// <summary>Trusted application report of an original execution; retained input is explicit and must follow the host capture policy.</summary>
/// <param name="ExecutionId">Original execution identity.</param>
/// <param name="Purpose">Production, Shadow, or Unknown; role and experiment identifiers cannot establish origin.</param>
/// <param name="BuildId">Registered build reported by the authenticated host.</param>
/// <param name="CommandId">Original command identifier.</param>
/// <param name="Outcome">Success, Failed, or Cancelled.</param>
/// <param name="StorageStatus">Stored or Unavailable.</param>
/// <param name="OutputRef">Exact original command or sealed aggregate reference, when stored.</param>
/// <param name="Value">Exact retained output, when stored.</param>
/// <param name="Input">Authorized frozen typed input, or absent when not retained.</param>
/// <param name="SourceFamilyId">Stable document/source lineage, required for curation.</param>
/// <param name="AssetHashes">Authorized retained immutable source assets.</param>
/// <param name="Trace">Captured trace anchor, independent of span availability.</param>
/// <param name="CaseElapsedSeconds">Actual host-captured case elapsed, never a sum of attempts.</param>
/// <param name="ExperimentEvidence">Original assignment/fallback evidence, or unknown.</param>
/// <param name="Attempts">Normalized observations, not a durable send journal.</param>
public sealed record ExecutionObservation(
    Guid ExecutionId,
    string Purpose,
    string BuildId,
    string CommandId,
    string Outcome,
    string StorageStatus,
    JsonElement? OutputRef = null,
    JsonElement? Value = null,
    JsonElement? Input = null,
    string? SourceFamilyId = null,
    IReadOnlyList<string>? AssetHashes = null,
    TraceAnchor? Trace = null,
    double? CaseElapsedSeconds = null,
    JsonElement? ExperimentEvidence = null,
    IReadOnlyList<ObservedAttempt>? Attempts = null
);

/// <summary>Incident capture names a draft and its observed revision; it cannot replace frozen cases or create a reference answer.</summary>
/// <param name="DatasetDraftId">Scoped regression draft.</param>
/// <param name="DatasetRevision">Observed draft revision.</param>
/// <param name="Reason">Human explanation retained with the review task.</param>
public sealed record CaptureIncidentRequest(
    string DatasetDraftId,
    long DatasetRevision,
    string Reason
);

/// <summary>Draft evidence and owned task produced by capture; acceptance remains a separate review.</summary>
/// <param name="Dataset">Updated draft.</param>
/// <param name="Review">Owned review task.</param>
/// <param name="CaseId">Captured or duplicate case.</param>
/// <param name="AlreadyPresent">Whether matching evidence already existed.</param>
public sealed record IncidentCapture(
    ManagementResource Dataset,
    ManagementResource Review,
    string CaseId,
    bool AlreadyPresent
);

public sealed partial class RelationalControlPlaneStore
{
    private static string IncidentEvidenceHash(JsonElement item) =>
        ContentIdentity.Hash(
            ContentIdentity.Normalize(
                JsonSerializer.SerializeToElement(
                    new
                    {
                        sourceFamilyId = Text(item, "sourceFamilyId"),
                        input = item.TryGetProperty("input", out var input)
                            ? input
                            : Element("null"),
                        assets = item.TryGetProperty("assets", out var assets)
                            ? assets
                            : Element("[]"),
                    }
                )
            )
        );

    /// <summary>Idempotently indexes authenticated host observations without interpreting best-effort events as send authority.</summary>
    public Task<ManagementResource> RecordObservationAsync(
        WorkspaceScope scope,
        ManagementActor host,
        ExecutionObservation report,
        CancellationToken cancellationToken = default
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                if (
                    report.ExecutionId == Guid.Empty
                    || report.Purpose is not ("Production" or "Shadow" or "Unknown")
                    || report.Outcome is not ("Success" or "Failed" or "Cancelled")
                    || report.StorageStatus is not ("Stored" or "Unavailable")
                    || string.IsNullOrWhiteSpace(report.CommandId)
                    || report.CaseElapsedSeconds is { } elapsed
                        && (!double.IsFinite(elapsed) || elapsed < 0)
                    || report.Input is { } input && input.ValueKind != JsonValueKind.Object
                )
                {
                    throw new ManagementException("execution_observation_invalid");
                }
                _ = await ReadAsync(db, tx, scope, "builds", report.BuildId, cancellationToken)
                    ?? throw new ManagementException("build_not_found", 404);
                if (
                    report.StorageStatus == "Stored"
                        ? report.Outcome != "Success"
                            || report.OutputRef is not { } reference
                            || !ValidOutputReference(reference, report.ExecutionId)
                            || report.Value is null
                        : report.OutputRef is not null || report.Value is not null
                )
                {
                    throw new ManagementException("execution_observation_output_invalid");
                }
                if (
                    report.Attempts is { } attempts
                    && (
                        attempts.Count > 1000
                        || attempts.Select(item => item.AttemptId).Distinct().Count()
                            != attempts.Count
                        || attempts.Any(item =>
                            item.AttemptId == Guid.Empty
                            || item.InputTokens < 0
                            || item.OutputTokens < 0
                            || item.DurationSeconds is { } duration
                                && (!double.IsFinite(duration) || duration < 0)
                        )
                    )
                )
                {
                    throw new ManagementException("execution_observation_attempt_invalid");
                }
                foreach (var hash in report.AssetHashes ?? [])
                {
                    if (Assets is null)
                    {
                        throw new ManagementException("asset_store_unavailable", 409);
                    }
                    await using var asset = await Assets.OpenAsync(scope, hash, cancellationToken);
                }
                var node = JsonSerializer.SerializeToNode(report, Json)!.AsObject();
                node["reporter"] = host.Subject;
                node["originAuthority"] = "AuthenticatedHostReport";
                node["inputRetained"] = report.Input is not null;
                node["role"] = report.Purpose == "Shadow" ? "Shadow" : "Primary";
                node["comparisonEligible"] = false;
                if (report.StorageStatus != "Stored")
                {
                    node.Remove("outputRef");
                    node.Remove("value");
                }
                var body = ContentIdentity.Normalize(JsonSerializer.SerializeToElement(node));
                if (body.Length > 4 * 1024 * 1024)
                {
                    throw new ManagementException("content_too_large", 413);
                }
                var id = report.ExecutionId.ToString();
                var existing = await ReadAsync(db, tx, scope, "executions", id, cancellationToken);
                if (existing is not null)
                {
                    return existing.ContentHash == ContentIdentity.Hash(body)
                        ? existing
                        : throw new ManagementException("execution_observation_conflict", 409);
                }
                return await InsertAsync(
                    db,
                    tx,
                    scope,
                    host,
                    "executions",
                    id,
                    report.CommandId,
                    "Recorded",
                    body,
                    cancellationToken
                );
            },
            cancellationToken
        );

    /// <summary>Captures replay-ready original evidence into a regression draft and creates a review task in the same transaction.</summary>
    public Task<IncidentCapture> CaptureIncidentAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string executionId,
        CaptureIncidentRequest request,
        CancellationToken cancellationToken = default
    ) =>
        TransactionAsync<IncidentCapture>(
            async (db, tx) =>
            {
                if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 1000)
                {
                    throw new ManagementException("incident_reason_required");
                }
                var execution = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "executions",
                    executionId,
                    cancellationToken
                ) ?? throw new ManagementException("execution_not_found", 404);
                var draft = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "datasets",
                    request.DatasetDraftId,
                    cancellationToken
                ) ?? throw new ManagementException("dataset_not_found", 404);
                if (draft.State != "Draft" || Text(draft.Body, "purpose") != "Regression")
                {
                    throw new ManagementException("regression_draft_required", 409);
                }
                var reviewId = ContentIdentity.Hash(
                    JsonSerializer.Serialize(new[] { "incident", draft.Id, executionId })
                );
                var existing = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "reviews",
                    reviewId,
                    cancellationToken
                );
                if (existing is not null)
                {
                    return new(draft, existing, Text(existing.Body, "caseId")!, true);
                }
                RequireRevision(draft, request.DatasetRevision);
                JsonElement source;
                if (
                    execution.Body.TryGetProperty("inputRetained", out var retained)
                    && retained.ValueKind == JsonValueKind.True
                    && execution.Body.TryGetProperty("input", out var input)
                    && input.ValueKind == JsonValueKind.Object
                )
                {
                    source = JsonSerializer.SerializeToElement(
                        new
                        {
                            input,
                            sourceFamilyId = Text(execution.Body, "sourceFamilyId"),
                            assets = execution.Body.TryGetProperty("assetHashes", out var hashes)
                                ? hashes
                                : Element("[]"),
                        }
                    );
                }
                else if (Text(execution.Body, "jobId") is { } jobId)
                {
                    var job = await ReadAsync(db, tx, scope, "jobs", jobId, cancellationToken)
                        ?? throw new ManagementException("incident_input_not_retained", 409);
                    var dataset = await ReadAsync(
                        db,
                        tx,
                        scope,
                        "datasets",
                        Text(job.Body, "datasetId")!,
                        cancellationToken
                    ) ?? throw new ManagementException("incident_input_not_retained", 409);
                    source = FindById(dataset.Body, "cases", Text(execution.Body, "caseId")!);
                    if (Text(dataset.Body, "purpose") == "Acceptance")
                    {
                        await using var exposed = Command(
                            db,
                            tx,
                            "SELECT 1 FROM cp_family_exposure WHERE scope=@scope AND family=@family LIMIT 1",
                            ("scope", scope.Key),
                            ("family", Text(source, "sourceFamilyId"))
                        );
                        if (await exposed.ExecuteScalarAsync(cancellationToken) is null)
                        {
                            throw new ManagementException("acceptance_case_reveal_required", 409);
                        }
                    }
                }
                else
                {
                    throw new ManagementException("incident_input_not_retained", 409);
                }
                var family = Text(source, "sourceFamilyId");
                if (
                    string.IsNullOrWhiteSpace(family)
                    || !source.TryGetProperty("input", out var frozenInput)
                    || frozenInput.ValueKind != JsonValueKind.Object
                )
                {
                    throw new ManagementException("incident_replay_evidence_missing", 409);
                }
                await using (
                    var protectedSource = Command(
                        db,
                        tx,
                        "SELECT 1 FROM cp_dataset_families WHERE scope=@scope AND family=@family AND purpose='Acceptance' AND NOT EXISTS (SELECT 1 FROM cp_family_exposure e WHERE e.scope=@scope AND e.family=@family) LIMIT 1",
                        ("scope", scope.Key),
                        ("family", family)
                    )
                )
                {
                    if (await protectedSource.ExecuteScalarAsync(cancellationToken) is not null)
                    {
                        throw new ManagementException("acceptance_case_reveal_required", 409);
                    }
                }
                var assetIds =
                    source.TryGetProperty("assets", out var assets)
                    && assets.ValueKind == JsonValueKind.Array
                        ? assets
                        : Element("[]");
                foreach (var assetId in assetIds.EnumerateArray())
                {
                    var hash =
                        assetId.ValueKind == JsonValueKind.String
                            ? assetId.GetString()
                            : Text(assetId, "hash");
                    if (Assets is null || string.IsNullOrWhiteSpace(hash))
                    {
                        throw new ManagementException("incident_asset_unavailable", 409);
                    }
                    await using var asset = await Assets.OpenAsync(scope, hash, cancellationToken);
                }
                var cases = DatasetCases(draft.Body);
                var duplicate = cases.FirstOrDefault(item =>
                    Text(item, "sourceFamilyId") == family
                    && item.TryGetProperty("input", out var previous)
                    && JsonElement.DeepEquals(previous, frozenInput)
                );
                var caseId =
                    duplicate.ValueKind == JsonValueKind.Undefined
                        ? "incident-" + execution.Id
                        : Text(duplicate, "id")!;
                var captured = new JsonObject
                {
                    ["id"] = caseId,
                    ["sourceFamilyId"] = family,
                    ["input"] = JsonNode.Parse(frozenInput.GetRawText()),
                    ["assets"] = JsonNode.Parse(assetIds.GetRawText()),
                    ["tags"] = new JsonArray("incident"),
                    ["reviewId"] = reviewId,
                    ["provenance"] = new JsonObject
                    {
                        ["executionId"] = execution.Id,
                        ["reason"] = request.Reason,
                        ["outputRef"] = execution.Body.TryGetProperty("outputRef", out var output)
                            ? JsonNode.Parse(output.GetRawText())
                            : null,
                        ["trace"] = execution.Body.TryGetProperty("trace", out var trace)
                            ? JsonNode.Parse(trace.GetRawText())
                            : null,
                    },
                };
                if (duplicate.ValueKind == JsonValueKind.Undefined)
                {
                    var body = JsonNode.Parse(draft.Body.GetRawText())!.AsObject();
                    body["cases"]!.AsArray().Add(captured);
                    draft = await ReplaceAsync(
                        db,
                        tx,
                        scope,
                        actor,
                        draft,
                        draft.Name,
                        "Draft",
                        ContentIdentity.Normalize(JsonSerializer.SerializeToElement(body)),
                        "incident.captured",
                        cancellationToken
                    );
                }
                var reviewBody = JsonSerializer.SerializeToElement(
                    new
                    {
                        kind = "Incident",
                        executionId = execution.Id,
                        datasetDraftId = draft.Id,
                        caseId,
                        reason = request.Reason,
                        status = "Ready",
                        caseEvidenceHash = IncidentEvidenceHash(
                            duplicate.ValueKind == JsonValueKind.Undefined
                                ? JsonSerializer.SerializeToElement(captured)
                                : duplicate
                        ),
                        caseInputHash = ContentIdentity.Hash(
                            ContentIdentity.Normalize(frozenInput)
                        ),
                        duplicate = duplicate.ValueKind != JsonValueKind.Undefined,
                    }
                );
                var review = await InsertAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    "reviews",
                    reviewId,
                    request.Reason[..Math.Min(200, request.Reason.Length)],
                    "Ready",
                    ContentIdentity.Normalize(reviewBody),
                    cancellationToken
                );
                return new(draft, review, caseId, duplicate.ValueKind != JsonValueKind.Undefined);
            },
            cancellationToken
        );

    /// <summary>Accepts a claimed incident into its existing draft; it does not mark model output correct or create a reference answer.</summary>
    public Task<ManagementResource> AcceptIncidentAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string reviewId,
        long reviewRevision,
        CancellationToken cancellationToken = default
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                var review = await ReadAsync(db, tx, scope, "reviews", reviewId, cancellationToken)
                    ?? throw new ManagementException("review_not_found", 404);
                RequireRevision(review, reviewRevision);
                if (
                    review.State != "Claimed"
                    || Text(review.Body, "assignee") != actor.Subject
                    || Text(review.Body, "kind") != "Incident"
                )
                {
                    throw new ManagementException("review_not_owned", 409);
                }
                var draft = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "datasets",
                    Text(review.Body, "datasetDraftId")!,
                    cancellationToken
                ) ?? throw new ManagementException("dataset_not_found", 404);
                var item = FindById(draft.Body, "cases", Text(review.Body, "caseId")!);
                if (
                    draft.State != "Draft"
                    || !item.TryGetProperty("input", out var input)
                    || ContentIdentity.Hash(ContentIdentity.Normalize(input))
                        != Text(review.Body, "caseInputHash")
                    || IncidentEvidenceHash(item) != Text(review.Body, "caseEvidenceHash")
                )
                {
                    throw new ManagementException("incident_draft_changed", 409);
                }
                var body = JsonNode.Parse(review.Body.GetRawText())!.AsObject();
                body["status"] = "AcceptedIntoDraft";
                return await ReplaceAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    review,
                    review.Name,
                    "AcceptedIntoDraft",
                    ContentIdentity.Normalize(JsonSerializer.SerializeToElement(body)),
                    "incident.accepted",
                    cancellationToken
                );
            },
            cancellationToken
        );

    /// <summary>Releases the current reviewer's claim without changing evidence or another person's review.</summary>
    public Task<ManagementResource> ReleaseReviewAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string reviewId,
        long reviewRevision,
        CancellationToken cancellationToken = default
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                var review = await ReadAsync(db, tx, scope, "reviews", reviewId, cancellationToken)
                    ?? throw new ManagementException("review_not_found", 404);
                RequireRevision(review, reviewRevision);
                if (review.State != "Claimed" || Text(review.Body, "assignee") != actor.Subject)
                {
                    throw new ManagementException("review_not_owned", 409);
                }
                var body = JsonNode.Parse(review.Body.GetRawText())!.AsObject();
                body.Remove("assignee");
                body["status"] = "Ready";
                return await ReplaceAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    review,
                    review.Name,
                    "Ready",
                    ContentIdentity.Normalize(JsonSerializer.SerializeToElement(body)),
                    "review.released",
                    cancellationToken
                );
            },
            cancellationToken
        );
}
