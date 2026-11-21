using System.Text.Json;

namespace Zeeq.AgentCmd.ControlPlane;

/// <summary>Identifies the stored execution and frozen criterion to assess.</summary>
/// <param name="ExecutionId">Exact retained execution.</param>
/// <param name="CriterionId">Criterion in the pinned benchmark.</param>
/// <param name="AssessmentJobId">Optional reassessment run that explicitly retained this execution.</param>
public sealed record DeterministicAssessmentRequest(
    string ExecutionId,
    string CriterionId,
    string? AssessmentJobId = null
);

public sealed partial class RelationalControlPlaneStore
{
    /// <summary>Assesses a retained output using an exact or numeric criterion, preserving non-scores.</summary>
    public Task<ManagementResource> AssessDeterministicAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        DeterministicAssessmentRequest request,
        CancellationToken cancellationToken = default
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                var execution = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "executions",
                    request.ExecutionId,
                    cancellationToken
                ) ?? throw new ManagementException("execution_not_found", 404);
                var sourceJobId = Text(execution.Body, "jobId")
                    ?? throw new ManagementException("execution_invalid");
                var jobId = request.AssessmentJobId ?? sourceJobId;
                var job = await ReadAsync(db, tx, scope, "jobs", jobId, cancellationToken)
                    ?? throw new ManagementException("job_not_found", 404);
                if (
                    request.AssessmentJobId is not null
                    && (
                        Text(job.Body, "kind") != "Reassess"
                        || Text(job.Body, "sourceJobId") != sourceJobId
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
                var item = FindById(dataset.Body, "cases", Text(execution.Body, "caseId")!);
                if (
                    !criterion.TryGetProperty("scorer", out var scorer)
                    || Text(scorer, "kind") is not ("Exact" or "Numeric")
                )
                {
                    throw new ManagementException("scorer_unsupported", 409);
                }
                var outputBinding = await ResolveOutputBindingAsync(
                    db,
                    tx,
                    scope,
                    criterion,
                    execution,
                    cancellationToken
                );
                var outcome = "Scored";
                double? score = null;
                if (Text(execution.Body, "outcome") != "Success")
                {
                    outcome = "TaskFailure";
                }
                else if (
                    Text(execution.Body, "storageStatus") != "Stored"
                    || !execution.Body.TryGetProperty("value", out var output)
                )
                {
                    outcome = "NoStoredOutput";
                }
                else if (
                    !item.TryGetProperty("reference", out var reference)
                    || !reference.TryGetProperty("value", out var referenceValue)
                )
                {
                    outcome = "Unmapped";
                }
                else if (Text(reference, "completeness") != "Exhaustive")
                {
                    // Equality cannot turn an incomplete reference into a negative correctness claim.
                    outcome = "Unmapped";
                }
                else if (
                    Text(criterion, "scope") == "Collection"
                    && (
                        !criterion.TryGetProperty("matching", out var matching)
                        || Text(matching, "order") != "Preserve"
                        || Text(matching, "duplicates") != "Count"
                        || Text(matching, "unit") != "Item"
                        || (
                            matching.TryGetProperty("paraphrases", out var paraphrases)
                            && paraphrases.ValueKind == JsonValueKind.True
                        )
                    )
                )
                {
                    // Ordered exact comparison cannot stand in for semantic or unordered matching.
                    outcome = "Unmapped";
                }
                else if (outputBinding is null)
                {
                    outcome = "Unmapped";
                }
                else if (
                    !JsonPointer.TryGet(output, outputBinding, out var target)
                    || !JsonPointer.TryGet(
                        referenceValue,
                        Text(criterion, "referenceBinding") ?? "",
                        out var expected
                    )
                )
                {
                    outcome = "TargetMissing";
                }
                else if (Text(scorer, "kind") == "Exact")
                {
                    score = JsonElement.DeepEquals(target, expected) ? 1 : 0;
                }
                else if (
                    target.ValueKind == JsonValueKind.Number
                    && expected.ValueKind == JsonValueKind.Number
                    && target.TryGetDouble(out var actual)
                    && expected.TryGetDouble(out var expectedNumber)
                    && scorer.TryGetProperty("tolerance", out var tolerance)
                    && tolerance.TryGetDouble(out var amount)
                    && double.IsFinite(amount)
                    && amount >= 0
                    && double.IsFinite(actual)
                    && double.IsFinite(expectedNumber)
                )
                {
                    score = Math.Abs(actual - expectedNumber) <= amount ? 1 : 0;
                }
                else
                {
                    outcome = "Unmapped";
                }
                var id = Guid.NewGuid().ToString("N");
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
                                kind = Text(scorer, "kind"),
                                version = Text(scorer, "version"),
                            },
                            outcome,
                            score,
                            targetPointer = outputBinding,
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
                    id,
                    request.CriterionId,
                    outcome,
                    body,
                    cancellationToken
                );
            },
            cancellationToken
        );

    private static JsonElement FindById(JsonElement body, string arrayName, string id)
    {
        if (
            body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty(arrayName, out var array)
            && array.ValueKind == JsonValueKind.Array
        )
        {
            foreach (var value in array.EnumerateArray())
            {
                if (Text(value, "id") == id)
                {
                    return value;
                }
            }
        }
        throw new ManagementException("assessment_binding_missing", 409);
    }
}
