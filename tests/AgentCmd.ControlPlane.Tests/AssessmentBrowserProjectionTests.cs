using System.Text.Json;
using Microsoft.Data.Sqlite;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd.ControlPlane;

namespace AgentCmd.ControlPlane.Tests;

public class AssessmentBrowserProjectionTests
{
    [Test]
    public async Task HiddenAcceptanceEvidenceIsScopedAcrossJobsAndLinkedRecords()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "agentcmd-projection-" + Guid.NewGuid().ToString("N") + ".db"
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
                "holdout",
                JsonSerializer.SerializeToElement(
                    new
                    {
                        purpose = "Acceptance",
                        cases = new[]
                        {
                            new
                            {
                                id = "one",
                                sourceFamilyId = "family-one",
                                input = new { value = "SECRET_INPUT_ONE" },
                                reference = "SECRET_REFERENCE_ONE",
                            },
                            new
                            {
                                id = "two",
                                sourceFamilyId = "family-two",
                                input = new { value = "SECRET_INPUT_TWO" },
                                reference = "SECRET_REFERENCE_TWO",
                            },
                        },
                    }
                )
            )
        );
        dataset = await store.FreezeAsync(scope, actor, "datasets", dataset.Id, dataset.Revision);
        var records = new[]
        {
            (
                "jobs",
                "job",
                JsonSerializer.Serialize(
                    new
                    {
                        datasetId = dataset.Id,
                        status = "Queued",
                        counts = new { Queued = 2 },
                        pins = new[]
                        {
                            new
                            {
                                candidate = new
                                {
                                    id = "candidate",
                                    buildId = "build",
                                    variantId = "v",
                                },
                                publicationHash = "hash",
                            },
                        },
                        items = new[] { new { caseId = "one", result = "SECRET_JOB_RESULT" } },
                    }
                )
            ),
            (
                "executions",
                "execution-one",
                JsonSerializer.Serialize(
                    new
                    {
                        jobId = "job",
                        caseId = "one",
                        candidateId = "candidate",
                        value = "SECRET_OUTPUT",
                        outputRef = new { artifactId = "SECRET_OUTPUT_REF" },
                        request = "SECRET_PROMPT",
                    }
                )
            ),
            (
                "executions",
                "execution-two",
                JsonSerializer.Serialize(
                    new
                    {
                        jobId = "job",
                        caseId = "two",
                        candidateId = "candidate",
                        value = "SECRET_OUTPUT_TWO",
                    }
                )
            ),
            (
                "executions",
                "family-observation",
                JsonSerializer.Serialize(
                    new
                    {
                        purpose = "Production",
                        sourceFamilyId = "family-one",
                        input = new { text = "SECRET_OBSERVATION_INPUT" },
                        value = "SECRET_OBSERVATION_OUTPUT",
                    }
                )
            ),
            (
                "assessments",
                "assessment-one",
                JsonSerializer.Serialize(
                    new
                    {
                        jobId = "job",
                        caseId = "one",
                        executionId = "execution-one",
                        findings = new[] { new { quote = "SECRET_FINDING" } },
                        score = 1.0,
                    }
                )
            ),
            (
                "assessments",
                "assessment-two",
                JsonSerializer.Serialize(
                    new
                    {
                        jobId = "job",
                        caseId = "two",
                        executionId = "execution-two",
                        findings = new[] { new { quote = "SECRET_FINDING_TWO" } },
                    }
                )
            ),
            (
                "jobs",
                "judge-job",
                JsonSerializer.Serialize(
                    new
                    {
                        parentJobId = "job",
                        executionId = "execution-one",
                        purpose = "Evaluator",
                        source = "SECRET_SOURCE",
                        reference = "SECRET_REFERENCE",
                        target = "SECRET_TARGET",
                    }
                )
            ),
            (
                "reviews",
                "review",
                JsonSerializer.Serialize(
                    new
                    {
                        assessmentId = "assessment-one",
                        caseId = "one",
                        proposedValue = "SECRET_PROPOSAL",
                        sourceLocator = new { quote = "SECRET_QUOTE" },
                        assignee = "reviewer",
                    }
                )
            ),
            (
                "comparisons",
                "comparison",
                JsonSerializer.Serialize(
                    new { assessmentIds = new[] { "assessment-one", "assessment-two" } }
                )
            ),
        };
        await using (var db = new SqliteConnection($"Data Source={path}"))
        {
            await db.OpenAsync();
            foreach (var (kind, id, body) in records)
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
        foreach (
            var (kind, id) in new[]
            {
                ("jobs", "job"),
                ("jobs", "judge-job"),
                ("executions", "execution-one"),
                ("executions", "family-observation"),
                ("assessments", "assessment-one"),
                ("reviews", "review"),
            }
        )
        {
            var resource = (await store.ReadAsync(scope, kind, id))!;
            var projected = await store.ProjectBrowserResourceAsync(scope, resource);
            await Assert.That(projected.Body.GetProperty("holdoutWithheld").GetBoolean()).IsTrue();
            await Assert
                .That(projected.Body.GetRawText().Contains("SECRET", StringComparison.Ordinal))
                .IsFalse();
            await Assert.That(projected.ContentHash).IsEqualTo(resource.ContentHash);
        }
        var cases = new[]
        {
            new ComparisonCase("one", "Scored", 1.0, ["assessment-one"], ["Scored"]),
            new ComparisonCase("two", "Scored", 0.0, ["assessment-two"], ["Scored"]),
        };
        var report = new ComparisonReport(
            "comparison",
            "dataset-hash",
            "benchmark-hash",
            "candidate",
            false,
            [],
            [
                new ComparisonCriterion(
                    "criterion",
                    1,
                    ["one", "two"],
                    [new ComparisonCandidateMetric("key", 2, 2, 2, 0, 0, 0.5, 0.5, cases)]
                ),
            ],
            [],
            true
        );
        var hidden = await store.ProjectBrowserComparisonAsync(scope, report);
        await Assert.That(hidden.HoldoutWithheld).IsTrue();
        await Assert.That(hidden.Criteria.Single().Candidates.Single().Mean).IsEqualTo(0.5);
        await Assert
            .That(
                hidden
                    .Criteria.Single()
                    .Candidates.Single()
                    .Cases.All(item =>
                        item.Status == "Withheld"
                        && item.Score is null
                        && item.AssessmentIds.Count == 0
                    )
            )
            .IsTrue();
        await store.RevealDatasetCaseAsync(scope, actor, dataset.Id, "one", "authorized review");
        var visibleExecution = await store.ProjectBrowserResourceAsync(
            scope,
            (await store.ReadAsync(scope, "executions", "execution-one"))!
        );
        await Assert
            .That(visibleExecution.Body.GetProperty("value").GetString())
            .IsEqualTo("SECRET_OUTPUT");
        var visibleObservation = await store.ProjectBrowserResourceAsync(
            scope,
            (await store.ReadAsync(scope, "executions", "family-observation"))!
        );
        await Assert
            .That(visibleObservation.Body.GetProperty("value").GetString())
            .IsEqualTo("SECRET_OBSERVATION_OUTPUT");
        var visibleJudge = await store.ProjectBrowserResourceAsync(
            scope,
            (await store.ReadAsync(scope, "jobs", "judge-job"))!
        );
        await Assert
            .That(visibleJudge.Body.GetProperty("source").GetString())
            .IsEqualTo("SECRET_SOURCE");
        var stillHiddenJob = await store.ProjectBrowserResourceAsync(
            scope,
            (await store.ReadAsync(scope, "jobs", "job"))!
        );
        await Assert.That(stillHiddenJob.Body.GetProperty("holdoutWithheld").GetBoolean()).IsTrue();
        var partial = await store.ProjectBrowserComparisonAsync(scope, report);
        await Assert
            .That(
                partial
                    .Criteria.Single()
                    .Candidates.Single()
                    .Cases.Single(item => item.CaseId == "one")
                    .Status
            )
            .IsEqualTo("Scored");
        await Assert
            .That(
                partial
                    .Criteria.Single()
                    .Candidates.Single()
                    .Cases.Single(item => item.CaseId == "two")
                    .Status
            )
            .IsEqualTo("Withheld");
        await store.RevealDatasetCaseAsync(scope, actor, dataset.Id, "two", "authorized review");
        var visibleJob = await store.ProjectBrowserResourceAsync(
            scope,
            (await store.ReadAsync(scope, "jobs", "job"))!
        );
        await Assert
            .That(visibleJob.Body.GetProperty("items")[0].GetProperty("result").GetString())
            .IsEqualTo("SECRET_JOB_RESULT");
    }
}
