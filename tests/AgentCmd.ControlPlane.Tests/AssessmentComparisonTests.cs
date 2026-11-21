using System.Text.Json;
using Microsoft.Data.Sqlite;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd.ControlPlane;

namespace AgentCmd.ControlPlane.Tests;

public class AssessmentComparisonTests
{
    [Test]
    public async Task CompleteRepeatsAreReducedWithinCaseAndMissingCasesStayVisible()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "agentcmd-comparison-" + Guid.NewGuid().ToString("N") + ".db"
        );
        var store = new RelationalControlPlaneStore(
            new ManagementDatabase("Sqlite", () => new SqliteConnection($"Data Source={path}"))
        );
        var scope = new WorkspaceScope("app", "test", "one");
        var actor = new ManagementActor("reviewer");
        await store.InitializeAsync();
        var dataset = await store.SaveDraftAsync(
            scope,
            actor,
            "datasets",
            new(
                "dataset",
                JsonSerializer.SerializeToElement(
                    new { cases = new[] { new { id = "a" }, new { id = "b" } } }
                )
            )
        );
        dataset = await store.FreezeAsync(scope, actor, "datasets", dataset.Id, dataset.Revision);
        var benchmark = await store.SaveDraftAsync(
            scope,
            actor,
            "benchmarks",
            new(
                "benchmark",
                JsonSerializer.SerializeToElement(
                    new
                    {
                        datasetId = dataset.Id,
                        repeats = 2,
                        primaryCriterionId = "metric",
                        criteria = new[]
                        {
                            new
                            {
                                id = "metric",
                                version = 1,
                                scorer = new { kind = "Exact" },
                            },
                        },
                        guardrails = new[] { new { id = "safety" } },
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
        var candidates = new[]
        {
            new
            {
                id = "baseline",
                buildId = "build",
                publicationId = "publication-1",
                commandId = "command",
                variantId = "one",
            },
            new
            {
                id = "candidate",
                buildId = "build",
                publicationId = "publication-2",
                commandId = "command",
                variantId = "two",
            },
        };
        var pins = candidates
            .Select(candidate => new
            {
                candidate,
                buildHash = "build-hash",
                publicationHash = candidate.publicationId + "-hash",
            })
            .ToArray();
        var rows = new List<(string Kind, string Id, string Body)>
        {
            (
                "jobs",
                "job",
                JsonSerializer.Serialize(
                    new
                    {
                        datasetId = dataset.Id,
                        datasetHash = dataset.ContentHash,
                        benchmarkId = benchmark.Id,
                        benchmarkHash = benchmark.ContentHash,
                        pins,
                    }
                )
            ),
        };
        var ids = new List<string>();
        foreach (var candidate in candidates)
        {
            foreach (var caseId in new[] { "a", "b" })
            {
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    if (candidate.id == "candidate" && caseId == "b" && repeat == 1)
                    {
                        continue;
                    }
                    var executionId = $"{candidate.id}-{caseId}-{repeat}";
                    var assessmentId = "assessment-" + executionId;
                    ids.Add(assessmentId);
                    var score =
                        candidate.id == "baseline" && caseId == "a" && repeat == 0 ? 0.0 : 1.0;
                    rows.Add(
                        (
                            "executions",
                            executionId,
                            JsonSerializer.Serialize(
                                new
                                {
                                    jobId = "job",
                                    candidateId = candidate.id,
                                    caseId,
                                    repeatIndex = repeat,
                                    buildId = candidate.buildId,
                                    publicationId = candidate.publicationId,
                                    publicationHash = candidate.publicationId + "-hash",
                                    commandId = candidate.commandId,
                                    variantId = candidate.variantId,
                                    comparisonEligible = true,
                                }
                            )
                        )
                    );
                    rows.Add(
                        (
                            "assessments",
                            assessmentId,
                            JsonSerializer.Serialize(
                                new
                                {
                                    jobId = "job",
                                    executionId,
                                    candidateId = candidate.id,
                                    caseId,
                                    criterionId = "metric",
                                    criterionVersion = 1,
                                    datasetHash = dataset.ContentHash,
                                    benchmarkHash = benchmark.ContentHash,
                                    comparisonEligible = true,
                                    outcome = "Scored",
                                    score,
                                    evaluator = new { kind = "Exact" },
                                }
                            )
                        )
                    );
                }
            }
        }
        await using (var db = new SqliteConnection($"Data Source={path}"))
        {
            await db.OpenAsync();
            foreach (var (kind, id, body) in rows)
            {
                await using var command = db.CreateCommand();
                command.CommandText =
                    "INSERT INTO cp_documents(scope,kind,id,revision,name,state,created_at,updated_at,content_hash,body) VALUES(@scope,@kind,@id,1,@id,'Recorded','2026-01-01T00:00:00Z','2026-01-01T00:00:00Z',@hash,@body)";
                command.Parameters.AddWithValue("scope", scope.Key);
                command.Parameters.AddWithValue("kind", kind);
                command.Parameters.AddWithValue("id", id);
                command.Parameters.AddWithValue("hash", ContentIdentity.Hash(body));
                command.Parameters.AddWithValue("body", body);
                await command.ExecuteNonQueryAsync();
            }
        }
        var comparison = await store.SaveComparisonAsync(
            scope,
            actor,
            "comparison",
            new(ids, "baseline")
        );
        var report = await store.ReadComparisonSummaryAsync(scope, comparison.Id);
        await Assert.That(report.PrimaryCriterionId).IsEqualTo("metric");
        var criterion = report.Criteria.Single();
        var baselineKey = report
            .Candidates.Single(candidate => candidate.CandidateId == "baseline")
            .Key;
        var candidateKey = report
            .Candidates.Single(candidate => candidate.CandidateId == "candidate")
            .Key;
        var baseline = criterion.Candidates.Single(item => item.CandidateKey == baselineKey);
        var candidateMetric = criterion.Candidates.Single(item =>
            item.CandidateKey == candidateKey
        );
        await Assert.That(baseline.Mean).IsEqualTo(0.75);
        await Assert.That(baseline.MatchedMean).IsEqualTo(0.5);
        await Assert.That(candidateMetric.MatchedMean).IsEqualTo(1.0);
        await Assert.That(candidateMetric.MissingCases).IsEqualTo(1);
        await Assert
            .That(candidateMetric.Cases.Single(item => item.CaseId == "b").Status)
            .IsEqualTo("Pending");
        await Assert.That(criterion.CommonCaseIds.Single()).IsEqualTo("a");
        await Assert.That(report.Guardrails.Single().Status).IsEqualTo("Unknown");
    }
}
