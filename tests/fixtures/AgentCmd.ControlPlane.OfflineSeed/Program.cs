using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Zeeq.AgentCmd;
using Zeeq.AgentCmd.ControlPlane;

if (
    args.Length != 1
    || args[0] != "--seed"
    || Environment.GetEnvironmentVariable("AGENTCMD_OFFLINE_FIXTURE") != "1"
)
{
    throw new InvalidOperationException(
        "Offline seed requires --seed and AGENTCMD_OFFLINE_FIXTURE=1."
    );
}
await Fixture.RunAsync();

namespace OfflineFixture
{
    public sealed record Input(string Document);

    public sealed record Output([property: JsonPropertyName("value")] string Value);

    public sealed partial class Extract : PromptCommand<Input, Output>
    {
        public const string Id = "offline.fixture.extract";
        public const string Profile = "fixture";
        public const string UserTemplate = "Extract the supported value from {Document}";
    }
}

internal static class Fixture
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static JsonElement Element(object value) =>
        JsonSerializer.SerializeToElement(value, Json);

    internal static async Task RunAsync()
    {
        var root = Path.GetFullPath(
            Environment.GetEnvironmentVariable("AGENTCMD_OFFLINE_FIXTURE_ROOT")
                ?? "artifacts/control-plane/integration"
        );
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "management.db");
        var store = new RelationalControlPlaneStore(
            new ManagementDatabase("Sqlite", () =>
                new SqliteConnection($"Data Source={path};Default Timeout=10")
            )
        );
        var assets = new FileAssetStore(Path.Combine(root, "assets"));
        var scope = new WorkspaceScope("local", "dev", "default");
        var operatorActor = new ManagementActor("offline-fixture-operator");
        var workerActor = new ManagementActor("offline-fixture-worker");
        await store.InitializeAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var prefix = "offline-fixture-" + suffix;
        var manifest = JsonDocument
            .Parse(Zeeq.AgentCmd.Generated.AgentCmdManifestData.Structural)
            .RootElement.Clone();
        var commandManifest = manifest
            .GetProperty("commands")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == OfflineFixture.Extract.Id);
        var signature = commandManifest.GetProperty("contractSignature").GetString()!;
        var buildId = prefix + "-build";
        var build = await store.RegisterBuildAsync(
            scope,
            operatorActor,
            new(prefix + "-build", Element(new { buildId, manifest }), buildId)
        );
        var publicationIds = new Dictionary<string, string>();
        foreach (var variant in new[] { "baseline", "candidate" })
        {
            var body = Element(
                new
                {
                    buildId,
                    commands = new[]
                    {
                        new
                        {
                            commandId = OfflineFixture.Extract.Id,
                            commandRevision = 1,
                            contractSignature = signature,
                            experimentId = "fixture-experiment",
                            controlVariantId = variant,
                            variants = new[]
                            {
                                new
                                {
                                    id = variant,
                                    weight = 1,
                                    userTemplate = "Extract the supported value from {Document}",
                                    parameters = new { },
                                },
                            },
                        },
                    },
                }
            );
            var draft = await store.SaveDraftAsync(
                scope,
                operatorActor,
                "publications",
                new(prefix + "-" + variant, body)
            );
            publicationIds[variant] = (
                await store.FreezeAsync(
                    scope,
                    operatorActor,
                    "publications",
                    draft.Id,
                    draft.Revision
                )
            ).Id;
        }
        const string sourceText = "A and C are supported.";
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(sourceText));
        var asset = await assets.PutAsync(scope, source);
        var caseRows = Enumerable
            .Range(1, 10)
            .Select(number => new
            {
                id = "case-"
                    + number.ToString("D2", System.Globalization.CultureInfo.InvariantCulture),
                sourceFamilyId = prefix
                    + "-case-"
                    + number.ToString("D2", System.Globalization.CultureInfo.InvariantCulture),
                input = new { document = sourceText },
                assets = new[] { asset.Hash },
                reference = new
                {
                    value = new[] { number == 1 ? "A" : "C" },
                    completeness = number == 1 ? "Partial" : "Exhaustive",
                },
                sourceCoverage = "Complete",
                exposed = false,
            })
            .ToArray();
        var dataset = await store.SaveDraftAsync(
            scope,
            operatorActor,
            "datasets",
            new(prefix + "-dataset", Element(new { purpose = "Calibration", cases = caseRows }))
        );
        dataset = await store.FreezeAsync(
            scope,
            operatorActor,
            "datasets",
            dataset.Id,
            dataset.Revision
        );
        var judgeConfig = await store.SaveDraftAsync(
            scope,
            operatorActor,
            "judge-configurations",
            new(
                prefix + "-judge",
                Element(
                    new
                    {
                        routeId = "offline-fixture-judge",
                        modelId = "fake-judge",
                        promptVersion = "1",
                        resultSchemaVersion = "1",
                        maxOutputTokens = 128,
                        rubric = "Assess the requested target against the provided reference and source.",
                        calibrationPolicy = new
                        {
                            minimumSamples = 10,
                            maximumScoreDifference = 0.2,
                            minimumAgreement = 0.8,
                        },
                        rubricLevels = new[] { new { name = "supported", score = 1.0 } },
                    }
                )
            )
        );
        judgeConfig = await store.FreezeAsync(
            scope,
            operatorActor,
            "judge-configurations",
            judgeConfig.Id,
            judgeConfig.Revision
        );
        var benchmarkBody = Element(
            new
            {
                datasetId = dataset.Id,
                primaryCriterionId = "exact",
                repeats = 1,
                criteria = new object[]
                {
                    new
                    {
                        id = "exact",
                        version = 1,
                        name = "Exact reference",
                        scope = "Field",
                        outputBinding = "/value",
                        referenceBinding = "/0",
                        scorer = new { kind = "Exact", version = "1" },
                    },
                    new
                    {
                        id = "human",
                        version = 1,
                        name = "Human review",
                        scope = "Field",
                        outputBinding = "/value",
                        scorer = new { kind = "Human", version = "1" },
                    },
                    new
                    {
                        id = "judge",
                        version = 1,
                        name = "Source grounding",
                        scope = "Field",
                        outputBinding = "/value",
                        referenceBinding = "/0",
                        sourceBinding = "/document",
                        scorer = new
                        {
                            kind = "Llm",
                            version = "1",
                            judgeConfigId = judgeConfig.Id,
                        },
                    },
                },
                guardrails = new object[]
                {
                    new
                    {
                        id = "coverage",
                        metric = "ValidOutputRate",
                        @operator = "Gte",
                        threshold = 1.0,
                        minimumCoverage = 1.0,
                        required = true,
                    },
                },
            }
        );
        var benchmark = await store.SaveDraftAsync(
            scope,
            operatorActor,
            "benchmarks",
            new(prefix + "-benchmark", benchmarkBody)
        );
        benchmark = await store.FreezeAsync(
            scope,
            operatorActor,
            "benchmarks",
            benchmark.Id,
            benchmark.Revision
        );
        var candidates = new[]
        {
            new JobCandidate(
                "baseline",
                "Baseline",
                buildId,
                publicationIds["baseline"],
                OfflineFixture.Extract.Id,
                "baseline"
            ),
            new JobCandidate(
                "candidate",
                "Candidate",
                buildId,
                publicationIds["candidate"],
                OfflineFixture.Extract.Id,
                "candidate"
            ),
        };
        var job = await store.CreateBacktestAsync(
            scope,
            operatorActor,
            prefix + "-backtest",
            new("Backtest", dataset.Id, benchmark.Id, candidates, true),
            assets
        );
        var noSend = (
            await store.ClaimWorkAsync(
                scope,
                workerActor,
                buildId,
                workerActor.Subject,
                TimeSpan.FromMinutes(2)
            )
        )!;
        await store.HoldPreflightFailureAsync(
            scope,
            workerActor,
            noSend,
            "offline_fixture_recovery"
        );
        job = await store.ResumeJobAsync(scope, operatorActor, job.Id, job.Revision);
        var ledger = new InMemoryFeedbackLedger();
        var runtimeActor = new Actor("local", "offline-fixture-worker");
        var runId = ledger.CreateRun(runtimeActor);
        var workers =
            new Dictionary<
                string,
                TypedWorkerAdapter<OfflineFixture.Input, OfflineFixture.Output>
            >();
        foreach (var candidate in candidates)
        {
            var publication = (
                await store.ReadAsync(scope, "publications", candidate.PublicationId)
            )!;
            ExperimentRevision.TryCreate(publication.ContentHash, out var revision);
            var variant = new ExperimentVariant(
                candidate.VariantId,
                1,
                new(
                    new TemplateChange.Inherited(),
                    new TemplateChange.Replaced("Extract the supported value from {Document}")
                ),
                new Dictionary<string, JsonElement>(),
                null
            );
            var view = ExperimentCatalogView.Freeze(
                OfflineFixture.Extract.Id,
                1,
                signature,
                revision!,
                "fixture-experiment",
                candidate.VariantId,
                [variant]
            );
            var snapshot = new ExperimentSnapshot(
                revision!,
                new Dictionary<string, ExperimentCatalogView> { [OfflineFixture.Extract.Id] = view }
            );
            var client = new FakeClient(candidate.VariantId == "baseline" ? "A" : "C");
            var runtime = CommandRuntime.Create(
                () => client,
                new Dictionary<string, Profile> { ["fixture"] = new("fake-model", 0) },
                ledger,
                () => runtimeActor,
                experiments: new ExperimentRuntimeOptions(
                    new ExperimentResolver(new SnapshotSource(snapshot))
                )
            );
            workers.Add(
                candidate.Id,
                new(
                    new OfflineFixture.Extract(runtime),
                    (JsonTypeInfo<OfflineFixture.Input>)
                        Json.GetTypeInfo(typeof(OfflineFixture.Input)),
                    buildId,
                    build.ContentHash,
                    new Preparation(
                        revision!,
                        runId,
                        publication.ContentHash,
                        build.ContentHash,
                        candidate.VariantId
                    ),
                    assets
                )
            );
        }
        for (var i = 0; i < candidates.Length * caseRows.Length; i++)
        {
            var claim = (
                await store.ClaimWorkAsync(
                    scope,
                    workerActor,
                    buildId,
                    workerActor.Subject,
                    TimeSpan.FromMinutes(2)
                )
            )!;
            if (i == 0 && (claim.WorkId != noSend.WorkId || claim.Epoch <= noSend.Epoch))
            {
                throw new InvalidOperationException(
                    "Unsent work did not recover under a new claim epoch."
                );
            }
            var candidateId = claim.Request.GetProperty("candidate").GetProperty("id").GetString()!;
            var adapter = workers[candidateId];
            var check = await adapter.CheckAsync(scope, claim, default);
            if (!check.Passed)
            {
                throw new InvalidOperationException("Fixture typed preflight failed.");
            }
            await store.RecordPreflightAsync(scope, workerActor, claim, check);
            var outcome = await adapter.ExecuteAndCompleteAsync(scope, workerActor, claim, store);
            if (outcome is not Outcome<OfflineFixture.Output>.Success)
            {
                throw new InvalidOperationException("Fixture command failed.");
            }
        }
        var progress = await store.GetJobAsync(scope, job.Id);
        if (
            progress.Job.State != "Finished"
            || progress.Work.Count != candidates.Length * caseRows.Length
            || FakeClient.Calls != candidates.Length * caseRows.Length
            || progress.Work.Any(item =>
                item.Status != "Finished"
                || item.Result is null
                || item.Result.Value.GetProperty("storageStatus").GetString() != "Stored"
                || !item.Result.Value.GetProperty("comparisonEligible").GetBoolean()
            )
        )
        {
            throw new InvalidOperationException("Fixture exact execution evidence is incomplete.");
        }
        var executions = progress.Work.Select(item => item.Result!.Value).ToArray();
        var exactAssessments = new List<string>();
        var humanAssessments = new List<string>();
        foreach (var execution in executions)
        {
            var executionId = execution.GetProperty("executionId").GetString()!;
            exactAssessments.Add(
                (
                    await store.AssessDeterministicAsync(
                        scope,
                        operatorActor,
                        new(executionId, "exact")
                    )
                ).Id
            );
            humanAssessments.Add(
                (
                    await store.AssessHumanAsync(
                        scope,
                        operatorActor,
                        new(
                            executionId,
                            "human",
                            execution.GetProperty("candidateId").GetString() == "candidate"
                                ? 1.0
                                : 0.5,
                            "Offline human assessment."
                        )
                    )
                ).Id
            );
        }
        var comparison = await store.SaveComparisonAsync(
            scope,
            operatorActor,
            prefix + "-comparison",
            new(exactAssessments, "baseline")
        );
        var summary = await store.ReadComparisonSummaryAsync(scope, comparison.Id);
        if (
            summary.Criteria.Single(item => item.CriterionId == "exact").CommonCaseIds.Count
            != caseRows.Length - 1
        )
        {
            throw new InvalidOperationException("Fixture comparison lacks matched cases.");
        }
        var exactSummary = summary.Criteria.Single(item => item.CriterionId == "exact");
        foreach (var candidate in summary.Candidates)
        {
            var metric = exactSummary.Candidates.Single(item => item.CandidateKey == candidate.Key);
            if (
                metric.ScoredCases != caseRows.Length - 1
                || metric.MatchedMean != (candidate.CandidateId == "baseline" ? 0.0 : 1.0)
            )
            {
                throw new InvalidOperationException("Fixture matched score is incorrect.");
            }
        }
        if (summary.Guardrails.Any(rule => rule.Status != "Unknown"))
        {
            throw new InvalidOperationException("Fixture guardrail state was invented.");
        }
        var candidateExecutions = executions
            .Where(item => item.GetProperty("candidateId").GetString() == "candidate")
            .ToArray();
        var candidateExecution = candidateExecutions.Single(item =>
            item.GetProperty("caseId").GetString() == "case-01"
        );
        var candidateExecutionId = candidateExecution.GetProperty("executionId").GetString()!;
        var policy = new ScopedJudgePolicy(
            "offline-fixture-judge",
            "fake-judge",
            "1",
            "1",
            128,
            "Assess the requested target against the provided reference and source."
        );
        var judgeAdapter = new JudgeWorkerAdapter(
            new FakeJudge(),
            policy,
            judgeConfig.Id,
            judgeConfig.ContentHash
        );
        var judgeJobIds = new List<string>();
        var judgeAssessmentIds = new List<string>();
        var calibrationPairs = new List<CalibrationPair>();
        var judgeByExecution = new Dictionary<string, string>();
        foreach (var execution in candidateExecutions)
        {
            var executionId = execution.GetProperty("executionId").GetString()!;
            var human = await store.AssessHumanAsync(
                scope,
                operatorActor,
                new(executionId, "judge", 1.0, "Offline source supports the candidate.")
            );
            var judgeJob = await store.CreateJudgeEvaluationAsync(
                scope,
                operatorActor,
                new(executionId, "judge", true)
            );
            judgeJobIds.Add(judgeJob.Id);
            var judgeClaim = (
                await store.ClaimWorkAsync(
                    scope,
                    workerActor,
                    "judge:" + judgeConfig.Id,
                    workerActor.Subject,
                    TimeSpan.FromMinutes(2)
                )
            )!;
            await store.RecordPreflightAsync(
                scope,
                workerActor,
                judgeClaim,
                await judgeAdapter.CheckAsync(scope, judgeClaim, default)
            );
            var judgeContext = new CommandExecutionContext(
                Guid.NewGuid(),
                "offline-fixture-judge",
                1,
                judgeConfig.ContentHash,
                new CapturedContext(null, null, ContextSource.Explicit, false)
            );
            var judged = await judgeAdapter.ExecuteAsync(
                scope,
                workerActor,
                judgeClaim,
                store,
                judgeContext
            );
            if (
                judged.State != "Scored"
                || judged.Body.GetProperty("findings")[0].GetProperty("category").GetString()
                    != (
                        execution.GetProperty("caseId").GetString() == "case-01"
                            ? "SupportedAddition"
                            : "Matched"
                    )
            )
            {
                throw new InvalidOperationException("Fixture judge evidence is invalid.");
            }
            judgeAssessmentIds.Add(judged.Id);
            judgeByExecution.Add(executionId, judged.Id);
            calibrationPairs.Add(new(human.Id, judged.Id));
        }
        var calibration = await store.CalibrateJudgeAsync(
            scope,
            operatorActor,
            prefix + "-calibration",
            new(judgeConfig.Id, "judge", calibrationPairs, 0.2, 0.8)
        );
        if (calibration.State != "Admitted")
        {
            throw new InvalidOperationException("Fixture calibration did not pass.");
        }
        var reviewCase = candidateExecution.GetProperty("caseId").GetString()!;
        var review = await store.CreateReviewAsync(
            scope,
            operatorActor,
            new(
                prefix + "-reference-gap",
                Element(
                    new
                    {
                        kind = "ReferenceGap",
                        executionId = candidateExecutionId,
                        assessmentId = judgeByExecution[candidateExecutionId],
                        datasetId = dataset.Id,
                        caseId = reviewCase,
                        criterionId = "judge",
                        proposedValue = "C",
                        sourceLocator = new
                        {
                            assetHash = asset.Hash,
                            start = 6,
                            end = 7,
                            quote = "C",
                        },
                    }
                )
            )
        );
        review = await store.ClaimReviewAsync(scope, operatorActor, review.Id, review.Revision);
        var datasetDraft = await store.SaveDraftAsync(
            scope,
            operatorActor,
            "datasets",
            new(prefix + "-revised-draft", dataset.Body)
        );
        review = await store.AcceptReferenceReviewAsync(
            scope,
            operatorActor,
            review.Id,
            review.Revision,
            new("Accept", "Offline reviewer verified the retained source.", datasetDraft.Id),
            assets
        );
        datasetDraft = (await store.ReadAsync(scope, "datasets", datasetDraft.Id))!;
        var revisedDataset = await store.FreezeAsync(
            scope,
            operatorActor,
            "datasets",
            datasetDraft.Id,
            datasetDraft.Revision
        );
        var originalReference = (await store.ReadAsync(scope, "datasets", dataset.Id))!
            .Body.GetProperty("cases")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == reviewCase)
            .GetProperty("reference")
            .GetProperty("value");
        var revisedReference = revisedDataset
            .Body.GetProperty("cases")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == reviewCase)
            .GetProperty("reference")
            .GetProperty("value");
        if (
            review.State != "AcceptedIntoDraft"
            || originalReference.GetArrayLength() != 1
            || revisedReference.GetArrayLength() != 2
        )
        {
            throw new InvalidOperationException(
                "Fixture reference adjudication changed the original or missed the draft."
            );
        }
        var revisedBody = JsonNode.Parse(benchmark.Body.GetRawText())!.AsObject();
        revisedBody["datasetId"] = revisedDataset.Id;
        var revisedBenchmark = await store.SaveDraftAsync(
            scope,
            operatorActor,
            "benchmarks",
            new(prefix + "-revised-benchmark", JsonSerializer.SerializeToElement(revisedBody, Json))
        );
        revisedBenchmark = await store.FreezeAsync(
            scope,
            operatorActor,
            "benchmarks",
            revisedBenchmark.Id,
            revisedBenchmark.Revision
        );
        var reassess = await store.CreateReassessmentAsync(
            scope,
            operatorActor,
            prefix + "-reassessment",
            new("Reassess", revisedDataset.Id, revisedBenchmark.Id, candidates, true, job.Id)
        );
        var reassessments = new List<string>();
        foreach (var execution in executions)
        {
            reassessments.Add(
                (
                    await store.AssessDeterministicAsync(
                        scope,
                        operatorActor,
                        new(execution.GetProperty("executionId").GetString()!, "exact", reassess.Id)
                    )
                ).Id
            );
        }
        if (
            FakeClient.Calls != candidates.Length * caseRows.Length
            || reassessments.Count != executions.Length
        )
        {
            throw new InvalidOperationException(
                "Reassessment regenerated a candidate or missed an original output."
            );
        }
        var ids = new
        {
            prefix,
            scope,
            buildId,
            publications = publicationIds,
            datasetId = dataset.Id,
            benchmarkId = benchmark.Id,
            jobId = job.Id,
            executionIds = executions
                .Select(item => item.GetProperty("executionId").GetString())
                .ToArray(),
            exactAssessmentIds = exactAssessments,
            humanAssessmentIds = humanAssessments,
            comparisonId = comparison.Id,
            judgeConfigurationId = judgeConfig.Id,
            judgeJobIds,
            judgeAssessmentIds,
            calibrationId = calibration.Id,
            reviewId = review.Id,
            revisedDatasetId = revisedDataset.Id,
            revisedBenchmarkId = revisedBenchmark.Id,
            reassessmentJobId = reassess.Id,
            reassessmentIds = reassessments,
            assetHash = asset.Hash,
        };
        await File.WriteAllTextAsync(
            Path.Combine(root, "fixture-ids.json"),
            JsonSerializer.Serialize(ids, new JsonSerializerOptions(Json) { WriteIndented = true })
        );
        Console.WriteLine(
            "Offline fixture created. IDs: artifacts/control-plane/integration/fixture-ids.json"
        );
    }

    private sealed class SnapshotSource(ExperimentSnapshot snapshot) : IExperimentSnapshotSource
    {
        public ValueTask<SourceResult<ExperimentSnapshot>> GetAsync(
            ExperimentLookup lookup,
            CancellationToken cancellationToken
        ) =>
            ValueTask.FromResult<SourceResult<ExperimentSnapshot>>(
                new SourceResult<ExperimentSnapshot>.Found(snapshot)
            );
    }

    private sealed class Preparation(
        ExperimentRevision revision,
        Guid runId,
        string publicationHash,
        string buildHash,
        string variantId
    ) : ITypedTreatmentPreparation<OfflineFixture.Input>
    {
        public Task<PreparedWorkerTreatment> PrepareAsync(
            WorkspaceScope scope,
            OfflineFixture.Input input,
            JsonElement pinnedRequest,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new PreparedWorkerTreatment(
                    revision,
                    "fixture-experiment",
                    variantId,
                    publicationHash,
                    buildHash,
                    runId
                )
            );
    }

    private sealed class FakeClient(string value) : IChatClient
    {
        public static int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Calls++;
            return Task.FromResult(
                new ChatResponse(
                    new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(new { value }))
                )
            );
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class FakeJudge : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            using var document = JsonDocument.Parse(
                messages.Last(message => message.Role == ChatRole.User).Text!
            );
            var reference = document.RootElement.GetProperty("Reference").GetString();
            var text =
                reference == "A"
                    ? "{\"rubricLevel\":\"supported\",\"findings\":[{\"category\":\"SupportedAddition\",\"candidatePointer\":\"\",\"sourceLocator\":{\"start\":6,\"end\":7,\"quote\":\"C\"}}]}"
                    : "{\"rubricLevel\":\"supported\",\"findings\":[{\"category\":\"Matched\",\"candidatePointer\":\"\",\"referencePointer\":\"\"}]}";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
