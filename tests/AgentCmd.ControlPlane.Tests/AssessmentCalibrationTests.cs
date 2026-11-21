using System.Text.Json;
using Microsoft.Data.Sqlite;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd.ControlPlane;

namespace AgentCmd.ControlPlane.Tests;

public class AssessmentCalibrationTests
{
    [Test]
    public async Task CalibrationUsesFrozenThresholdsAndRejectsAcceptanceFamilyOverlap()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "agentcmd-calibration-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "data.db");
        var store = new RelationalControlPlaneStore(
            new ManagementDatabase("Sqlite", () => new SqliteConnection($"Data Source={path}"))
        );
        var scope = new WorkspaceScope("app", "test", "partition");
        var actor = new ManagementActor("reviewer");
        await store.InitializeAsync();
        var cases = Enumerable
            .Range(0, 10)
            .Select(i => new
            {
                id = "case-" + i,
                sourceFamilyId = "family-" + i,
                input = new { text = "x" },
            })
            .ToArray();
        var dataset = await store.SaveDraftAsync(
            scope,
            actor,
            "datasets",
            new(
                "calibration",
                JsonSerializer.SerializeToElement(new { purpose = "Calibration", cases })
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
                        calibrationPolicy = new
                        {
                            minimumSamples = 10,
                            maximumScoreDifference = 0.2,
                            minimumAgreement = 0.8,
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
                        criteria = new[]
                        {
                            new
                            {
                                id = "criterion",
                                version = 1,
                                scorer = new { kind = "Llm", judgeConfigId = config.Id },
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
        var records = new List<(string Kind, string Id, string Body)>
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
                    }
                )
            ),
        };
        var pairs = new List<CalibrationPair>();
        for (var i = 0; i < 10; i++)
        {
            var human = "human-" + i;
            var judge = "judge-" + i;
            pairs.Add(new(human, judge));
            records.Add(
                (
                    "assessments",
                    human,
                    JsonSerializer.Serialize(
                        new
                        {
                            jobId = "job",
                            executionId = "execution-" + i,
                            caseId = "case-" + i,
                            criterionId = "criterion",
                            criterionVersion = 1,
                            datasetHash = dataset.ContentHash,
                            benchmarkHash = benchmark.ContentHash,
                            outcome = "Scored",
                            score = 1.0,
                            evaluator = new { kind = "Human" },
                        }
                    )
                )
            );
            records.Add(
                (
                    "assessments",
                    judge,
                    JsonSerializer.Serialize(
                        new
                        {
                            jobId = "job",
                            executionId = "execution-" + i,
                            caseId = "case-" + i,
                            criterionId = "criterion",
                            criterionVersion = 1,
                            datasetHash = dataset.ContentHash,
                            benchmarkHash = benchmark.ContentHash,
                            outcome = "Scored",
                            score = 0.9,
                            evaluator = new
                            {
                                kind = "Llm",
                                configId = config.Id,
                                configHash = config.ContentHash,
                            },
                        }
                    )
                )
            );
        }
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
        var request = new CalibrationRequest(config.Id, "criterion", pairs, 0.2, 0.8);
        var result = await store.CalibrateJudgeAsync(scope, actor, "agreement", request);
        await Assert.That(result.State).IsEqualTo("Admitted");
        var mismatch = false;
        try
        {
            await store.CalibrateJudgeAsync(
                scope,
                actor,
                "changed policy",
                request with
                {
                    MinimumAgreement = 0.1,
                }
            );
        }
        catch (ManagementException error)
        {
            mismatch = error.Code == "calibration_policy_mismatch";
        }
        await Assert.That(mismatch).IsTrue();
        var acceptance = await store.SaveDraftAsync(
            scope,
            actor,
            "datasets",
            new(
                "acceptance",
                JsonSerializer.SerializeToElement(
                    new { purpose = "Acceptance", cases = new[] { cases[0] } }
                )
            )
        );
        var rejectedFreeze = false;
        try
        {
            await store.FreezeAsync(scope, actor, "datasets", acceptance.Id, acceptance.Revision);
        }
        catch (ManagementException error)
        {
            rejectedFreeze = error.Code == "acceptance_source_exposed";
        }
        await Assert.That(rejectedFreeze).IsTrue();
        // Simulate historical data committed before the freeze guard existed.
        await using (var db = new SqliteConnection($"Data Source={path}"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText =
                "UPDATE cp_documents SET state='Frozen' WHERE scope=@scope AND kind='datasets' AND id=@id";
            command.Parameters.AddWithValue("scope", scope.Key);
            command.Parameters.AddWithValue("id", acceptance.Id);
            await command.ExecuteNonQueryAsync();
        }
        var overlap = false;
        try
        {
            await store.CalibrateJudgeAsync(scope, actor, "overlap", request);
        }
        catch (ManagementException error)
        {
            overlap = error.Code == "calibration_acceptance_overlap";
        }
        await Assert.That(overlap).IsTrue();
    }
}
