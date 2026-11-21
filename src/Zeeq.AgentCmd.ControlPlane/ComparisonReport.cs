using System.Text.Json;

namespace Zeeq.AgentCmd.ControlPlane;

/// <summary>Exact candidate configuration represented in a saved comparison.</summary>
/// <param name="Key">Content-derived candidate identity.</param>
/// <param name="CandidateId">Run-local candidate identity.</param>
/// <param name="BuildId">Registered application build.</param>
/// <param name="BuildHash">Exact build content digest.</param>
/// <param name="PublicationId">Frozen snapshot identity.</param>
/// <param name="PublicationHash">Exact snapshot content digest.</param>
/// <param name="CommandId">Compiled command identity.</param>
/// <param name="VariantId">Selected variant.</param>
public sealed record ComparisonCandidate(
    string Key,
    string CandidateId,
    string BuildId,
    string BuildHash,
    string PublicationId,
    string PublicationHash,
    string CommandId,
    string VariantId
);

/// <summary>One frozen case outcome under a candidate and criterion.</summary>
/// <param name="CaseId">Frozen case identity.</param>
/// <param name="Status">Scored, Pending, NonScore, Unreconciled or browser-projected Withheld.</param>
/// <param name="Score">Within-case mean, only when every expected repeat is scored.</param>
/// <param name="AssessmentIds">Exact contributing or incomplete assessments.</param>
/// <param name="Outcomes">Distinct observed assessment outcomes, including failures and non-scores.</param>
public sealed record ComparisonCase(
    string CaseId,
    string Status,
    double? Score,
    IReadOnlyList<string> AssessmentIds,
    IReadOnlyList<string> Outcomes
);

/// <summary>Equal-case weighted candidate results and visible coverage.</summary>
/// <param name="CandidateKey">Exact candidate identity.</param>
/// <param name="ExpectedCases">Frozen protocol case count.</param>
/// <param name="EligibleCases">Cases with complete repeat evidence and verified candidate provenance.</param>
/// <param name="ScoredCases">Cases with a score for each repeat.</param>
/// <param name="MissingCases">Cases lacking a complete score.</param>
/// <param name="UnreconciledCases">Cases with duplicate, conflicting or unverified evidence.</param>
/// <param name="Mean">Mean of scored case means, or null when none are scored.</param>
/// <param name="MatchedMean">Mean over the shared scored cases, or null when no cases match.</param>
/// <param name="Cases">Frozen cases and their status.</param>
public sealed record ComparisonCandidateMetric(
    string CandidateKey,
    int ExpectedCases,
    int EligibleCases,
    int ScoredCases,
    int MissingCases,
    int UnreconciledCases,
    double? Mean,
    double? MatchedMean,
    IReadOnlyList<ComparisonCase> Cases
);

/// <summary>Criterion results under one frozen benchmark and dataset.</summary>
/// <param name="CriterionId">Frozen criterion identity.</param>
/// <param name="CriterionVersion">Frozen criterion version.</param>
/// <param name="CommonCaseIds">Cases scored for every represented candidate.</param>
/// <param name="Candidates">Per-candidate coverage and scores.</param>
public sealed record ComparisonCriterion(
    string CriterionId,
    int CriterionVersion,
    IReadOnlyList<string> CommonCaseIds,
    IReadOnlyList<ComparisonCandidateMetric> Candidates
);

/// <summary>A guardrail whose evaluation is not yet supported.</summary>
/// <param name="Id">Frozen rule identity.</param>
/// <param name="Status">Unknown until the rule is evaluated.</param>
public sealed record ComparisonGuardrail(string Id, string Status);

