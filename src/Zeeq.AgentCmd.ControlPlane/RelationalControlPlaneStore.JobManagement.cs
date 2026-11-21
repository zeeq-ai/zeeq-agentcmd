using System.Text.Json;

namespace Zeeq.AgentCmd.ControlPlane;

/// <summary>An operator-selected candidate; the server resolves its immutable content pins.</summary>
/// <param name="Id">Candidate identity unique in this run.</param>
/// <param name="Name">Display name.</param>
/// <param name="BuildId">Registered application build.</param>
/// <param name="PublicationId">Frozen evaluation snapshot.</param>
/// <param name="CommandId">Manifest command identity.</param>
/// <param name="VariantId">Variant within the snapshot.</param>
public sealed record JobCandidate(
    string Id,
    string Name,
    string BuildId,
    string PublicationId,
    string CommandId,
    string VariantId
);

/// <summary>Paid launch request; configuration saves never implicitly authorize execution.</summary>
/// <param name="Kind">Backtest or Reassess.</param>
/// <param name="DatasetId">Frozen dataset.</param>
/// <param name="BenchmarkId">Frozen benchmark.</param>
/// <param name="Candidates">Selected exact candidate identities.</param>
/// <param name="ExecutionApproved">Explicit approval for execution.</param>
/// <param name="SourceJobId">Parent job for reassessment.</param>
public sealed record CreateJobRequest(
    string Kind,
    string DatasetId,
    string BenchmarkId,
    IReadOnlyList<JobCandidate> Candidates,
    bool ExecutionApproved,
    string? SourceJobId = null
);

/// <summary>Job and its scoped durable work projection.</summary>
/// <param name="Job">Immutable launch content and mutable lifecycle state.</param>
/// <param name="Work">A bounded page of per-item status and retained result.</param>
/// <param name="Counts">Counts by durable status over all work items.</param>
/// <param name="Offset">Starting position of this page.</param>
/// <param name="Limit">Maximum number of items in this page.</param>
public sealed record JobProgress(
    ManagementResource Job,
    IReadOnlyList<JobWork> Work,
    IReadOnlyDictionary<string, long> Counts,
    int Offset,
    int Limit
);

/// <summary>Searchable execution evidence, including unsuccessful or output-free work.</summary>
/// <param name="Id">Work identity.</param>
/// <param name="CandidateId">Requested candidate.</param>
/// <param name="CaseId">Frozen case identity.</param>
/// <param name="RepeatIndex">Generation repeat.</param>
/// <param name="Status">Durable lifecycle state.</param>
/// <param name="Result">Exact retained completion receipt, when available.</param>
public sealed record JobWork(
    string Id,
    string CandidateId,
    string CaseId,
    int RepeatIndex,
    string Status,
    JsonElement? Result
);

