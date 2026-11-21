using System.Text.Json;
using Microsoft.Data.Sqlite;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd.ControlPlane;

namespace AgentCmd.ControlPlane.Tests;

public class AssessmentOutputBindingsTests
{
    [Test]
    public async Task DifferentDeclaredShapesRequireMappingAndNeverUseFallback()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "agentcmd-bindings-" + Guid.NewGuid().ToString("N") + ".db"
        );
        var store = new RelationalControlPlaneStore(
            new ManagementDatabase("Sqlite", () => new SqliteConnection($"Data Source={path}"))
        );
        var scope = new WorkspaceScope("app", "test", "one");
        var actor = new ManagementActor("worker");
        await store.InitializeAsync();
        var dataset = await store.SaveDraftAsync(
            scope,
            actor,
            "datasets",
            new(
                "dataset",
                JsonSerializer.SerializeToElement(
                    new
                    {
                        purpose = "Calibration",
                        cases = new[]
                        {
                            new
                            {
                                id = "case",
                                sourceFamilyId = "family",
                                input = new { document = "test" },
                                reference = new { value = 42, completeness = "Exhaustive" },
                            },
                        },
                    }
                )
            )
        );
        dataset = await store.FreezeAsync(scope, actor, "datasets", dataset.Id, dataset.Revision);
        var config = await store.SaveDraftAsync(
            scope,
            actor,
            "judge-configurations",
            new(
                "judge",
                JsonSerializer.SerializeToElement(
                    new
                    {
                        routeId = "route",
                        modelId = "model",
                        promptVersion = "1",
                        resultSchemaVersion = "1",
                        rubric = "rubric",
                        maxOutputTokens = 100,
                        rubricLevels = new[] { new { name = "pass", score = 1 } },
                        calibrationPolicy = new
                        {
                            minimumSamples = 10,
                            maximumScoreDifference = 0.1,
                            minimumAgreement = 0.9,
                        },
                    }
                )
            )
        );
        config = await store.FreezeAsync(
            scope,
            actor,
            "judge-configurations",
            config.Id,
            config.Revision
        );
        var candidates = new List<JobCandidate>();
        foreach (var (buildId, signature) in new[] { ("r7", "shape-r7"), ("r8", "shape-r8") })
        {
            await store.RegisterBuildAsync(
                scope,
                actor,
                new(
                    buildId,
                    JsonSerializer.SerializeToElement(
                        new
                        {
                            buildId,
                            commands = new[]
                            {
                                new
                                {
                                    id = "c",
                                    revision = 1,
                                    contractSignature = "contract",
                                    output = new { status = "declared", signature },
                                },
                            },
                        }
                    ),
                    buildId
                )
            );
            var publication = await store.SaveDraftAsync(
                scope,
                actor,
                "publications",
                new(
                    buildId,
                    JsonSerializer.SerializeToElement(
                        new
                        {
                            buildId,
                            commands = new[]
                            {
                                new
                                {
                                    commandId = "c",
                                    commandRevision = 1,
                                    contractSignature = "contract",
                                    variants = new[] { new { id = "v" } },
                                },
                            },
                        }
                    )
                )
            );
            publication = await store.FreezeAsync(
                scope,
                actor,
                "publications",
                publication.Id,
                publication.Revision
            );
            candidates.Add(new(buildId, buildId, buildId, publication.Id, "c", "v"));
        }
        var unmappedBenchmark = await store.SaveDraftAsync(
            scope,
            actor,
            "benchmarks",
            new(
                "invalid",
                JsonSerializer.SerializeToElement(
                    new
                    {
                        datasetId = dataset.Id,
                        repeats = 1,
                        primaryCriterionId = "exact",
                        criteria = new[]
                        {
                            new
                            {
                                id = "exact",
                                version = 1,
                                outputBinding = "/total",
                                scorer = new { kind = "Exact", version = "1" },
                            },
                        },
                    }
                )
            )
        );
        unmappedBenchmark = await store.FreezeAsync(
            scope,
            actor,
            "benchmarks",
            unmappedBenchmark.Id,
            unmappedBenchmark.Revision
        );
        var denied = false;
        try
        {
            await store.CreateBacktestAsync(
                scope,
                actor,
                "invalid",
                new("Backtest", dataset.Id, unmappedBenchmark.Id, candidates, true)
            );
        }
        catch (ManagementException error)
        {
            denied = error.Code == "criterion_output_bindings_required";
        }
        await Assert.That(denied).IsTrue();
        var mapping = new Dictionary<string, string>
        {
            ["shape-r7"] = "/total",
            ["shape-r8"] = "/Totals/Amount",
        };
        var partial = new Dictionary<string, string> { ["shape-r7"] = "/total" };
        var benchmark = await store.SaveDraftAsync(
            scope,
            actor,
            "benchmarks",
            new(
                "mapped",
                JsonSerializer.SerializeToElement(
                    new
                    {
                        datasetId = dataset.Id,
                        repeats = 1,
                        primaryCriterionId = "exact",
                        criteria = new object[]
                        {
                            new
                            {
                                id = "exact",
                                version = 1,
                                outputBindings = mapping,
                                scorer = new { kind = "Exact", version = "1" },
                            },
                            new
                            {
                                id = "human",
                                version = 1,
                                outputBindings = mapping,
                                scorer = new { kind = "Human", version = "1" },
                            },
                            new
                            {
                                id = "missing",
                                version = 1,
                                outputBinding = "/Totals/Amount",
                                outputBindings = partial,
                                scorer = new { kind = "Exact", version = "1" },
                            },
                            new
                            {
                                id = "judge",
                                version = 1,
                                outputBinding = "/Totals/Amount",
                                outputBindings = partial,
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
            scope,
            actor,
            "benchmarks",
            benchmark.Id,
            benchmark.Revision
        );
        var job = await store.CreateBacktestAsync(
            scope,
            actor,
            "mapped",
            new("Backtest", dataset.Id, benchmark.Id, candidates, true)
        );
        var exactAssessmentIds = new List<string>();
        foreach (var buildId in new[] { "r7", "r8" })
        {
            var claim = (
                await store.ClaimWorkAsync(
                    scope,
                    actor,
                    buildId,
                    actor.Subject,
                    TimeSpan.FromMinutes(1)
                )
            )!;
            await store.RecordPreflightAsync(
                scope,
                actor,
                claim,
                new(true, ContentIdentity.Hash(ContentIdentity.Normalize(claim.Request)))
            );
            await store.StartAttemptAsync(
                scope,
                actor,
                claim.WorkId,
                claim.Owner,
                claim.Epoch,
                "attempt-" + buildId
            );
            await store.FinishAttemptAsync(
                scope,
                actor,
                claim.WorkId,
                claim.Owner,
                claim.Epoch,
                "attempt-" + buildId,
                JsonSerializer.SerializeToElement(new { outcome = "success" })
            );
            var candidate = claim.Request.GetProperty("candidate");
            var pin = claim
                .Request.GetProperty("pins")
                .EnumerateArray()
                .Single(item =>
                    item.GetProperty("candidate").GetProperty("id").GetString() == buildId
                );
            var executionId = Guid.NewGuid();
            object value =
                buildId == "r7" ? new { total = 42 } : new { Totals = new { Amount = 42 } };
            var receipt = JsonSerializer.SerializeToElement(
                new
                {
                    jobId = job.Id,
                    workId = claim.WorkId,
                    candidateId = buildId,
                    caseId = "case",
                    repeatIndex = 0,
                    buildId,
                    publicationId = candidate.GetProperty("publicationId").GetString(),
                    publicationHash = pin.GetProperty("publicationHash").GetString(),
                    commandId = "c",
                    variantId = "v",
                    purpose = "Backtest",
                    role = "Primary",
                    executionId,
                    outcome = "Success",
                    storageStatus = "Stored",
                    comparisonEligible = true,
                    value,
                    outputRef = new
                    {
                        runId = Guid.NewGuid(),
                        artifactId = Guid.NewGuid(),
                        versionId = Guid.NewGuid(),
                        version = 1,
                        producer = new Dictionary<string, string>
                        {
                            ["$producer"] = "command",
                            ["id"] = executionId.ToString(),
                        },
                    },
                }
            );
            await store.CompleteWorkAsync(
                scope,
                actor,
                claim.WorkId,
                claim.Owner,
                claim.Epoch,
                receipt
            );
            var exact = await store.AssessDeterministicAsync(
                scope,
                actor,
                new(executionId.ToString(), "exact")
            );
            exactAssessmentIds.Add(exact.Id);
            await Assert.That(exact.State).IsEqualTo("Scored");
            await Assert.That(exact.Body.GetProperty("score").GetDouble()).IsEqualTo(1.0);
            var human = await store.AssessHumanAsync(
                scope,
                actor,
                new(executionId.ToString(), "human", 1, "reviewed")
            );
            await Assert
                .That(human.Body.GetProperty("targetPointer").GetString())
                .IsEqualTo(mapping["shape-" + buildId]);
            if (buildId == "r8")
            {
                var missing = await store.AssessDeterministicAsync(
                    scope,
                    actor,
                    new(executionId.ToString(), "missing")
                );
                await Assert.That(missing.State).IsEqualTo("Unmapped");
                var judge = await store.CreateJudgeEvaluationAsync(
                    scope,
                    actor,
                    new(executionId.ToString(), "judge", true)
                );
                await Assert.That(judge.State).IsEqualTo("Unmapped");
                await Assert
                    .That((await store.GetJobAsync(scope, judge.Id)).Work.Count)
                    .IsEqualTo(0);
                var assessment = await store.ReadAsync(
                    scope,
                    "assessments",
                    judge.Body.GetProperty("assessmentId").GetString()!
                );
                await Assert.That(assessment!.State).IsEqualTo("Unmapped");
            }
        }
        var comparison = await store.SaveComparisonAsync(
            scope,
            actor,
            "mapped comparison",
            new(exactAssessmentIds, "r7")
        );
        var report = await store.ReadComparisonSummaryAsync(scope, comparison.Id);
        var exactCriterion = report.Criteria.Single(item => item.CriterionId == "exact");
        await Assert.That(exactCriterion.CommonCaseIds.Single()).IsEqualTo("case");
        await Assert.That(exactCriterion.Candidates.All(item => item.MatchedMean == 1.0)).IsTrue();
    }
}