/// <summary>Read-only exact-protocol comparison; it makes no causal or winner claim.</summary>
/// <param name="ComparisonId">Saved comparison identity.</param>
/// <param name="DatasetHash">Frozen dataset content identity.</param>
/// <param name="BenchmarkHash">Frozen benchmark content identity.</param>
/// <param name="BaselineCandidateId">Requested baseline ID.</param>
/// <param name="BaselineAmbiguous">Whether that ID names more than one exact candidate configuration.</param>
/// <param name="Candidates">Configurations represented by selected assessment IDs.</param>
/// <param name="Criteria">Case-weighted criterion summaries.</param>
/// <param name="Guardrails">Frozen rules with unknown evaluation status.</param>
/// <param name="IndependentAcceptanceEligible">Current exposure status for an acceptance dataset; null for other purposes.</param>
/// <param name="HoldoutWithheld">Whether per-case evidence was withheld by the browser projection.</param>
/// <param name="PrimaryCriterionId">Headline metric pinned by the frozen benchmark; never inferred from the first available score.</param>
public sealed record ComparisonReport(
    string ComparisonId,
    string DatasetHash,
    string BenchmarkHash,
    string BaselineCandidateId,
    bool BaselineAmbiguous,
    IReadOnlyList<ComparisonCandidate> Candidates,
    IReadOnlyList<ComparisonCriterion> Criteria,
    IReadOnlyList<ComparisonGuardrail> Guardrails,
    bool? IndependentAcceptanceEligible,
    bool HoldoutWithheld = false,
    string? PrimaryCriterionId = null
);

public sealed partial class RelationalControlPlaneStore
{
    private sealed record ComparisonObservation(
        string Id,
        string CandidateKey,
        string CaseId,
        string CriterionId,
        int Repeat,
        string Outcome,
        double? Score,
        bool Provenance,
        int CriterionVersion,
        string? EvaluatorKind,
        string? EvaluatorConfigId
    );

