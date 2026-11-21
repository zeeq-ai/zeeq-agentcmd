using System.Text.Json;

namespace Zeeq.AgentCmd.ControlPlane;

/// <summary>Explicitly approved judge work for a retained output under a frozen protocol.</summary>
/// <param name="ExecutionId">Exact output execution.</param>
/// <param name="CriterionId">Pinned LLM criterion.</param>
/// <param name="ExecutionApproved">Explicit approval for evaluator calls.</param>
/// <param name="AssessmentJobId">Optional reassessment that retains this output.</param>
/// <param name="CalibrationId">Admitted matching calibration for non-calibration datasets.</param>
public sealed record JudgeEvaluationRequest(
    string ExecutionId,
    string CriterionId,
    bool ExecutionApproved,
    string? AssessmentJobId = null,
    string? CalibrationId = null
);

public sealed partial class RelationalControlPlaneStore
{
    /// <summary>Queues one separately accounted evaluator work item without invoking a provider.</summary>
    public Task<ManagementResource> CreateJudgeEvaluationAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        JudgeEvaluationRequest request,
        CancellationToken cancellationToken = default
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                if (!request.ExecutionApproved)
                {
                    throw new ManagementException("judge_execution_approval_required");
                }
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
                var assessmentJobId = request.AssessmentJobId ?? sourceJobId;
                var parent = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "jobs",
                    assessmentJobId,
                    cancellationToken
                ) ?? throw new ManagementException("job_not_found", 404);
                if (
                    request.AssessmentJobId is not null
                    && (
                        Text(parent.Body, "kind") != "Reassess"
                        || Text(parent.Body, "sourceJobId") != sourceJobId
                        || !parent.Body.TryGetProperty("executionIds", out var ids)
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
                    Text(parent.Body, "datasetId")!,
                    cancellationToken
                ) ?? throw new ManagementException("dataset_not_found", 404);
                var benchmark = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "benchmarks",
                    Text(parent.Body, "benchmarkId")!,
                    cancellationToken
                ) ?? throw new ManagementException("benchmark_not_found", 404);
                if (
                    dataset.State != "Frozen"
                    || benchmark.State != "Frozen"
                    || dataset.ContentHash != Text(parent.Body, "datasetHash")
                    || benchmark.ContentHash != Text(parent.Body, "benchmarkHash")
                )
                {
                    throw new ManagementException("assessment_pin_mismatch", 409);
                }
                var criterion = FindById(benchmark.Body, "criteria", request.CriterionId);
                if (
                    !criterion.TryGetProperty("scorer", out var scorer)
                    || Text(scorer, "kind") != "Llm"
                    || Text(scorer, "judgeConfigId") is not { } configId
                )
                {
                    throw new ManagementException("judge_configuration_required", 409);
                }
                var config = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "judge-configurations",
                    configId,
                    cancellationToken
                ) ?? throw new ManagementException("judge_configuration_not_found", 404);
                var outputPointer = await ResolveOutputBindingAsync(
                    db,
                    tx,
                    scope,
                    criterion,
                    execution,
                    cancellationToken
                );
                if (outputPointer is null)
                {
                    var assessmentId = Guid.NewGuid().ToString("N");
                    var assessmentBody = ContentIdentity.Normalize(
                        JsonSerializer.SerializeToElement(
                            new
                            {
                                jobId = assessmentJobId,
                                executionId = execution.Id,
                                candidateId = Text(execution.Body, "candidateId"),
                                comparisonEligible = execution.Body.TryGetProperty(
                                    "comparisonEligible",
                                    out var eligible
                                )
                                    && eligible.ValueKind == JsonValueKind.True,
                                caseId = Text(execution.Body, "caseId"),
                                criterionId = request.CriterionId,
                                criterionVersion = criterion.TryGetProperty(
                                    "version",
                                    out var version
                                ) && version.TryGetInt32(out var parsedVersion)
                                    ? parsedVersion
                                    : 0,
                                evaluator = new
                                {
                                    kind = "Llm",
                                    configId = config.Id,
                                    configHash = config.ContentHash,
                                },
                                outcome = "Unmapped",
                                score = (double?)null,
                                datasetHash = dataset.ContentHash,
                                benchmarkHash = benchmark.ContentHash,
                            },
                            Json
                        )
                    );
                    await InsertAsync(
                        db,
                        tx,
                        scope,
                        actor,
                        "assessments",
                        assessmentId,
                        request.CriterionId,
                        "Unmapped",
                        assessmentBody,
                        cancellationToken
                    );
                    var jobBody = ContentIdentity.Normalize(
                        JsonSerializer.SerializeToElement(
                            new
                            {
                                kind = "Judge",
                                purpose = "Evaluator",
                                parentJobId = assessmentJobId,
                                executionId = execution.Id,
                                request.CriterionId,
                                assessmentId,
                                outcome = "Unmapped",
                            },
                            Json
                        )
                    );
                    return await InsertAsync(
                        db,
                        tx,
                        scope,
                        actor,
                        "jobs",
                        Guid.NewGuid().ToString("N"),
                        "Judge " + request.CriterionId,
                        "Unmapped",
                        jobBody,
                        cancellationToken
                    );
                }
                if (
                    config.State != "Frozen"
                    || Text(config.Body, "routeId") is not { Length: > 0 } route
                    || Text(config.Body, "modelId") is not { Length: > 0 } model
                    || Text(config.Body, "promptVersion") is not { Length: > 0 } promptVersion
                    || Text(config.Body, "resultSchemaVersion") is not { Length: > 0 } schemaVersion
                    || Text(config.Body, "rubric") is not { Length: > 0 } rubric
                    || !config.Body.TryGetProperty("maxOutputTokens", out var tokensValue)
                    || !tokensValue.TryGetInt32(out var tokens)
                    || tokens is <= 0 or > 16384
                    || !config.Body.TryGetProperty("rubricLevels", out var levels)
                    || levels.ValueKind != JsonValueKind.Array
                )
                {
                    throw new ManagementException("judge_configuration_invalid", 409);
                }
                if (
                    !config.Body.TryGetProperty("calibrationPolicy", out var calibrationPolicy)
                    || calibrationPolicy.ValueKind != JsonValueKind.Object
                    || !calibrationPolicy.TryGetProperty("minimumSamples", out var minimumSamples)
                    || !minimumSamples.TryGetInt32(out var minimum)
                    || minimum < 10
                    || !calibrationPolicy.TryGetProperty(
                        "maximumScoreDifference",
                        out var difference
                    )
                    || !difference.TryGetDouble(out var d)
                    || !double.IsFinite(d)
                    || d is < 0 or > 1
                    || !calibrationPolicy.TryGetProperty("minimumAgreement", out var agreement)
                    || !agreement.TryGetDouble(out var a)
                    || !double.IsFinite(a)
                    || a is < 0 or > 1
                )
                {
                    throw new ManagementException("calibration_policy_required", 409);
                }
                var criterionHash = ContentIdentity.Hash(ContentIdentity.Normalize(criterion));
                if (Text(dataset.Body, "purpose") != "Calibration")
                {
                    var calibration = request.CalibrationId is null
                        ? null
                        : await ReadAsync(
                            db,
                            tx,
                            scope,
                            "calibrations",
                            request.CalibrationId,
                            cancellationToken
                        );
                    if (
                        calibration is null
                        || calibration.State != "Admitted"
                        || Text(calibration.Body, "judgeConfigurationId") != config.Id
                        || Text(calibration.Body, "judgeConfigurationHash") != config.ContentHash
                        || Text(calibration.Body, "criterionHash") != criterionHash
                    )
                    {
                        throw new ManagementException("judge_calibration_required", 409);
                    }
                    var calibrationDataset = await ReadAsync(
                        db,
                        tx,
                        scope,
                        "datasets",
                        Text(calibration.Body, "datasetId")!,
                        cancellationToken
                    ) ?? throw new ManagementException("calibration_dataset_required", 409);
                    if (
                        calibrationDataset.State != "Frozen"
                        || calibrationDataset.ContentHash != Text(calibration.Body, "datasetHash")
                        || Text(calibrationDataset.Body, "purpose") != "Calibration"
                        || !calibrationDataset.Body.TryGetProperty(
                            "cases",
                            out var calibrationCases
                        )
                        || calibrationCases.ValueKind != JsonValueKind.Array
                        || !dataset.Body.TryGetProperty("cases", out var targetCases)
                        || targetCases.ValueKind != JsonValueKind.Array
                    )
                    {
                        throw new ManagementException("calibration_dataset_required", 409);
                    }
                    var calibrationFamilies = calibrationCases
                        .EnumerateArray()
                        .Select(item => Text(item, "sourceFamilyId"))
                        .ToArray();
                    var targetFamilies = targetCases
                        .EnumerateArray()
                        .Select(item => Text(item, "sourceFamilyId"))
                        .ToArray();
                    if (
                        calibrationFamilies.Any(string.IsNullOrWhiteSpace)
                        || targetFamilies.Any(string.IsNullOrWhiteSpace)
                        || calibrationFamilies
                            .Intersect(targetFamilies, StringComparer.Ordinal)
                            .Any()
                    )
                    {
                        throw new ManagementException("calibration_dataset_overlap", 409);
                    }
                }
                if (
                    Text(execution.Body, "outcome") != "Success"
                    || Text(execution.Body, "storageStatus") != "Stored"
                    || !execution.Body.TryGetProperty("value", out var output)
                    || !execution.Body.TryGetProperty("outputRef", out var outputRef)
                    || !JsonPointer.TryGet(output, outputPointer, out var target)
                )
                {
                    throw new ManagementException("assessment_target_unavailable", 409);
                }
                var caseId = Text(execution.Body, "caseId")
                    ?? throw new ManagementException("execution_invalid");
                var item = FindById(dataset.Body, "cases", caseId);
                JsonElement? reference = null;
                var completeness = "None";
                if (
                    item.TryGetProperty("reference", out var referenceObject)
                    && referenceObject.TryGetProperty("value", out var referenceValue)
                )
                {
                    completeness = Text(referenceObject, "completeness") ?? "None";
                    if (
                        JsonPointer.TryGet(
                            referenceValue,
                            Text(criterion, "referenceBinding") ?? "",
                            out var boundReference
                        )
                    )
                    {
                        reference = boundReference;
                    }
                }
                string? source = null;
                var coverage = "Unknown";
                if (
                    Text(criterion, "sourceBinding") is { } sourcePointer
                    && item.TryGetProperty("input", out var input)
                    && JsonPointer.TryGet(input, sourcePointer, out var boundSource)
                    && boundSource.ValueKind == JsonValueKind.String
                )
                {
                    source = boundSource.GetString();
                    coverage = Text(item, "sourceCoverage") is "Complete" or "Partial"
                        ? Text(item, "sourceCoverage")!
                        : "Unknown";
                }
                var id = Guid.NewGuid().ToString("N");
                var workId = ContentIdentity.Hash(
                    JsonSerializer.Serialize(new[] { id, request.ExecutionId, request.CriterionId })
                );
                var data = ContentIdentity.Normalize(
                    JsonSerializer.SerializeToElement(
                        new
                        {
                            kind = "Judge",
                            purpose = "Evaluator",
                            executionApproved = true,
                            parentJobId = assessmentJobId,
                            evaluatorJobId = id,
                            criterionVersion = criterion.TryGetProperty(
                                "version",
                                out var criterionVersion
                            ) && criterionVersion.TryGetInt32(out var v)
                                ? v
                                : 0,
                            executionId = execution.Id,
                            request.CriterionId,
                            criterionHash,
                            datasetHash = dataset.ContentHash,
                            benchmarkHash = benchmark.ContentHash,
                            judgeConfigurationId = config.Id,
                            judgeConfigurationHash = config.ContentHash,
                            request.CalibrationId,
                            outputRef,
                            output,
                            targetPointer = outputPointer,
                            target,
                            reference,
                            referenceCompleteness = completeness,
                            source,
                            sourceCoverage = coverage,
                            policy = new
                            {
                                routeId = route,
                                modelId = model,
                                promptVersion,
                                resultSchemaVersion = schemaVersion,
                                maxOutputTokens = tokens,
                                rubric,
                                rubricLevels = levels,
                            },
                        },
                        Json
                    )
                );
                var job = await InsertAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    "jobs",
                    id,
                    "Judge " + request.CriterionId,
                    "Queued",
                    data,
                    cancellationToken
                );
                await ExecuteAsync(
                    db,
                    tx,
                    "INSERT INTO cp_work(scope,id,job_id,build_id,candidate_id,case_id,repeat_index,status,request) VALUES(@scope,@id,@job,@build,@candidate,@case,0,'Queued',@request)",
                    cancellationToken,
                    ("scope", scope.Key),
                    ("id", workId),
                    ("job", id),
                    ("build", "judge:" + config.Id),
                    ("candidate", Text(execution.Body, "candidateId")!),
                    ("case", caseId),
                    ("request", data)
                );
                return job;
            },
            cancellationToken
        );

    /// <summary>Persists validated findings only for a known completed evaluator attempt and pinned inputs.</summary>
    public Task<ManagementResource> RecordJudgeAssessmentAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string workId,
        ScopedJudgeResult result,
        CancellationToken cancellationToken = default
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                string workRequest;
                string owner;
                long epoch;
                string journalReceipt;
                await using (
                    var command = Command(
                        db,
                        tx,
                        "SELECT w.request,w.owner,w.epoch,a.receipt FROM cp_work w JOIN cp_attempts a ON a.scope=w.scope AND a.work_id=w.id WHERE w.scope=@scope AND w.id=@work AND w.status IN ('Claimed','Held','Finished') AND a.id=@attempt AND a.state='Finished' AND a.owner=w.owner AND a.epoch=w.epoch",
                        ("scope", scope.Key),
                        ("work", workId),
                        ("attempt", result.AttemptId.ToString("D"))
                    )
                )
                await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    if (!await reader.ReadAsync(cancellationToken))
                    {
                        throw new ManagementException("judge_attempt_missing", 409);
                    }
                    workRequest = reader.GetString(0);
                    owner = reader.GetString(1);
                    epoch = reader.GetInt64(2);
                    journalReceipt = reader.GetString(3);
                }
                var work = Element(workRequest);
                if (
                    await ExecuteAsync(
                        db,
                        tx,
                        "UPDATE cp_documents SET state=state WHERE scope=@scope AND kind='jobs' AND id=@job",
                        cancellationToken,
                        ("scope", scope.Key),
                        ("job", Text(work, "evaluatorJobId"))
                    ) != 1
                )
                {
                    throw new ManagementException("job_not_found", 404);
                }
                if (owner != actor.Subject || Text(work, "purpose") != "Evaluator")
                {
                    throw new ManagementException("judge_attempt_mismatch", 409);
                }
                var journalEvent = Element(journalReceipt);
                var journalOutcome = Text(journalEvent, "Outcome") ?? Text(journalEvent, "outcome");
                if (
                    result.Outcome is not ("Returned" or "EvaluatorError")
                    || (result.Outcome == "Returned" && journalOutcome != "success")
                    || (Text(journalEvent, "Provider") ?? Text(journalEvent, "provider"))
                        != Text(work.GetProperty("policy"), "routeId")
                    || JournalUsage(journalEvent, "InputTokens", "inputTokens")
                        != result.InputTokens
                    || JournalUsage(journalEvent, "OutputTokens", "outputTokens")
                        != result.OutputTokens
                )
                {
                    throw new ManagementException("judge_attempt_mismatch", 409);
                }
                var outcome = "EvaluatorError";
                double? score = null;
                JsonElement? findings = null;
                string? rubricLevel = null;
                var referenceDisputed = false;
                if (
                    result.Outcome == "Returned"
                    && result.Findings is { ValueKind: JsonValueKind.Object } response
                    && response.TryGetProperty("findings", out var values)
                )
                {
                    var reference =
                        work.TryGetProperty("reference", out var referenceValue)
                        && referenceValue.ValueKind != JsonValueKind.Null
                            ? referenceValue
                            : (JsonElement?)null;
                    var validated = JudgeFindingsValidator.Validate(
                        values,
                        work.GetProperty("target"),
                        reference,
                        Text(work, "source"),
                        Text(work, "sourceCoverage") ?? "Unknown"
                    );
                    if (validated.Valid)
                    {
                        findings = values.Clone();
                        referenceDisputed =
                            Text(work, "referenceCompleteness") == "Exhaustive"
                            && values
                                .EnumerateArray()
                                .Any(finding => Text(finding, "category") == "SupportedAddition");
                        rubricLevel = Text(response, "rubricLevel");
                        var policy = work.GetProperty("policy");
                        var selected = policy
                            .GetProperty("rubricLevels")
                            .EnumerateArray()
                            .Where(level => Text(level, "name") == rubricLevel)
                            .ToArray();
                        if (
                            !validated.HasUnresolved
                            && !referenceDisputed
                            && selected.Length == 1
                            && selected[0].TryGetProperty("score", out var value)
                            && value.TryGetDouble(out var numeric)
                            && double.IsFinite(numeric)
                            && numeric is >= 0 and <= 1
                        )
                        {
                            score = numeric;
                            outcome = "Scored";
                        }
                        else
                        {
                            outcome = "Unreconciled";
                        }
                    }
                }
                var execution = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "executions",
                    Text(work, "executionId")!,
                    cancellationToken
                ) ?? throw new ManagementException("execution_not_found", 404);
                var body = ContentIdentity.Normalize(
                    JsonSerializer.SerializeToElement(
                        new
                        {
                            jobId = Text(work, "parentJobId"),
                            evaluatorJobId = Text(work, "evaluatorJobId"),
                            executionId = execution.Id,
                            candidateId = Text(execution.Body, "candidateId"),
                            comparisonEligible = execution.Body.TryGetProperty(
                                "comparisonEligible",
                                out var eligible
                            )
                                && eligible.ValueKind == JsonValueKind.True,
                            caseId = Text(execution.Body, "caseId"),
                            criterionId = Text(work, "criterionId"),
                            criterionVersion = work.GetProperty("criterionVersion").GetInt32(),
                            evaluator = new
                            {
                                kind = "Llm",
                                configId = Text(work, "judgeConfigurationId"),
                                configHash = Text(work, "judgeConfigurationHash"),
                                routeId = Text(work.GetProperty("policy"), "routeId"),
                                requestedModel = Text(work.GetProperty("policy"), "modelId"),
                                promptVersion = Text(work.GetProperty("policy"), "promptVersion"),
                                resultSchemaVersion = Text(
                                    work.GetProperty("policy"),
                                    "resultSchemaVersion"
                                ),
                                reportedModel = result.ReportedModel,
                                result.AttemptId,
                                result.InputTokens,
                                result.OutputTokens,
                            },
                            outcome,
                            score,
                            findings,
                            rubricLevel,
                            referenceDisputed,
                            targetPointer = Text(work, "targetPointer"),
                            referenceCompleteness = Text(work, "referenceCompleteness"),
                            sourceCoverage = Text(work, "sourceCoverage"),
                            outputRef = work.GetProperty("outputRef"),
                            datasetHash = Text(work, "datasetHash"),
                            benchmarkHash = Text(work, "benchmarkHash"),
                        },
                        Json
                    )
                );
                var assessmentId = ContentIdentity.Hash(
                    JsonSerializer.Serialize(new[] { workId, result.AttemptId.ToString("D") })
                );
                var existing = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "assessments",
                    assessmentId,
                    cancellationToken
                );
                if (existing is not null)
                {
                    if (existing.ContentHash != ContentIdentity.Hash(body))
                    {
                        throw new ManagementException("judge_receipt_conflict", 409);
                    }
                    return existing;
                }
                var changed = await ExecuteAsync(
                    db,
                    tx,
                    "UPDATE cp_work SET status='Finished',result=@result WHERE scope=@scope AND id=@id AND owner=@owner AND epoch=@epoch AND status IN ('Claimed','Held')",
                    cancellationToken,
                    ("scope", scope.Key),
                    ("id", workId),
                    ("owner", owner),
                    ("epoch", epoch),
                    ("result", body)
                );
                if (changed != 1)
                {
                    throw new ManagementException("judge_receipt_conflict", 409);
                }
                var evaluatorJob = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "jobs",
                    Text(work, "evaluatorJobId")!,
                    cancellationToken
                ) ?? throw new ManagementException("job_not_found", 404);
                await ReplaceAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    evaluatorJob,
                    evaluatorJob.Name,
                    evaluatorJob.State == "Cancelled" ? "Cancelled" : "Finished",
                    ContentIdentity.Normalize(evaluatorJob.Body),
                    "judge.completed",
                    cancellationToken
                );
                return await InsertAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    "assessments",
                    assessmentId,
                    Text(work, "criterionId")!,
                    outcome,
                    body,
                    cancellationToken
                );
            },
            cancellationToken
        );

    private static long? JournalUsage(JsonElement receipt, string pascal, string camel)
    {
        if (
            !receipt.TryGetProperty(pascal, out var value)
            && !receipt.TryGetProperty(camel, out value)
        )
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;
    }
}
