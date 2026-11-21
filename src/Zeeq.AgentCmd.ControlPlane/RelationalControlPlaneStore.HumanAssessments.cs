using System.Text.Json;

namespace Zeeq.AgentCmd.ControlPlane;

/// <summary>Human rubric assessment of a retained exact execution.</summary>
/// <param name="ExecutionId">Stored execution identity.</param>
/// <param name="CriterionId">Frozen human criterion.</param>
/// <param name="Score">Reviewer-assigned normalized score.</param>
/// <param name="Rationale">Reviewer explanation.</param>
/// <param name="AssessmentJobId">Optional reassessment run retaining this execution.</param>
public sealed record HumanAssessmentRequest(
    string ExecutionId,
    string CriterionId,
    double Score,
    string Rationale,
    string? AssessmentJobId = null
);

public sealed partial class RelationalControlPlaneStore
{
    /// <summary>Records a reviewer-authored rubric assessment without impersonating a ledger GradeEvent.</summary>
    public Task<ManagementResource> AssessHumanAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        HumanAssessmentRequest request,
        CancellationToken cancellationToken = default
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                if (
                    !double.IsFinite(request.Score)
                    || request.Score is < 0 or > 1
                    || string.IsNullOrWhiteSpace(request.Rationale)
                )
                {
                    throw new ManagementException("human_assessment_invalid");
                }
                var execution = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "executions",
                    request.ExecutionId,
                    cancellationToken
                ) ?? throw new ManagementException("execution_not_found", 404);
                var sourceId = Text(execution.Body, "jobId")
                    ?? throw new ManagementException("execution_invalid");
                var jobId = request.AssessmentJobId ?? sourceId;
                var job = await ReadAsync(db, tx, scope, "jobs", jobId, cancellationToken)
                    ?? throw new ManagementException("job_not_found", 404);
                if (
                    request.AssessmentJobId is not null
                    && (
                        Text(job.Body, "kind") != "Reassess"
                        || Text(job.Body, "sourceJobId") != sourceId
                        || !job.Body.TryGetProperty("executionIds", out var ids)
                        || ids.ValueKind != JsonValueKind.Array
                        || !ids.EnumerateArray().Any(id => id.GetString() == execution.Id)
                    )
                )
                {
                    throw new ManagementException("reassessment_execution_mismatch", 409);
                }
                var dataset = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "datasets",
                    Text(job.Body, "datasetId")!,
                    cancellationToken
                ) ?? throw new ManagementException("dataset_not_found", 404);
                var benchmark = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "benchmarks",
                    Text(job.Body, "benchmarkId")!,
                    cancellationToken
                ) ?? throw new ManagementException("benchmark_not_found", 404);
                if (
                    dataset.State != "Frozen"
                    || benchmark.State != "Frozen"
                    || dataset.ContentHash != Text(job.Body, "datasetHash")
                    || benchmark.ContentHash != Text(job.Body, "benchmarkHash")
                )
                {
                    throw new ManagementException("assessment_pin_mismatch", 409);
                }
                var criterion = FindById(benchmark.Body, "criteria", request.CriterionId);
                if (
                    !criterion.TryGetProperty("scorer", out var scorer)
                    || (
                        Text(scorer, "kind") != "Human"
                        && !(
                            Text(scorer, "kind") == "Llm"
                            && Text(dataset.Body, "purpose") == "Calibration"
                        )
                    )
                )
                {
                    throw new ManagementException("scorer_unsupported", 409);
                }
                var pointer = await ResolveOutputBindingAsync(
                    db,
                    tx,
                    scope,
                    criterion,
                    execution,
                    cancellationToken
                );
                var outcome = "Scored";
                JsonElement? outputRef = null;
                if (Text(execution.Body, "outcome") != "Success")
                {
                    outcome = "TaskFailure";
                }
                else if (
                    Text(execution.Body, "storageStatus") != "Stored"
                    || !execution.Body.TryGetProperty("outputRef", out var storedRef)
                    || !execution.Body.TryGetProperty("value", out var output)
                )
                {
                    outcome = "NoStoredOutput";
                }
                else
                {
                    outputRef = storedRef.Clone();
                    if (pointer is null)
                    {
                        outcome = "Unmapped";
                    }
                    else if (!JsonPointer.TryGet(output, pointer, out _))
                    {
                        outcome = "TargetMissing";
                    }
                }
                var body = ContentIdentity.Normalize(
                    JsonSerializer.SerializeToElement(
                        new
                        {
                            jobId,
                            executionId = execution.Id,
                            candidateId = Text(execution.Body, "candidateId"),
                            comparisonEligible = execution.Body.TryGetProperty(
                                "comparisonEligible",
                                out var eligible
                            )
                                && eligible.ValueKind == JsonValueKind.True,
                            caseId = Text(execution.Body, "caseId"),
                            criterionId = request.CriterionId,
                            criterionVersion = criterion.TryGetProperty("version", out var version)
                                ? version.GetInt32()
                                : 0,
                            evaluator = new
                            {
                                kind = "Human",
                                version = Text(scorer, "version"),
                                reviewer = actor.Subject,
                            },
                            outcome,
                            score = outcome == "Scored" ? (double?)request.Score : null,
                            request.Rationale,
                            targetPointer = pointer,
                            outputRef,
                            datasetHash = dataset.ContentHash,
                            benchmarkHash = benchmark.ContentHash,
                        },
                        Json
                    )
                );
                return await InsertAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    "assessments",
                    Guid.NewGuid().ToString("N"),
                    request.CriterionId,
                    outcome,
                    body,
                    cancellationToken
                );
            },
            cancellationToken
        );
}
