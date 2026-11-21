using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd;
using Zeeq.AgentCmd.ControlPlane;

namespace AgentCmd.ControlPlane.Tests;

public class JobTransitionsTests
{
    private static JsonElement J(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static readonly WorkspaceScope Scope = new("app", "test", "one");
    private static readonly ManagementActor Actor = new("worker");

    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal static async Task<(
        RelationalControlPlaneStore Store,
        Clock Clock,
        ManagementResource Job
    )> CreateAsync(string completeness = "Exhaustive")
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "agentcmd-jobs-" + Guid.NewGuid().ToString("N") + ".db"
        );
        var clock = new Clock();
        var store = new RelationalControlPlaneStore(
            new ManagementDatabase("Sqlite", () => new SqliteConnection($"Data Source={path}")),
            clock
        );
        await store.InitializeAsync();
        await store.RegisterBuildAsync(
            Scope,
            Actor,
            new(
                "build",
                J(
                    "{\"buildId\":\"build\",\"commands\":[{\"id\":\"c\",\"revision\":1,\"contractSignature\":\"sig\"}]}"
                ),
                "build"
            )
        );
        var publication = await store.SaveDraftAsync(
            Scope,
            Actor,
            "publications",
            new(
                "p",
                J(
                    "{\"buildId\":\"build\",\"commands\":[{\"commandId\":\"c\",\"commandRevision\":1,\"contractSignature\":\"sig\",\"variants\":[{\"id\":\"v\"}]}]}"
                )
            )
        );
        publication = await store.FreezeAsync(
            Scope,
            Actor,
            "publications",
            publication.Id,
            publication.Revision
        );
        var dataset = await store.SaveDraftAsync(
            Scope,
            Actor,
            "datasets",
            new(
                "d",
                JsonSerializer.SerializeToElement(
                    new
                    {
                        purpose = "Calibration",
                        cases = new[]
                        {
                            new
                            {
                                id = "case",
                                input = new { value = 1 },
                                reference = new { value = 10, completeness },
                            },
                        },
                    }
                )
            )
        );
        dataset = await store.FreezeAsync(Scope, Actor, "datasets", dataset.Id, dataset.Revision);
        var benchmark = await store.SaveDraftAsync(
            Scope,
            Actor,
            "benchmarks",
            new(
                "b",
                JsonSerializer.SerializeToElement(
                    new
                    {
                        datasetId = dataset.Id,
                        primaryCriterionId = "numeric",
                        repeats = 1,
                        criteria = new object[]
                        {
                            new
                            {
                                id = "numeric",
                                version = 1,
                                outputBinding = "",
                                referenceBinding = "",
                                scorer = new
                                {
                                    kind = "Numeric",
                                    tolerance = 0.5,
                                    version = "1",
                                },
                            },
                            new
                            {
                                id = "human",
                                version = 1,
                                outputBinding = "",
                                scorer = new { kind = "Human", version = "rubric-1" },
                            },
                        },
                    }
                )
            )
        );
        benchmark = await store.FreezeAsync(
            Scope,
            Actor,
            "benchmarks",
            benchmark.Id,
            benchmark.Revision
        );
        var job = await store.CreateBacktestAsync(
            Scope,
            Actor,
            "job",
            new(
                "Backtest",
                dataset.Id,
                benchmark.Id,
                [new("candidate", "Candidate", "build", publication.Id, "c", "v")],
                true
            )
        );
        return (store, clock, job);
    }

    private static Task PassPreflightAsync(RelationalControlPlaneStore store, WorkClaim claim) =>
        store.RecordPreflightAsync(
            Scope,
            new ManagementActor(claim.Owner),
            claim,
            new WorkerPreflightResult(
                true,
                ContentIdentity.Hash(ContentIdentity.Normalize(claim.Request))
            )
        );

    [Test]
    public async Task ClaimRaceAndLateReceiptAreFencedAndIdempotent()
    {
        var (store, clock, job) = await CreateAsync();
        var claims = await Task.WhenAll(
            store.ClaimWorkAsync(Scope, Actor, "build", "one", TimeSpan.FromSeconds(10)),
            store.ClaimWorkAsync(Scope, Actor, "build", "two", TimeSpan.FromSeconds(10))
        );
        await Assert.That(claims.Count(x => x is not null)).IsEqualTo(1);
        var claim = claims.Single(x => x is not null)!;
        await PassPreflightAsync(store, claim);
        await store.StartAttemptAsync(
            Scope,
            Actor,
            claim.WorkId,
            claim.Owner,
            claim.Epoch,
            "attempt"
        );
        clock.Now = clock.Now.AddMinutes(1);
        await Assert
            .That(
                await store.ClaimWorkAsync(Scope, Actor, "build", "new", TimeSpan.FromSeconds(10))
            )
            .IsNull();
        await store.FinishAttemptAsync(
            Scope,
            Actor,
            claim.WorkId,
            claim.Owner,
            claim.Epoch,
            "attempt",
            J("{\"outcome\":\"failed\"}")
        );
        await store.FinishAttemptAsync(
            Scope,
            Actor,
            claim.WorkId,
            claim.Owner,
            claim.Epoch,
            "attempt",
            J("{\"outcome\":\"failed\"}")
        );
        await Assert
            .That(
                await store.ClaimWorkAsync(Scope, Actor, "build", "new", TimeSpan.FromSeconds(10))
            )
            .IsNull();
        var page = await store.GetJobPageAsync(Scope, job.Id, 1, 1);
        await Assert.That(page.Work.Count).IsEqualTo(0);
        await Assert.That(page.Counts["Claimed"]).IsEqualTo(1);
        var status = (await store.GetJobAsync(Scope, job.Id)).Work.Single().Status;
        await Assert.That(status).IsEqualTo("Claimed");
    }

    [Test]
    public async Task ExpiredUnsentClaimCanBeReclaimedAndOtherScopeCannotRead()
    {
        var (store, clock, job) = await CreateAsync();
        var first = (
            await store.ClaimWorkAsync(Scope, Actor, "build", "old", TimeSpan.FromSeconds(1))
        )!;
        clock.Now = clock.Now.AddSeconds(2);
        var second = (
            await store.ClaimWorkAsync(Scope, Actor, "build", "new", TimeSpan.FromSeconds(2))
        )!;
        await Assert.That(second.Epoch).IsGreaterThan(first.Epoch);
        var rejected = false;
        try
        {
            await store.StartAttemptAsync(
                Scope,
                Actor,
                first.WorkId,
                first.Owner,
                first.Epoch,
                "stale"
            );
        }
        catch (ManagementException error)
        {
            rejected = error.Code == "claim_stale";
        }
        await Assert.That(rejected).IsTrue();
        await Assert
            .That(await store.ReadAsync(new WorkspaceScope("app", "test", "other"), "jobs", job.Id))
            .IsNull();
    }

    [Test]
    public async Task SequentialAttemptsUseOneClaim()
    {
        var (store, _, _) = await CreateAsync();
        var claim = (
            await store.ClaimWorkAsync(Scope, Actor, "build", "worker", TimeSpan.FromMinutes(1))
        )!;
        await PassPreflightAsync(store, claim);
        await store.StartAttemptAsync(Scope, Actor, claim.WorkId, claim.Owner, claim.Epoch, "one");
        await store.FinishAttemptAsync(
            Scope,
            Actor,
            claim.WorkId,
            claim.Owner,
            claim.Epoch,
            "one",
            J("{}")
        );
        await store.StartAttemptAsync(Scope, Actor, claim.WorkId, claim.Owner, claim.Epoch, "two");
        await store.FinishAttemptAsync(
            Scope,
            Actor,
            claim.WorkId,
            claim.Owner,
            claim.Epoch,
            "two",
            J("{}")
        );
        await Assert
            .That(
                (await store.GetJobAsync(Scope, claim.Request.GetProperty("jobId").GetString()!))
                    .Work.Single()
                    .Status
            )
            .IsEqualTo("Claimed");
    }

    [Test]
    [Arguments("Exhaustive", "Scored")]
    [Arguments("Partial", "Unmapped")]
    public async Task AssessmentUsesStoredValueAndReferenceCompleteness(
        string completeness,
        string expected
    )
    {
        var (store, _, job) = await CreateAsync(completeness);
        var claim = (
            await store.ClaimWorkAsync(Scope, Actor, "build", "worker", TimeSpan.FromMinutes(1))
        )!;
        await PassPreflightAsync(store, claim);
        await store.StartAttemptAsync(
            Scope,
            Actor,
            claim.WorkId,
            claim.Owner,
            claim.Epoch,
            "attempt"
        );
        await store.FinishAttemptAsync(
            Scope,
            Actor,
            claim.WorkId,
            claim.Owner,
            claim.Epoch,
            "attempt",
            J("{}")
        );
        var pin = claim.Request.GetProperty("pins")[0];
        var candidate = pin.GetProperty("candidate");
        var receipt = JsonSerializer.SerializeToElement(
            new
            {
                jobId = job.Id,
                workId = claim.WorkId,
                candidateId = "candidate",
                caseId = "case",
                repeatIndex = 0,
                buildId = "build",
                publicationId = candidate.GetProperty("publicationId").GetString(),
                publicationHash = pin.GetProperty("publicationHash").GetString(),
                commandId = "c",
                variantId = "v",
                purpose = "Backtest",
                role = "Primary",
                executionId = "11111111-1111-1111-1111-111111111111",
                outcome = "Success",
                storageStatus = "Stored",
                value = 10.4,
                outputRef = new
                {
                    runId = Guid.NewGuid(),
                    artifactId = Guid.NewGuid(),
                    versionId = Guid.NewGuid(),
                    version = 1,
                    producer = new Dictionary<string, string>
                    {
                        ["$producer"] = "command",
                        ["id"] = "11111111-1111-1111-1111-111111111111",
                    },
                },
            }
        );
        await store.CompleteWorkAsync(
            Scope,
            Actor,
            claim.WorkId,
            claim.Owner,
            claim.Epoch,
            receipt
        );
        var assessment = await store.AssessDeterministicAsync(
            Scope,
            Actor,
            new("11111111-1111-1111-1111-111111111111", "numeric")
        );
        await Assert.That(assessment.State).IsEqualTo(expected);
        var human = await store.AssessHumanAsync(
            Scope,
            Actor,
            new("11111111-1111-1111-1111-111111111111", "human", 0.75, "Supported by review")
        );
        await Assert.That(human.Body.GetProperty("score").GetDouble()).IsEqualTo(0.75);
        await Assert
            .That(human.Body.GetProperty("evaluator").GetProperty("reviewer").GetString())
            .IsEqualTo(Actor.Subject);
        var review = await store.CreateReviewAsync(
            Scope,
            Actor,
            new SaveResource(
                "review",
                JsonSerializer.SerializeToElement(
                    new { kind = "JudgeAudit", assessmentId = assessment.Id }
                )
            )
        );
        var owned = await store.ClaimReviewAsync(Scope, Actor, review.Id, review.Revision);
        var denied = false;
        try
        {
            await store.DecideReviewAsync(
                Scope,
                new ManagementActor("other"),
                owned.Id,
                owned.Revision,
                new("Reject", "not owner")
            );
        }
        catch (ManagementException error)
        {
            denied = error.Code == "review_not_owned";
        }
        await Assert.That(denied).IsTrue();
        var decided = await store.DecideReviewAsync(
            Scope,
            Actor,
            owned.Id,
            owned.Revision,
            new("NeedsEvidence", "need source")
        );
        await Assert.That(decided.State).IsEqualTo("NeedsEvidence");
        if (expected == "Scored")
        {
            await Assert.That(assessment.Body.GetProperty("score").GetDouble()).IsEqualTo(1);
        }
        var reassess = await store.CreateReassessmentAsync(
            Scope,
            Actor,
            "reassess",
            new CreateJobRequest(
                "Reassess",
                job.Body.GetProperty("datasetId").GetString()!,
                job.Body.GetProperty("benchmarkId").GetString()!,
                [
                    new JobCandidate(
                        "candidate",
                        "Candidate",
                        "build",
                        candidate.GetProperty("publicationId").GetString()!,
                        "c",
                        "v"
                    ),
                ],
                true,
                job.Id
            )
        );
        await Assert.That(reassess.State).IsEqualTo("AwaitingAssessment");
        await Assert.That((await store.GetJobAsync(Scope, reassess.Id)).Work.Count).IsEqualTo(0);
        var reassessment = await store.AssessDeterministicAsync(
            Scope,
            Actor,
            new("11111111-1111-1111-1111-111111111111", "numeric", reassess.Id)
        );
        await Assert.That(reassessment.State).IsEqualTo(expected);
        await Assert
            .That(reassessment.Body.GetProperty("executionId").GetString())
            .IsEqualTo("11111111-1111-1111-1111-111111111111");
    }

    [Test]
    public async Task CancelAndResumeOnlyRequeueUnsentWork()
    {
        var (store, _, job) = await CreateAsync();
        var claim = (
            await store.ClaimWorkAsync(Scope, Actor, "build", "worker", TimeSpan.FromMinutes(1))
        )!;
        var cancelled = await store.CancelJobAsync(Scope, Actor, job.Id, job.Revision);
        var rejected = false;
        try
        {
            await store.StartAttemptAsync(
                Scope,
                Actor,
                claim.WorkId,
                claim.Owner,
                claim.Epoch,
                "late"
            );
        }
        catch (ManagementException error)
        {
            rejected = error.Code == "claim_stale";
        }
        await Assert.That(rejected).IsTrue();
        await store.ResumeJobAsync(Scope, Actor, job.Id, cancelled.Revision);
        var newClaim = await store.ClaimWorkAsync(
            Scope,
            Actor,
            "build",
            "worker",
            TimeSpan.FromMinutes(1)
        );
        await Assert.That(newClaim!.Epoch).IsGreaterThan(claim.Epoch);
    }

    [Test]
    public async Task SendRequiresPreflightBoundToCurrentClaim()
    {
        var (store, _, _) = await CreateAsync();
        var claim = (
            await store.ClaimWorkAsync(Scope, Actor, "build", "worker", TimeSpan.FromMinutes(1))
        )!;
        var denied = false;
        try
        {
            await store.StartAttemptAsync(
                Scope,
                Actor,
                claim.WorkId,
                claim.Owner,
                claim.Epoch,
                "attempt"
            );
        }
        catch (ManagementException error)
        {
            denied = error.Code == "claim_stale";
        }
        await Assert.That(denied).IsTrue();
        await PassPreflightAsync(store, claim);
        await store.StartAttemptAsync(
            Scope,
            Actor,
            claim.WorkId,
            claim.Owner,
            claim.Epoch,
            "attempt"
        );
        await store.FinishAttemptAsync(
            Scope,
            Actor,
            claim.WorkId,
            claim.Owner,
            claim.Epoch,
            "attempt",
            J("{}")
        );
    }

    [Test]
    public async Task ExplicitResumeRequeuesBlockedPreflightButNeverAdmittedSend()
    {
        var (store, _, job) = await CreateAsync();
        var claim = (
            await store.ClaimWorkAsync(Scope, Actor, "build", "worker", TimeSpan.FromMinutes(1))
        )!;
        await store.HoldPreflightFailureAsync(Scope, Actor, claim, "invalid_input");
        await Assert
            .That((await store.GetJobAsync(Scope, job.Id)).Work.Single().Status)
            .IsEqualTo("Blocked");
        await store.ResumeJobAsync(Scope, Actor, job.Id, job.Revision);
        var next = await store.ClaimWorkAsync(
            Scope,
            Actor,
            "build",
            "worker",
            TimeSpan.FromMinutes(1)
        );
        await Assert.That(next!.Epoch).IsGreaterThan(claim.Epoch);
    }

    [Test]
    public async Task ChangedAttemptReceiptConflicts()
    {
        var (store, _, _) = await CreateAsync();
        var claim = (
            await store.ClaimWorkAsync(Scope, Actor, "build", "worker", TimeSpan.FromMinutes(1))
        )!;
        await PassPreflightAsync(store, claim);
        await store.StartAttemptAsync(
            Scope,
            Actor,
            claim.WorkId,
            claim.Owner,
            claim.Epoch,
            "attempt"
        );
        await store.FinishAttemptAsync(
            Scope,
            Actor,
            claim.WorkId,
            claim.Owner,
            claim.Epoch,
            "attempt",
            J("{\"outcome\":\"failed\"}")
        );
        var rejected = false;
        try
        {
            await store.FinishAttemptAsync(
                Scope,
                Actor,
                claim.WorkId,
                claim.Owner,
                claim.Epoch,
                "attempt",
                J("{\"outcome\":\"success\"}")
            );
        }
        catch (ManagementException error)
        {
            rejected = error.Code == "receipt_conflict";
        }
        await Assert.That(rejected).IsTrue();
    }

    private sealed class JudgeClient : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Calls++;
            return Task.FromResult(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        "{\"findings\":[],\"rubricLevel\":\"pass\"}"
                    )
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

    [Test]
    public async Task JournaledJudgeAssessmentStoresValidatedExactEvidence()
    {
        var (store, _, job) = await CreateAsync();
        var claim = (
            await store.ClaimWorkAsync(
                Scope,
                Actor,
                "build",
                Actor.Subject,
                TimeSpan.FromMinutes(1)
            )
        )!;
        await PassPreflightAsync(store, claim);
        await store.StartAttemptAsync(
            Scope,
            Actor,
            claim.WorkId,
            claim.Owner,
            claim.Epoch,
            "generation"
        );
        await store.FinishAttemptAsync(
            Scope,
            Actor,
            claim.WorkId,
            claim.Owner,
            claim.Epoch,
            "generation",
            J("{}")
        );
        var pin = claim.Request.GetProperty("pins")[0];
        var candidate = pin.GetProperty("candidate");
        var executionId = "11111111-1111-1111-1111-111111111111";
        var receipt = JsonSerializer.SerializeToElement(
            new
            {
                jobId = job.Id,
                workId = claim.WorkId,
                candidateId = "candidate",
                caseId = "case",
                repeatIndex = 0,
                buildId = "build",
                publicationId = candidate.GetProperty("publicationId").GetString(),
                publicationHash = pin.GetProperty("publicationHash").GetString(),
                commandId = "c",
                variantId = "v",
                purpose = "Backtest",
                role = "Primary",
                executionId,
                outcome = "Success",
                storageStatus = "Stored",
                value = 10.4,
                outputRef = new
                {
                    runId = Guid.NewGuid(),
                    artifactId = Guid.NewGuid(),
                    versionId = Guid.NewGuid(),
                    version = 1,
                    producer = new Dictionary<string, string>
                    {
                        ["$producer"] = "command",
                        ["id"] = executionId,
                    },
                },
            }
        );
        await store.CompleteWorkAsync(
            Scope,
            Actor,
            claim.WorkId,
            claim.Owner,
            claim.Epoch,
            receipt
        );
        var config = await store.SaveDraftAsync(
            Scope,
            Actor,
            "judge-configurations",
            new(
                "judge",
                JsonSerializer.SerializeToElement(
                    new
                    {
                        routeId = "route",
                        modelId = "model",
                        promptVersion = "v1",
                        resultSchemaVersion = "v1",
                        maxOutputTokens = 128,
                        rubric = "Assess the output",
                        calibrationPolicy = new
                        {
                            minimumSamples = 10,
                            maximumScoreDifference = 0.2,
                            minimumAgreement = 0.8,
                        },
                        rubricLevels = new[] { new { name = "pass", score = 1.0 } },
                    }
                )
            )
        );
        config = await store.FreezeAsync(
            Scope,
            Actor,
            "judge-configurations",
            config.Id,
            config.Revision
        );
        var datasetId = job.Body.GetProperty("datasetId").GetString()!;
        var benchmark = await store.SaveDraftAsync(
            Scope,
            Actor,
            "benchmarks",
            new(
                "judge protocol",
                JsonSerializer.SerializeToElement(
                    new
                    {
                        datasetId,
                        primaryCriterionId = "semantic",
                        repeats = 1,
                        criteria = new[]
                        {
                            new
                            {
                                id = "semantic",
                                version = 1,
                                scope = "Whole",
                                outputBinding = "",
                                referenceBinding = "",
                                scorer = new
                                {
                                    kind = "Llm",
                                    version = "1",
                                    judgeConfigId = config.Id,
                                },
                            },
                        },
                    }
                )
            )
        );
        benchmark = await store.FreezeAsync(
            Scope,
            Actor,
            "benchmarks",
            benchmark.Id,
            benchmark.Revision
        );
        var reassess = await store.CreateReassessmentAsync(
            Scope,
            Actor,
            "reassess",
            new(
                "Reassess",
                datasetId,
                benchmark.Id,
                [
                    new(
                        "candidate",
                        "Candidate",
                        "build",
                        candidate.GetProperty("publicationId").GetString()!,
                        "c",
                        "v"
                    ),
                ],
                true,
                job.Id
            )
        );
        var evaluation = await store.CreateJudgeEvaluationAsync(
            Scope,
            Actor,
            new(executionId, "semantic", true, reassess.Id)
        );
        var judgeClaim = (
            await store.ClaimWorkAsync(
                Scope,
                Actor,
                "judge:" + config.Id,
                Actor.Subject,
                TimeSpan.FromMinutes(1)
            )
        )!;
        await Assert
            .That(judgeClaim.Request.GetProperty("purpose").GetString())
            .IsEqualTo("Evaluator");
        await PassPreflightAsync(store, judgeClaim);
        var context = new CommandExecutionContext(
            Guid.NewGuid(),
            "judge",
            1,
            "hash",
            new CapturedContext(null, null, ContextSource.Explicit, false)
        );
        var judgeClient = new JudgeClient();
        var judge = new ScopedJudge(
            judgeClient,
            new ScopedJudgePolicy("route", "model", "v1", "v1", 128, "Assess the output")
        );
        var journal = new WorkExecutionJournal(store, Scope, Actor, judgeClaim);
        var workerAdapter = new JudgeWorkerAdapter(
            judgeClient,
            new ScopedJudgePolicy("route", "model", "v1", "v1", 128, "Assess the output"),
            config.Id,
            config.ContentHash
        );
        await Assert
            .That((await workerAdapter.CheckAsync(Scope, judgeClaim, default)).Passed)
            .IsTrue();
        var denied = false;
        try
        {
            await judge.EvaluateAsync(judgeClaim, false, context, journal);
        }
        catch (ManagementException)
        {
            denied = true;
        }
        await Assert.That(denied).IsTrue();
        await Assert.That(judgeClient.Calls).IsEqualTo(0);
        var assessment = await workerAdapter.ExecuteAsync(Scope, Actor, judgeClaim, store, context);
        await Assert.That(judgeClient.Calls).IsEqualTo(1);
        var retryDenied = false;
        try
        {
            await store.StartAttemptAsync(
                Scope,
                Actor,
                judgeClaim.WorkId,
                judgeClaim.Owner,
                judgeClaim.Epoch,
                "second"
            );
        }
        catch (ManagementException)
        {
            retryDenied = true;
        }
        await Assert.That(retryDenied).IsTrue();
        await Assert.That(assessment.State).IsEqualTo("Scored");
        await Assert
            .That(assessment.Body.GetProperty("evaluatorJobId").GetString())
            .IsEqualTo(evaluation.Id);
        await Assert
            .That(assessment.Body.GetProperty("executionId").GetString())
            .IsEqualTo(executionId);
        var attemptId = assessment.Body.GetProperty("evaluator").GetProperty("attemptId").GetGuid();
        var response = new ScopedJudgeResult(
            attemptId,
            "Returned",
            J("{\"findings\":[],\"rubricLevel\":\"pass\"}"),
            null,
            null,
            null
        );
        var duplicate = await store.RecordJudgeAssessmentAsync(
            Scope,
            Actor,
            judgeClaim.WorkId,
            response
        );
        await Assert.That(duplicate.Id).IsEqualTo(assessment.Id);
    }
}