    /// <summary>Summarizes exact saved assessment references without inventing missing data or winners.</summary>
    public async Task<ComparisonReport> ReadComparisonSummaryAsync(
        WorkspaceScope scope,
        string id,
        CancellationToken cancellationToken = default
    )
    {
        var comparison = await ReadAsync(scope, "comparisons", id, cancellationToken)
            ?? throw new ManagementException("not_found", 404);
        if (
            !comparison.Body.TryGetProperty("assessmentIds", out var assessmentIds)
            || assessmentIds.ValueKind != JsonValueKind.Array
            || assessmentIds.GetArrayLength() is 0 or > 1000
        )
        {
            throw new ManagementException("comparison_invalid", 409);
        }
        var datasetHash = Text(comparison.Body, "datasetHash")!;
        var benchmarkHash = Text(comparison.Body, "benchmarkHash")!;
        var baselineId = Text(comparison.Body, "baselineCandidateId")!;
        var candidates = new Dictionary<string, ComparisonCandidate>(StringComparer.Ordinal);
        var observations = new List<ComparisonObservation>();
        ManagementResource? dataset = null,
            benchmark = null;
        var jobs = new Dictionary<string, ManagementResource>(StringComparer.Ordinal);
        foreach (var selectedId in assessmentIds.EnumerateArray())
        {
            var assessment = await ReadAsync(
                scope,
                "assessments",
                selectedId.GetString()!,
                cancellationToken
            ) ?? throw new ManagementException("assessment_not_found", 404);
            if (
                Text(assessment.Body, "datasetHash") != datasetHash
                || Text(assessment.Body, "benchmarkHash") != benchmarkHash
            )
            {
                throw new ManagementException("comparison_protocol_mismatch", 409);
            }
            var execution = await ReadAsync(
                scope,
                "executions",
                Text(assessment.Body, "executionId")!,
                cancellationToken
            ) ?? throw new ManagementException("execution_not_found", 404);
            var jobId = Text(assessment.Body, "jobId")
                ?? throw new ManagementException("comparison_evidence_invalid", 409);
            if (!jobs.TryGetValue(jobId, out var job))
            {
                job = await ReadAsync(scope, "jobs", jobId, cancellationToken)
                    ?? throw new ManagementException("job_not_found", 404);
                jobs.Add(jobId, job);
            }
            dataset ??= await ReadAsync(
                scope,
                "datasets",
                Text(job.Body, "datasetId")!,
                cancellationToken
            );
            benchmark ??= await ReadAsync(
                scope,
                "benchmarks",
                Text(job.Body, "benchmarkId")!,
                cancellationToken
            );
            if (
                dataset is null
                || benchmark is null
                || dataset.State != "Frozen"
                || benchmark.State != "Frozen"
                || dataset.ContentHash != datasetHash
                || benchmark.ContentHash != benchmarkHash
                || Text(job.Body, "datasetHash") != datasetHash
                || Text(job.Body, "benchmarkHash") != benchmarkHash
            )
            {
                throw new ManagementException("comparison_protocol_mismatch", 409);
            }
            var executionJobId = Text(execution.Body, "jobId")
                ?? throw new ManagementException("comparison_evidence_invalid", 409);
            if (!jobs.TryGetValue(executionJobId, out var executionJob))
            {
                executionJob = await ReadAsync(scope, "jobs", executionJobId, cancellationToken)
                    ?? throw new ManagementException("job_not_found", 404);
                jobs.Add(executionJobId, executionJob);
            }
            if (executionJobId != jobId)
            {
                if (
                    Text(job.Body, "kind") != "Reassess"
                    || Text(job.Body, "sourceJobId") != executionJobId
                    || Text(job.Body, "sourceDatasetHash") != Text(executionJob.Body, "datasetHash")
                )
                {
                    throw new ManagementException("comparison_evidence_invalid", 409);
                }
                var originalDataset = await ReadAsync(
                    scope,
                    "datasets",
                    Text(executionJob.Body, "datasetId")!,
                    cancellationToken
                ) ?? throw new ManagementException("dataset_not_found", 404);
                if (
                    originalDataset.State != "Frozen"
                    || originalDataset.ContentHash != Text(executionJob.Body, "datasetHash")
                    || !SameCaseInputs(originalDataset.Body, dataset.Body)
                )
                {
                    throw new ManagementException("comparison_protocol_mismatch", 409);
                }
            }
            var candidateId = Text(execution.Body, "candidateId")
                ?? throw new ManagementException("comparison_evidence_invalid", 409);
            if (
                Text(assessment.Body, "candidateId") != candidateId
                || Text(assessment.Body, "caseId") != Text(execution.Body, "caseId")
                || !executionJob.Body.TryGetProperty("pins", out var pins)
                || pins.ValueKind != JsonValueKind.Array
                || !execution.Body.TryGetProperty("repeatIndex", out var repeatValue)
                || !repeatValue.TryGetInt32(out var repeat)
                || repeat < 0
            )
            {
                throw new ManagementException("comparison_evidence_invalid", 409);
            }
            var matches = pins.EnumerateArray()
                .Where(p =>
                    p.ValueKind == JsonValueKind.Object
                    && p.TryGetProperty("candidate", out var candidate)
                    && Text(candidate, "id") == candidateId
                )
                .ToArray();
            if (matches.Length != 1)
            {
                throw new ManagementException("comparison_candidate_invalid", 409);
            }
            var pin = matches[0];
            var candidateBody = pin.GetProperty("candidate");
            var buildId = Text(candidateBody, "buildId")
                ?? throw new ManagementException("comparison_candidate_invalid", 409);
            var buildHash = Text(pin, "buildHash")
                ?? throw new ManagementException("comparison_candidate_invalid", 409);
            var publicationId = Text(candidateBody, "publicationId")
                ?? throw new ManagementException("comparison_candidate_invalid", 409);
            var publicationHash = Text(pin, "publicationHash")
                ?? throw new ManagementException("comparison_candidate_invalid", 409);
            var commandId = Text(candidateBody, "commandId")
                ?? throw new ManagementException("comparison_candidate_invalid", 409);
            var variantId = Text(candidateBody, "variantId")
                ?? throw new ManagementException("comparison_candidate_invalid", 409);
            var key = ContentIdentity.Hash(
                JsonSerializer.Serialize(
                    new[]
                    {
                        candidateId,
                        buildId,
                        buildHash,
                        publicationId,
                        publicationHash,
                        commandId,
                        variantId,
                    }
                )
            );
            candidates.TryAdd(
                key,
                new(
                    key,
                    candidateId,
                    buildId,
                    buildHash,
                    publicationId,
                    publicationHash,
                    commandId,
                    variantId
                )
            );
            double? score = null;
            if (
                assessment.Body.TryGetProperty("score", out var scoreElement)
                && scoreElement.ValueKind == JsonValueKind.Number
                && scoreElement.TryGetDouble(out var number)
                && double.IsFinite(number)
                && number is >= 0 and <= 1
            )
            {
                score = number;
            }
            var provenance =
                assessment.Body.TryGetProperty("comparisonEligible", out var eligible)
                && eligible.ValueKind == JsonValueKind.True
                && execution.Body.TryGetProperty("comparisonEligible", out var executionEligible)
                && executionEligible.ValueKind == JsonValueKind.True
                && Text(execution.Body, "publicationHash") == publicationHash
                && Text(execution.Body, "buildId") == buildId
                && Text(execution.Body, "publicationId") == publicationId
                && Text(execution.Body, "commandId") == commandId
                && Text(execution.Body, "variantId") == variantId;
            var evaluatorKind = assessment.Body.TryGetProperty("evaluator", out var evaluator)
                ? Text(evaluator, "kind")
                : null;
            observations.Add(
                new(
                    assessment.Id,
                    key,
                    Text(execution.Body, "caseId")!,
                    Text(assessment.Body, "criterionId") ?? "",
                    repeat,
                    Text(assessment.Body, "outcome") ?? "Unmapped",
                    score,
                    provenance,
                    assessment.Body.TryGetProperty("criterionVersion", out var assessedVersion)
                    && assessedVersion.TryGetInt32(out var cv)
                        ? cv
                        : 0,
                    evaluatorKind,
                    assessment.Body.TryGetProperty("evaluator", out var eval)
                        ? Text(eval, "configId")
                        : null
                )
            );
        }
        if (
            dataset is null
            || benchmark is null
            || !dataset.Body.TryGetProperty("cases", out var cases)
            || cases.ValueKind != JsonValueKind.Array
            || !benchmark.Body.TryGetProperty("criteria", out var criteria)
            || criteria.ValueKind != JsonValueKind.Array
            || !benchmark.Body.TryGetProperty("repeats", out var repeatsValue)
            || !repeatsValue.TryGetInt32(out var repeats)
            || repeats is < 1 or > 100
            || candidates.Count is 0 or > 100
            || criteria.GetArrayLength() > 256
            || (long)cases.GetArrayLength() * candidates.Count * criteria.GetArrayLength() > 100000
        )
        {
            throw new ManagementException("comparison_size_or_protocol_invalid", 409);
        }
        var caseIds = cases
            .EnumerateArray()
            .Select(c =>
                Text(c, "id") ?? throw new ManagementException("comparison_protocol_invalid", 409)
            )
            .Order(StringComparer.Ordinal)
            .ToArray();
        var orderedCandidates = candidates
            .Values.OrderBy(c => c.Key, StringComparer.Ordinal)
            .ToArray();
        if (caseIds.Distinct(StringComparer.Ordinal).Count() != caseIds.Length)
        {
            throw new ManagementException("comparison_protocol_invalid", 409);
        }
        var grouped = observations.ToLookup(o => (o.CandidateKey, o.CaseId, o.CriterionId));
        var reports = new List<ComparisonCriterion>();
        foreach (var criterion in criteria.EnumerateArray())
        {
            var criterionId = Text(criterion, "id")
                ?? throw new ManagementException("comparison_protocol_invalid", 409);
            var expectedKind = criterion.TryGetProperty("scorer", out var scorer)
                ? Text(scorer, "kind")
                : null;
            var expectedConfig = expectedKind == "Llm" ? Text(scorer, "judgeConfigId") : null;
            var criterionVersion =
                criterion.TryGetProperty("version", out var versionValue)
                && versionValue.TryGetInt32(out var parsedVersion)
                    ? parsedVersion
                    : 0;
            var all = new Dictionary<string, (List<ComparisonCase> Cases, int Eligible)>(
                StringComparer.Ordinal
            );
            foreach (var candidate in orderedCandidates)
            {
                var results = new List<ComparisonCase>();
                var eligibleCases = 0;
                foreach (var caseId in caseIds)
                {
                    var found = grouped[(candidate.Key, caseId, criterionId)]
                        .OrderBy(o => o.Repeat)
                        .ToArray();
                    var ids = found.Select(o => o.Id).ToArray();
                    string status;
                    double? value = null;
                    if (
                        found.Length > repeats
                        || found.Select(o => o.Repeat).Distinct().Count() != found.Length
                        || found.Any(o =>
                            o.Repeat >= repeats
                            || !o.Provenance
                            || o.EvaluatorKind != expectedKind
                            || o.CriterionVersion != criterionVersion
                            || (expectedKind == "Llm" && o.EvaluatorConfigId != expectedConfig)
                        )
                    )
                    {
                        status = "Unreconciled";
                    }
                    else if (found.Length < repeats)
                    {
                        status = "Pending";
                    }
                    else if (expectedKind is not ("Exact" or "Numeric" or "Human" or "Llm"))
                    {
                        status = "Unreconciled";
                    }
                    else
                    {
                        eligibleCases++;
                        if (found.All(o => o.Outcome == "Scored" && o.Score is not null))
                        {
                            status = "Scored";
                            value = found.Average(o => o.Score!.Value);
                        }
                        else
                        {
                            status = "NonScore";
                        }
                    }
                    results.Add(
                        new(
                            caseId,
                            status,
                            value,
                            ids,
                            found
                                .Select(o => o.Outcome)
                                .Distinct(StringComparer.Ordinal)
                                .Order(StringComparer.Ordinal)
                                .ToArray()
                        )
                    );
                }
                all.Add(candidate.Key, (results, eligibleCases));
            }
            var scoredByCandidate = orderedCandidates.ToDictionary(
                candidate => candidate.Key,
                candidate =>
                    all[candidate.Key]
                        .Cases.Where(item => item.Status == "Scored")
                        .Select(item => item.CaseId)
                        .ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal
            );
            var common = caseIds
                .Where(caseId =>
                    orderedCandidates.All(candidate =>
                        scoredByCandidate[candidate.Key].Contains(caseId)
                    )
                )
                .ToArray();
            var commonSet = common.ToHashSet(StringComparer.Ordinal);
            var metrics = orderedCandidates
                .Select(candidate =>
                {
                    var (results, eligibleCases) = all[candidate.Key];
                    var scored = results.Where(item => item.Status == "Scored").ToArray();
                    var matched = scored.Where(item => commonSet.Contains(item.CaseId)).ToArray();
                    return new ComparisonCandidateMetric(
                        candidate.Key,
                        caseIds.Length,
                        eligibleCases,
                        scored.Length,
                        caseIds.Length - scored.Length,
                        results.Count(item => item.Status == "Unreconciled"),
                        scored.Length == 0 ? null : scored.Average(item => item.Score!.Value),
                        matched.Length == 0 ? null : matched.Average(item => item.Score!.Value),
                        results
                    );
                })
                .ToArray();
            reports.Add(
                new(
                    criterionId,
                    criterion.TryGetProperty("version", out var version)
                    && version.TryGetInt32(out var v)
                        ? v
                        : 0,
                    common,
                    metrics
                )
            );
        }
        var guardrails =
            benchmark.Body.TryGetProperty("guardrails", out var rules)
            && rules.ValueKind == JsonValueKind.Array
                ? rules
                    .EnumerateArray()
                    .Select(rule => new ComparisonGuardrail(
                        Text(rule, "id") ?? "unknown",
                        "Unknown"
                    ))
                    .ToArray()
                : [];
        bool? independentAcceptanceEligible =
            Text(dataset.Body, "purpose") == "Acceptance"
                ? (
                    await ExposureForSnapshotAsync(scope, dataset, cancellationToken)
                ).IndependentAcceptanceEligible
                : null;
        return new(
            id,
            datasetHash,
            benchmarkHash,
            baselineId,
            orderedCandidates.Count(candidate => candidate.CandidateId == baselineId) != 1,
            orderedCandidates,
            reports,
            guardrails,
            independentAcceptanceEligible,
            PrimaryCriterionId: Text(benchmark.Body, "primaryCriterionId")
        );
    }
}