public sealed partial class RelationalControlPlaneStore
{
    /// <summary>Validates frozen artifacts and creates a backtest and all work atomically.</summary>
    public Task<ManagementResource> CreateBacktestAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string name,
        CreateJobRequest request,
        CancellationToken cancellationToken = default
    ) => CreateBacktestCoreAsync(scope, actor, name, request, null, cancellationToken);

    /// <summary>Validates retained scoped assets and creates a backtest atomically.</summary>
    public Task<ManagementResource> CreateBacktestAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string name,
        CreateJobRequest request,
        IControlPlaneAssetStore assets,
        CancellationToken cancellationToken = default
    ) => CreateBacktestCoreAsync(scope, actor, name, request, assets, cancellationToken);

    private Task<ManagementResource> CreateBacktestCoreAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string name,
        CreateJobRequest request,
        IControlPlaneAssetStore? assetStore,
        CancellationToken cancellationToken
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                if (
                    request.Kind != "Backtest"
                    || !request.ExecutionApproved
                    || request.Candidates is not { Count: > 0 and <= 100 }
                    || request.Candidates.Any(candidate => candidate is null)
                    || request.Candidates.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count()
                        != request.Candidates.Count
                )
                {
                    throw new ManagementException("job_request_invalid");
                }
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
                if (
                    dataset.State != "Frozen"
                    || benchmark.State != "Frozen"
                    || Text(benchmark.Body, "datasetId") != dataset.Id
                )
                {
                    throw new ManagementException("job_frozen_protocol_required", 409);
                }
                if (
                    dataset.Body.ValueKind != JsonValueKind.Object
                    || benchmark.Body.ValueKind != JsonValueKind.Object
                    || !dataset.Body.TryGetProperty("cases", out var cases)
                    || cases.ValueKind != JsonValueKind.Array
                    || cases.GetArrayLength() is 0 or > 10000
                    || !benchmark.Body.TryGetProperty("repeats", out var repeatsValue)
                    || !repeatsValue.TryGetInt32(out var repeats)
                    || repeats is < 1 or > 100
                )
                {
                    throw new ManagementException("job_protocol_invalid");
                }
                if (
                    !benchmark.Body.TryGetProperty("criteria", out var criteria)
                    || criteria.ValueKind != JsonValueKind.Array
                    || criteria.GetArrayLength() == 0
                    || string.IsNullOrWhiteSpace(Text(benchmark.Body, "primaryCriterionId"))
                )
                {
                    throw new ManagementException("job_protocol_invalid");
                }
                var criterionIds = criteria.EnumerateArray().Select(c => Text(c, "id")).ToArray();
                if (
                    criterionIds.Any(string.IsNullOrWhiteSpace)
                    || criterionIds.Distinct(StringComparer.Ordinal).Count() != criterionIds.Length
                    || criterionIds.Count(id => id == Text(benchmark.Body, "primaryCriterionId"))
                        != 1
                )
                {
                    throw new ManagementException("job_protocol_invalid");
                }
                var caseList = cases.EnumerateArray().ToArray();
                var caseIds = caseList.Select(c => Text(c, "id")).ToArray();
                if (
                    caseList.Any(c =>
                        c.ValueKind != JsonValueKind.Object
                        || !c.TryGetProperty("input", out var input)
                        || input.ValueKind != JsonValueKind.Object
                        || (
                            c.TryGetProperty("assets", out var assets)
                            && (assets.ValueKind != JsonValueKind.Array)
                        )
                    )
                    || caseIds.Any(string.IsNullOrWhiteSpace)
                    || caseIds.Distinct(StringComparer.Ordinal).Count() != caseIds.Length
                    || (long)caseIds.Length * request.Candidates.Count * repeats > 100000
                )
                {
                    throw new ManagementException("job_cases_invalid");
                }
                foreach (var item in caseList)
                {
                    await VerifyCaseAssetsAsync(scope, item, assetStore, cancellationToken);
                }
                var pins = new List<object>();
                var candidatePins = new Dictionary<string, object>(StringComparer.Ordinal);
                var outputShapes = new List<string>();
                foreach (var candidate in request.Candidates)
                {
                    if (
                        string.IsNullOrWhiteSpace(candidate.BuildId)
                        || candidate.BuildId.StartsWith("judge:", StringComparison.Ordinal)
                        || string.IsNullOrWhiteSpace(candidate.PublicationId)
                        || string.IsNullOrWhiteSpace(candidate.Id)
                        || string.IsNullOrWhiteSpace(candidate.CommandId)
                        || string.IsNullOrWhiteSpace(candidate.VariantId)
                    )
                    {
                        throw new ManagementException("candidate_invalid");
                    }
                    var build = await ReadAsync(
                        db,
                        tx,
                        scope,
                        "builds",
                        candidate.BuildId,
                        cancellationToken
                    ) ?? throw new ManagementException("build_not_found", 404);
                    var publication = await ReadAsync(
                        db,
                        tx,
                        scope,
                        "publications",
                        candidate.PublicationId,
                        cancellationToken
                    ) ?? throw new ManagementException("publication_not_found", 404);
                    if (
                        publication.State != "Frozen"
                        || Text(publication.Body, "buildId") != build.Id
                        || !ContainsVariant(
                            publication.Body,
                            candidate.CommandId,
                            candidate.VariantId
                        )
                        || !MatchesBuild(build.Body, publication.Body, candidate.CommandId)
                    )
                    {
                        throw new ManagementException("candidate_pin_invalid", 409);
                    }
                    var outputSignature = OutputSignature(build, candidate.CommandId);
                    outputShapes.Add(
                        string.IsNullOrWhiteSpace(outputSignature)
                            ? "opaque:" + build.ContentHash + ":" + candidate.CommandId
                            : "declared:" + outputSignature
                    );
                    var candidatePin = new
                    {
                        candidate,
                        buildHash = build.ContentHash,
                        publicationHash = publication.ContentHash,
                        outputSignature,
                    };
                    pins.Add(candidatePin);
                    candidatePins.Add(candidate.Id, candidatePin);
                }
                RequireCommonShapeOrMappings(benchmark.Body, outputShapes);
                var id = Guid.NewGuid().ToString("N");
                var body = ContentIdentity.Normalize(
                    JsonSerializer.SerializeToElement(
                        new
                        {
                            kind = "Backtest",
                            purpose = "Backtest",
                            request.DatasetId,
                            datasetHash = dataset.ContentHash,
                            request.BenchmarkId,
                            benchmarkHash = benchmark.ContentHash,
                            request.Candidates,
                            pins,
                            repeats,
                            executionApproved = true,
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
                    name,
                    "Queued",
                    body,
                    cancellationToken
                );
                foreach (var candidate in request.Candidates)
                {
                    foreach (var item in caseList)
                    {
                        var caseId = Text(item, "id")!;
                        for (var repeat = 0; repeat < repeats; repeat++)
                        {
                            var workId = ContentIdentity.Hash(
                                JsonSerializer.Serialize(
                                    new[]
                                    {
                                        id,
                                        candidate.Id,
                                        caseId,
                                        repeat.ToString(
                                            System.Globalization.CultureInfo.InvariantCulture
                                        ),
                                    }
                                )
                            );
                            var workBody = ContentIdentity.Normalize(
                                JsonSerializer.SerializeToElement(
                                    new
                                    {
                                        jobId = id,
                                        purpose = "Backtest",
                                        candidate,
                                        @case = item,
                                        repeat,
                                        datasetHash = dataset.ContentHash,
                                        benchmarkHash = benchmark.ContentHash,
                                        pins = new[] { candidatePins[candidate.Id] },
                                    },
                                    Json
                                )
                            );
                            await ExecuteAsync(
                                db,
                                tx,
                                "INSERT INTO cp_work(scope,id,job_id,build_id,candidate_id,case_id,repeat_index,status,request) VALUES(@scope,@id,@job,@build,@candidate,@case,@repeat,'Queued',@request)",
                                cancellationToken,
                                ("scope", scope.Key),
                                ("id", workId),
                                ("job", id),
                                ("build", candidate.BuildId),
                                ("candidate", candidate.Id),
                                ("case", caseId),
                                ("repeat", repeat),
                                ("request", workBody)
                            );
                        }
                    }
                }
                return job;
            },
            cancellationToken
        );

    internal static async Task VerifyCaseAssetsAsync(
        WorkspaceScope scope,
        JsonElement item,
        IControlPlaneAssetStore? assets,
        CancellationToken cancellationToken
    )
    {
        if (!item.TryGetProperty("assets", out var values))
        {
            return;
        }
        if (values.ValueKind != JsonValueKind.Array)
        {
            throw new ManagementException("case_assets_invalid");
        }
        foreach (var value in values.EnumerateArray())
        {
            var hash =
                value.ValueKind == JsonValueKind.String ? value.GetString() : Text(value, "hash");
            if (string.IsNullOrWhiteSpace(hash) || assets is null)
            {
                throw new ManagementException("case_asset_unavailable", 409);
            }
            await using var retained = await assets.OpenAsync(scope, hash, cancellationToken);
        }
    }

    private static bool ContainsVariant(JsonElement body, string commandId, string variantId) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty("commands", out var commands)
        && commands.ValueKind == JsonValueKind.Array
        && commands
            .EnumerateArray()
            .Any(command =>
                Text(command, "commandId") == commandId
                && command.TryGetProperty("variants", out var variants)
                && variants.ValueKind == JsonValueKind.Array
                && variants.EnumerateArray().Any(variant => Text(variant, "id") == variantId)
            );

    private static bool MatchesBuild(JsonElement build, JsonElement publication, string commandId)
    {
        if (
            build.ValueKind != JsonValueKind.Object
            || publication.ValueKind != JsonValueKind.Object
        )
        {
            return false;
        }
        if (
            build.TryGetProperty("manifestJson", out var raw)
            && raw.ValueKind == JsonValueKind.String
        )
        {
            using var document = JsonDocument.Parse(raw.GetString()!);
            build = document.RootElement.Clone();
        }
        else if (build.TryGetProperty("manifest", out var nested))
        {
            build = nested;
        }
        if (build.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        if (
            !build.TryGetProperty("commands", out var commands)
            || commands.ValueKind != JsonValueKind.Array
            || !publication.TryGetProperty("commands", out var published)
            || published.ValueKind != JsonValueKind.Array
        )
        {
            return false;
        }
        var matches = commands.EnumerateArray().Where(c => Text(c, "id") == commandId).ToArray();
        var selected = published
            .EnumerateArray()
            .Where(c => Text(c, "commandId") == commandId)
            .ToArray();
        if (
            matches.Length != 1
            || selected.Length != 1
            || string.IsNullOrWhiteSpace(Text(matches[0], "contractSignature"))
            || Text(matches[0], "contractSignature") != Text(selected[0], "contractSignature")
        )
        {
            return false;
        }
        return matches[0].TryGetProperty("revision", out var revision)
            && revision.TryGetInt32(out var r)
            && selected[0].TryGetProperty("commandRevision", out var publishedRevision)
            && publishedRevision.TryGetInt32(out var p)
            && r == p;
    }

    /// <summary>Reads a job with bounded per-item evidence in the authorized scope.</summary>
    public Task<JobProgress> GetJobAsync(
        WorkspaceScope scope,
        string id,
        CancellationToken cancellationToken = default
    ) => GetJobPageAsync(scope, id, 0, 100, cancellationToken);

    /// <summary>Reads a bounded page and status counts for one authorized job.</summary>
    public async Task<JobProgress> GetJobPageAsync(
        WorkspaceScope scope,
        string id,
        int offset,
        int limit,
        CancellationToken cancellationToken = default
    )
    {
        if (offset < 0 || limit is < 1 or > 250)
        {
            throw new ManagementException("page_invalid");
        }
        var job = await ReadAsync(scope, "jobs", id, cancellationToken)
            ?? throw new ManagementException("not_found", 404);
        await using var db = await OpenAsync(cancellationToken);
        var work = new List<JobWork>();
        await using (
            var command = Command(
                db,
                null,
                "SELECT id,candidate_id,case_id,repeat_index,status,result FROM cp_work WHERE scope=@scope AND job_id=@job ORDER BY id LIMIT @limit OFFSET @offset",
                ("scope", scope.Key),
                ("job", id),
                ("limit", limit),
                ("offset", offset)
            )
        )
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                work.Add(
                    new(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetInt32(3),
                        reader.GetString(4),
                        reader.IsDBNull(5) ? null : Element(reader.GetString(5))
                    )
                );
            }
        }
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        await using (
            var command = Command(
                db,
                null,
                "SELECT status,COUNT(*) FROM cp_work WHERE scope=@scope AND job_id=@job GROUP BY status",
                ("scope", scope.Key),
                ("job", id)
            )
        )
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                counts.Add(reader.GetString(0), reader.GetInt64(1));
            }
        }
        return new(job, work, counts, offset, limit);
    }

    /// <summary>Cancels unsent admissions while preserving any potentially spent attempt.</summary>
    public Task<ManagementResource> CancelJobAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string id,
        long revision,
        CancellationToken cancellationToken = default
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                var job = await ReadAsync(db, tx, scope, "jobs", id, cancellationToken)
                    ?? throw new ManagementException("not_found", 404);
                RequireRevision(job, revision);
                if (job.State != "Queued")
                {
                    throw new ManagementException("job_state_invalid", 409);
                }
                var updated = await ReplaceAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    job,
                    job.Name,
                    "Cancelled",
                    ContentIdentity.Normalize(job.Body),
                    "job.cancelled",
                    cancellationToken
                );
                await ExecuteAsync(
                    db,
                    tx,
                    "UPDATE cp_work SET status=CASE WHEN EXISTS (SELECT 1 FROM cp_attempts a WHERE a.scope=cp_work.scope AND a.work_id=cp_work.id) THEN 'Held' ELSE 'Cancelled' END WHERE scope=@scope AND job_id=@job AND status IN ('Queued','Claimed','Blocked')",
                    cancellationToken,
                    ("scope", scope.Key),
                    ("job", id)
                );
                return updated;
            },
            cancellationToken
        );

    /// <summary>Requeues only blocked or cancelled work with no admitted send.</summary>
    public Task<ManagementResource> ResumeJobAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string id,
        long revision,
        CancellationToken cancellationToken = default
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                var job = await ReadAsync(db, tx, scope, "jobs", id, cancellationToken)
                    ?? throw new ManagementException("not_found", 404);
                RequireRevision(job, revision);
                if (job.State is not ("Cancelled" or "Queued"))
                {
                    throw new ManagementException("job_state_invalid", 409);
                }
                var updated = await ReplaceAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    job,
                    job.Name,
                    "Queued",
                    ContentIdentity.Normalize(job.Body),
                    "job.resumed",
                    cancellationToken
                );
                await ExecuteAsync(
                    db,
                    tx,
                    "UPDATE cp_work SET status='Queued',owner=NULL,lease_until=NULL WHERE scope=@scope AND job_id=@job AND status IN ('Cancelled','Blocked') AND NOT EXISTS (SELECT 1 FROM cp_attempts a WHERE a.scope=cp_work.scope AND a.work_id=cp_work.id)",
                    cancellationToken,
                    ("scope", scope.Key),
                    ("job", id)
                );
                return updated;
            },
            cancellationToken
        );
}
