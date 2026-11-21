using System.Text.Json;

namespace Zeeq.AgentCmd.ControlPlane;

/// <summary>One blinded calibration pair against the same retained execution and criterion.</summary>
/// <param name="HumanAssessmentId">Human-labelled reference assessment.</param>
/// <param name="JudgeAssessmentId">Judge assessment to measure.</param>
public sealed record CalibrationPair(string HumanAssessmentId, string JudgeAssessmentId);

/// <summary>Predeclared calibration rule and exact sample identities.</summary>
/// <param name="JudgeConfigurationId">Frozen judge configuration.</param>
/// <param name="CriterionId">Single criterion under calibration.</param>
/// <param name="Pairs">Distinct retained calibration cases.</param>
/// <param name="MaximumScoreDifference">Maximum absolute normalized score disagreement for a pair.</param>
/// <param name="MinimumAgreement">Minimum required fraction of agreeing pairs.</param>
public sealed record CalibrationRequest(
    string JudgeConfigurationId,
    string CriterionId,
    IReadOnlyList<CalibrationPair> Pairs,
    double MaximumScoreDifference,
    double MinimumAgreement
);

public sealed partial class RelationalControlPlaneStore
{
    /// <summary>Records bounded agreement against human labels, scoped to exact configuration and calibration dataset.</summary>
    public Task<ManagementResource> CalibrateJudgeAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string name,
        CalibrationRequest request,
        CancellationToken cancellationToken = default
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                if (
                    request.Pairs is not { Count: >= 10 and <= 1000 }
                    || string.IsNullOrWhiteSpace(request.CriterionId)
                    || !double.IsFinite(request.MaximumScoreDifference)
                    || request.MaximumScoreDifference is < 0 or > 1
                    || !double.IsFinite(request.MinimumAgreement)
                    || request.MinimumAgreement is < 0 or > 1
                )
                {
                    throw new ManagementException("calibration_policy_invalid");
                }
                var config = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "judge-configurations",
                    request.JudgeConfigurationId,
                    cancellationToken
                ) ?? throw new ManagementException("judge_configuration_not_found", 404);
                if (config.State != "Frozen")
                {
                    throw new ManagementException("judge_configuration_not_frozen", 409);
                }
                if (
                    !config.Body.TryGetProperty("calibrationPolicy", out var policy)
                    || policy.ValueKind != JsonValueKind.Object
                    || !policy.TryGetProperty("minimumSamples", out var minimumSamples)
                    || !minimumSamples.TryGetInt32(out var minimum)
                    || minimum < 10
                    || request.Pairs.Count < minimum
                    || !policy.TryGetProperty("maximumScoreDifference", out var maximumDifference)
                    || !maximumDifference.TryGetDouble(out var allowedDifference)
                    || allowedDifference != request.MaximumScoreDifference
                    || !policy.TryGetProperty("minimumAgreement", out var minimumAgreement)
                    || !minimumAgreement.TryGetDouble(out var requiredAgreement)
                    || requiredAgreement != request.MinimumAgreement
                )
                {
                    throw new ManagementException("calibration_policy_mismatch", 409);
                }
                string? datasetId = null;
                string? benchmarkId = null;
                string? expectedDatasetHash = null;
                string? expectedBenchmarkHash = null;
                var caseIds = new HashSet<string>(StringComparer.Ordinal);
                var pairResults = new List<object>();
                var agreements = 0;
                foreach (var pair in request.Pairs)
                {
                    var human = await ReadAsync(
                        db,
                        tx,
                        scope,
                        "assessments",
                        pair.HumanAssessmentId,
                        cancellationToken
                    ) ?? throw new ManagementException("assessment_not_found", 404);
                    var judge = await ReadAsync(
                        db,
                        tx,
                        scope,
                        "assessments",
                        pair.JudgeAssessmentId,
                        cancellationToken
                    ) ?? throw new ManagementException("assessment_not_found", 404);
                    if (
                        Text(human.Body, "outcome") != "Scored"
                        || Text(judge.Body, "outcome") != "Scored"
                        || Text(human.Body, "criterionId") != request.CriterionId
                        || Text(judge.Body, "criterionId") != request.CriterionId
                        || Text(human.Body, "executionId") != Text(judge.Body, "executionId")
                        || Text(human.Body, "benchmarkHash") != Text(judge.Body, "benchmarkHash")
                        || Text(human.Body, "datasetHash") != Text(judge.Body, "datasetHash")
                        || Text(human.Body, "caseId") != Text(judge.Body, "caseId")
                        || !human.Body.TryGetProperty("criterionVersion", out var humanVersion)
                        || !judge.Body.TryGetProperty("criterionVersion", out var judgeVersion)
                        || !JsonElement.DeepEquals(humanVersion, judgeVersion)
                        || !human.Body.TryGetProperty("evaluator", out var humanEvaluator)
                        || Text(humanEvaluator, "kind") != "Human"
                        || !judge.Body.TryGetProperty("evaluator", out var judgeEvaluator)
                        || Text(judgeEvaluator, "kind") != "Llm"
                        || Text(judgeEvaluator, "configId") != config.Id
                        || Text(judgeEvaluator, "configHash") != config.ContentHash
                        || !human.Body.TryGetProperty("score", out var humanScore)
                        || !humanScore.TryGetDouble(out var h)
                        || !judge.Body.TryGetProperty("score", out var judgeScore)
                        || !judgeScore.TryGetDouble(out var j)
                        || !double.IsFinite(h)
                        || !double.IsFinite(j)
                        || h is < 0 or > 1
                        || j is < 0 or > 1
                    )
                    {
                        throw new ManagementException("calibration_pair_invalid", 409);
                    }
                    var job = await ReadAsync(
                        db,
                        tx,
                        scope,
                        "jobs",
                        Text(human.Body, "jobId")!,
                        cancellationToken
                    ) ?? throw new ManagementException("job_not_found", 404);
                    var currentDataset = Text(job.Body, "datasetId");
                    var currentBenchmark = Text(job.Body, "benchmarkId");
                    var caseId = Text(human.Body, "caseId");
                    if (
                        string.IsNullOrWhiteSpace(currentDataset)
                        || string.IsNullOrWhiteSpace(caseId)
                        || datasetId is not null && datasetId != currentDataset
                        || string.IsNullOrWhiteSpace(currentBenchmark)
                        || benchmarkId is not null && benchmarkId != currentBenchmark
                        || !caseIds.Add(caseId)
                    )
                    {
                        throw new ManagementException("calibration_sample_invalid", 409);
                    }
                    var currentDatasetHash = Text(human.Body, "datasetHash");
                    var currentBenchmarkHash = Text(human.Body, "benchmarkHash");
                    if (
                        string.IsNullOrWhiteSpace(currentDatasetHash)
                        || string.IsNullOrWhiteSpace(currentBenchmarkHash)
                        || (
                            expectedDatasetHash is not null
                            && expectedDatasetHash != currentDatasetHash
                        )
                        || (
                            expectedBenchmarkHash is not null
                            && expectedBenchmarkHash != currentBenchmarkHash
                        )
                        || Text(job.Body, "datasetHash") != currentDatasetHash
                        || Text(job.Body, "benchmarkHash") != currentBenchmarkHash
                    )
                    {
                        throw new ManagementException("calibration_protocol_mismatch", 409);
                    }
                    expectedDatasetHash = currentDatasetHash;
                    expectedBenchmarkHash = currentBenchmarkHash;
                    datasetId = currentDataset;
                    benchmarkId = currentBenchmark;
                    var agrees = Math.Abs(h - j) <= request.MaximumScoreDifference;
                    if (agrees)
                    {
                        agreements++;
                    }
                    pairResults.Add(
                        new
                        {
                            pair.HumanAssessmentId,
                            pair.JudgeAssessmentId,
                            caseId,
                            humanScore = h,
                            judgeScore = j,
                            agrees,
                        }
                    );
                }
                var dataset = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "datasets",
                    datasetId!,
                    cancellationToken
                ) ?? throw new ManagementException("dataset_not_found", 404);
                if (
                    dataset.State != "Frozen"
                    || Text(dataset.Body, "purpose") != "Calibration"
                    || dataset.ContentHash != expectedDatasetHash
                )
                {
                    throw new ManagementException("calibration_dataset_required", 409);
                }
                var benchmark = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "benchmarks",
                    benchmarkId!,
                    cancellationToken
                ) ?? throw new ManagementException("benchmark_not_found", 404);
                if (benchmark.State != "Frozen" || benchmark.ContentHash != expectedBenchmarkHash)
                {
                    throw new ManagementException("calibration_protocol_mismatch", 409);
                }
                var criterionHash = ContentIdentity.Hash(
                    ContentIdentity.Normalize(
                        FindById(benchmark.Body, "criteria", request.CriterionId)
                    )
                );
                var families = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in caseIds)
                {
                    var item = FindById(dataset.Body, "cases", id);
                    var family = Text(item, "sourceFamilyId");
                    if (string.IsNullOrWhiteSpace(family))
                    {
                        throw new ManagementException("calibration_family_missing", 409);
                    }
                    families.Add(family);
                }
                // Correlated rows from one document are one independent calibration sample.
                if (families.Count < minimum)
                {
                    throw new ManagementException(
                        "calibration_independent_samples_insufficient",
                        409
                    );
                }
                await using (
                    var command = Command(
                        db,
                        tx,
                        "SELECT body FROM cp_documents WHERE scope=@scope AND kind='datasets' AND state='Frozen'",
                        ("scope", scope.Key)
                    )
                )
                await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var other = Element(reader.GetString(0));
                        if (
                            Text(other, "purpose") == "Acceptance"
                            && other.TryGetProperty("cases", out var cases)
                            && cases.ValueKind == JsonValueKind.Array
                            && cases
                                .EnumerateArray()
                                .Any(item => families.Contains(Text(item, "sourceFamilyId") ?? ""))
                        )
                        {
                            throw new ManagementException("calibration_acceptance_overlap", 409);
                        }
                    }
                }
                var rate = (double)agreements / request.Pairs.Count;
                var state = rate >= request.MinimumAgreement ? "Admitted" : "Rejected";
                var body = ContentIdentity.Normalize(
                    JsonSerializer.SerializeToElement(
                        new
                        {
                            request.JudgeConfigurationId,
                            judgeConfigurationHash = config.ContentHash,
                            request.CriterionId,
                            criterionHash,
                            datasetId,
                            datasetHash = dataset.ContentHash,
                            benchmarkHash = expectedBenchmarkHash,
                            request.MaximumScoreDifference,
                            request.MinimumAgreement,
                            agreements,
                            total = request.Pairs.Count,
                            agreement = rate,
                            pairs = pairResults,
                        },
                        Json
                    )
                );
                return await InsertAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    "calibrations",
                    Guid.NewGuid().ToString("N"),
                    name,
                    state,
                    body,
                    cancellationToken
                );
            },
            cancellationToken
        );
}
