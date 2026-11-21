using System.Text.Json;

namespace Zeeq.AgentCmd.ControlPlane;

public sealed partial class RelationalControlPlaneStore
{
    /// <summary>Creates a reassessment of exact retained executions without enqueueing generation work.</summary>
    public Task<ManagementResource> CreateReassessmentAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string name,
        CreateJobRequest request,
        CancellationToken cancellationToken = default
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                if (
                    request.Kind != "Reassess"
                    || !request.ExecutionApproved
                    || string.IsNullOrWhiteSpace(request.SourceJobId)
                )
                {
                    throw new ManagementException("reassessment_request_invalid");
                }
                var source = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "jobs",
                    request.SourceJobId,
                    cancellationToken
                ) ?? throw new ManagementException("source_job_not_found", 404);
                var dataset = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "datasets",
                    request.DatasetId,
                    cancellationToken
                ) ?? throw new ManagementException("dataset_not_found", 404);
                var benchmark = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "benchmarks",
                    request.BenchmarkId,
                    cancellationToken
                ) ?? throw new ManagementException("benchmark_not_found", 404);
                var sourceDataset = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "datasets",
                    Text(source.Body, "datasetId")!,
                    cancellationToken
                ) ?? throw new ManagementException("dataset_not_found", 404);
                if (
                    Text(source.Body, "kind") != "Backtest"
                    || dataset.State != "Frozen"
                    || benchmark.State != "Frozen"
                    || sourceDataset.State != "Frozen"
                    || Text(source.Body, "datasetHash") != sourceDataset.ContentHash
                    || !SameCaseInputs(sourceDataset.Body, dataset.Body)
                    || Text(benchmark.Body, "datasetId") != dataset.Id
                )
                {
                    throw new ManagementException("reassessment_protocol_mismatch", 409);
                }
                var selected = request
                    .Candidates?.OrderBy(c => c.Id, StringComparer.Ordinal)
                    .ToArray()
                    ?? [];
                var original = (
                    source.Body.GetProperty("candidates").Deserialize<JobCandidate[]>(Json) ?? []
                )
                    .OrderBy(c => c.Id, StringComparer.Ordinal)
                    .ToArray();
                if (!selected.SequenceEqual(original))
                {
                    throw new ManagementException("reassessment_candidate_mismatch", 409);
                }
                if (
                    !source.Body.TryGetProperty("pins", out var sourcePins)
                    || sourcePins.ValueKind != JsonValueKind.Array
                )
                {
                    throw new ManagementException("reassessment_candidate_mismatch", 409);
                }
                var outputShapes = new List<string>();
                foreach (var candidate in original)
                {
                    var pins = sourcePins
                        .EnumerateArray()
                        .Where(pin =>
                            pin.ValueKind == JsonValueKind.Object
                            && pin.TryGetProperty("candidate", out var pinnedCandidate)
                            && Text(pinnedCandidate, "id") == candidate.Id
                        )
                        .ToArray();
                    var build = await ReadAsync(
                        db,
                        tx,
                        scope,
                        "builds",
                        candidate.BuildId,
                        cancellationToken
                    ) ?? throw new ManagementException("build_not_found", 404);
                    if (pins.Length != 1 || Text(pins[0], "buildHash") != build.ContentHash)
                    {
                        throw new ManagementException("reassessment_candidate_mismatch", 409);
                    }
                    var signature = OutputSignature(build, candidate.CommandId);
                    outputShapes.Add(
                        string.IsNullOrWhiteSpace(signature)
                            ? "opaque:" + build.ContentHash + ":" + candidate.CommandId
                            : "declared:" + signature
                    );
                }
                RequireCommonShapeOrMappings(benchmark.Body, outputShapes);
                await using (
                    var pending = Command(
                        db,
                        tx,
                        "SELECT COUNT(*) FROM cp_work WHERE scope=@scope AND job_id=@job AND status<>'Finished'",
                        ("scope", scope.Key),
                        ("job", source.Id)
                    )
                )
                {
                    if (
                        Convert.ToInt64(
                            await pending.ExecuteScalarAsync(cancellationToken),
                            System.Globalization.CultureInfo.InvariantCulture
                        ) != 0
                    )
                    {
                        throw new ManagementException("reassessment_source_incomplete", 409);
                    }
                }
                var executionIds = new List<string>();
                await using (
                    var command = Command(
                        db,
                        tx,
                        "SELECT id,body FROM cp_documents WHERE scope=@scope AND kind='executions' ORDER BY id",
                        ("scope", scope.Key)
                    )
                )
                await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var body = Element(reader.GetString(1));
                        if (Text(body, "jobId") == source.Id)
                        {
                            executionIds.Add(reader.GetString(0));
                        }
                    }
                }
                if (executionIds.Count == 0)
                {
                    throw new ManagementException("reassessment_outputs_missing", 409);
                }
                var bodyJson = ContentIdentity.Normalize(
                    JsonSerializer.SerializeToElement(
                        new
                        {
                            kind = "Reassess",
                            purpose = "Evaluator",
                            sourceJobId = source.Id,
                            sourceDatasetHash = sourceDataset.ContentHash,
                            request.DatasetId,
                            datasetHash = dataset.ContentHash,
                            request.BenchmarkId,
                            benchmarkHash = benchmark.ContentHash,
                            executionApproved = true,
                            executionIds,
                            candidates = source.Body.GetProperty("candidates"),
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
                    name,
                    "AwaitingAssessment",
                    bodyJson,
                    cancellationToken
                );
            },
            cancellationToken
        );

    private static bool SameCaseInputs(JsonElement original, JsonElement revised)
    {
        if (
            original.ValueKind != JsonValueKind.Object
            || revised.ValueKind != JsonValueKind.Object
            || !original.TryGetProperty("cases", out var left)
            || left.ValueKind != JsonValueKind.Array
            || !revised.TryGetProperty("cases", out var right)
            || right.ValueKind != JsonValueKind.Array
            || left.GetArrayLength() != right.GetArrayLength()
        )
        {
            return false;
        }
        var leftCases = left.EnumerateArray().ToArray();
        var rightCases = right.EnumerateArray().ToArray();
        var leftIds = leftCases.Select(item => Text(item, "id")).ToArray();
        var rightIds = rightCases.Select(item => Text(item, "id")).ToArray();
        if (
            leftIds.Any(string.IsNullOrWhiteSpace)
            || rightIds.Any(string.IsNullOrWhiteSpace)
            || leftIds.Distinct(StringComparer.Ordinal).Count() != leftIds.Length
            || rightIds.Distinct(StringComparer.Ordinal).Count() != rightIds.Length
        )
        {
            return false;
        }
        foreach (var item in leftCases)
        {
            var counterpart = rightCases.SingleOrDefault(other =>
                Text(other, "id") == Text(item, "id")
            );
            if (
                counterpart.ValueKind != JsonValueKind.Object
                || Text(item, "sourceFamilyId") != Text(counterpart, "sourceFamilyId")
                || !item.TryGetProperty("input", out var input)
                || !counterpart.TryGetProperty("input", out var otherInput)
                || !JsonElement.DeepEquals(input, otherInput)
            )
            {
                return false;
            }
            var hasAssets = item.TryGetProperty("assets", out var assets);
            var hasOtherAssets = counterpart.TryGetProperty("assets", out var otherAssets);
            if (
                hasAssets != hasOtherAssets
                || hasAssets && !JsonElement.DeepEquals(assets, otherAssets)
            )
            {
                return false;
            }
        }
        return true;
    }
}
