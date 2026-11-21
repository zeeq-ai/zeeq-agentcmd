using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd.ControlPlane;

namespace AgentCmd.ControlPlane.Tests;

public class AssessmentJudgeFindingsTests
{
    private static JsonElement J(string value) => JsonDocument.Parse(value).RootElement.Clone();

    [Test]
    public async Task RequiresOneToOneMatchesAndVerifiableSourceLocators()
    {
        var output = J("[\"A\",\"C\"]");
        var reference = J("[\"A\"]");
        var valid = J(
            "[{\"category\":\"Matched\",\"candidatePointer\":\"/0\",\"referencePointer\":\"/0\"},{\"category\":\"SupportedAddition\",\"candidatePointer\":\"/1\",\"sourceLocator\":{\"start\":16,\"end\":17,\"quote\":\"C\"}}]"
        );
        await Assert
            .That(
                JudgeFindingsValidator
                    .Validate(valid, output, reference, "source supports C", "Complete")
                    .Valid
            )
            .IsTrue();
        var duplicate = J(
            "[{\"category\":\"Matched\",\"candidatePointer\":\"/0\",\"referencePointer\":\"/0\"},{\"category\":\"Matched\",\"candidatePointer\":\"/1\",\"referencePointer\":\"/0\"}]"
        );
        await Assert
            .That(
                JudgeFindingsValidator
                    .Validate(duplicate, output, reference, "source supports C", "Complete")
                    .Valid
            )
            .IsFalse();
        var wrongQuote = J(
            "[{\"category\":\"SupportedAddition\",\"candidatePointer\":\"/1\",\"sourceLocator\":{\"start\":16,\"end\":17,\"quote\":\"X\"}}]"
        );
        await Assert
            .That(
                JudgeFindingsValidator
                    .Validate(wrongQuote, output, reference, "source supports C", "Complete")
                    .Valid
            )
            .IsFalse();
        var unsupported = J(
            "[{\"category\":\"UnsupportedInReviewedSource\",\"candidatePointer\":\"/1\"}]"
        );
        await Assert
            .That(
                JudgeFindingsValidator
                    .Validate(unsupported, output, reference, "source supports C", "Partial")
                    .Valid
            )
            .IsFalse();
    }
}

public class AssessmentJudgeDisputeTests
{
    [Test]
    public async Task SupportedExtraAgainstExhaustiveReferenceIsUnreconciled()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "agentcmd-judge-dispute-" + Guid.NewGuid().ToString("N") + ".db"
        );
        var store = new RelationalControlPlaneStore(
            new ManagementDatabase("Sqlite", () =>
                new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}")
            )
        );
        var scope = new WorkspaceScope("app", "test", "one");
        var actor = new ManagementActor("worker");
        await store.InitializeAsync();
        var attempt = Guid.NewGuid();
        var work = JsonSerializer.Serialize(
            new
            {
                kind = "Judge",
                purpose = "Evaluator",
                evaluatorJobId = "judge-job",
                parentJobId = "parent",
                executionId = "execution",
                criterionId = "judge",
                criterionVersion = 1,
                datasetHash = "dataset-hash",
                benchmarkHash = "benchmark-hash",
                judgeConfigurationId = "config",
                judgeConfigurationHash = "config-hash",
                outputRef = new { artifactId = "output" },
                target = "C",
                reference = "A",
                referenceCompleteness = "Exhaustive",
                source = "A and C",
                sourceCoverage = "Complete",
                policy = new
                {
                    routeId = "route",
                    modelId = "model",
                    rubricLevels = new[] { new { name = "supported", score = 1.0 } },
                },
            }
        );
        var eventReceipt = JsonSerializer.Serialize(
            new
            {
                Outcome = "success",
                Provider = "route",
                InputTokens = (long?)null,
                OutputTokens = (long?)null,
            }
        );
        await using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            await db.OpenAsync();
            foreach (
                var (kind, id, body) in new[]
                {
                    ("jobs", "judge-job", "{}"),
                    (
                        "executions",
                        "execution",
                        "{\"candidateId\":\"candidate\",\"caseId\":\"case\"}"
                    ),
                }
            )
            {
                await using var command = db.CreateCommand();
                command.CommandText =
                    "INSERT INTO cp_documents(scope,kind,id,revision,name,state,created_at,updated_at,content_hash,body) VALUES(@scope,@kind,@id,1,@id,'Queued','2026-01-01T00:00:00Z','2026-01-01T00:00:00Z',@hash,@body)";
                command.Parameters.AddWithValue("scope", scope.Key);
                command.Parameters.AddWithValue("kind", kind);
                command.Parameters.AddWithValue("id", id);
                command.Parameters.AddWithValue("hash", ContentIdentity.Hash(body));
                command.Parameters.AddWithValue("body", body);
                await command.ExecuteNonQueryAsync();
            }
            await using (var command = db.CreateCommand())
            {
                command.CommandText =
                    "INSERT INTO cp_work(scope,id,job_id,build_id,candidate_id,case_id,repeat_index,status,owner,epoch,request) VALUES(@scope,'work','judge-job','judge:config','candidate','case',0,'Claimed','worker',1,@request)";
                command.Parameters.AddWithValue("scope", scope.Key);
                command.Parameters.AddWithValue("request", work);
                await command.ExecuteNonQueryAsync();
            }
            await using (var command = db.CreateCommand())
            {
                command.CommandText =
                    "INSERT INTO cp_attempts(scope,id,work_id,epoch,owner,state,started_at,receipt) VALUES(@scope,@id,'work',1,'worker','Finished','2026-01-01T00:00:00Z',@receipt)";
                command.Parameters.AddWithValue("scope", scope.Key);
                command.Parameters.AddWithValue("id", attempt.ToString("D"));
                command.Parameters.AddWithValue("receipt", eventReceipt);
                await command.ExecuteNonQueryAsync();
            }
        }
        using var response = JsonDocument.Parse(
            "{\"rubricLevel\":\"supported\",\"findings\":[{\"category\":\"SupportedAddition\",\"candidatePointer\":\"\",\"sourceLocator\":{\"start\":6,\"end\":7,\"quote\":\"C\"}}]}"
        );
        var assessment = await store.RecordJudgeAssessmentAsync(
            scope,
            actor,
            "work",
            new(attempt, "Returned", response.RootElement.Clone(), null, null, null)
        );
        await Assert.That(assessment.State).IsEqualTo("Unreconciled");
        await Assert.That(assessment.Body.GetProperty("referenceDisputed").GetBoolean()).IsTrue();
        await Assert
            .That(assessment.Body.GetProperty("score").ValueKind)
            .IsEqualTo(JsonValueKind.Null);
    }
}
