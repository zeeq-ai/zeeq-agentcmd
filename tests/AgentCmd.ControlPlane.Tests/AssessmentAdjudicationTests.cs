using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd.ControlPlane;

namespace AgentCmd.ControlPlane.Tests;

public class AssessmentAdjudicationTests
{
    [Test]
    public async Task AcceptedReferenceRequiresExactCitedBytesAndOnlyUpdatesDraft()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "agentcmd-review-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "review.db");
        var store = new RelationalControlPlaneStore(
            new ManagementDatabase("Sqlite", () =>
                new SqliteConnection($"Data Source={databasePath}")
            )
        );
        var assets = new FileAssetStore(Path.Combine(directory, "assets"));
        var scope = new WorkspaceScope("app", "test", "one");
        var actor = new ManagementActor("reviewer");
        await store.InitializeAsync();
        await using var bytes = new MemoryStream(Encoding.UTF8.GetBytes("source supports C"));
        var asset = await assets.PutAsync(scope, bytes);
        var content = JsonSerializer.SerializeToElement(
            new
            {
                purpose = "Regression",
                cases = new[]
                {
                    new
                    {
                        id = "case",
                        sourceFamilyId = "family",
                        input = new { text = "source" },
                        assets = new[] { asset.Hash },
                        reference = new { value = new[] { "A" }, completeness = "Partial" },
                    },
                },
            }
        );
        var original = await store.SaveDraftAsync(
            scope,
            actor,
            "datasets",
            new("original", content)
        );
        original = await store.FreezeAsync(
            scope,
            actor,
            "datasets",
            original.Id,
            original.Revision
        );
        var draft = await store.SaveDraftAsync(scope, actor, "datasets", new("draft", content));
        await using (var db = new SqliteConnection($"Data Source={databasePath}"))
        {
            await db.OpenAsync();
            foreach (
                var (kind, id, body) in new[]
                {
                    (
                        "jobs",
                        "source-job",
                        JsonSerializer.Serialize(new { datasetId = original.Id })
                    ),
                    (
                        "executions",
                        "execution",
                        JsonSerializer.Serialize(new { jobId = "source-job", caseId = "case" })
                    ),
                }
            )
            {
                await using var cmd = db.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO cp_documents(scope,kind,id,revision,name,state,created_at,updated_at,content_hash,body) VALUES(@scope,@kind,@id,1,@id,'Recorded','2026-01-01T00:00:00Z','2026-01-01T00:00:00Z',@hash,@body)";
                cmd.Parameters.AddWithValue("scope", scope.Key);
                cmd.Parameters.AddWithValue("kind", kind);
                cmd.Parameters.AddWithValue("id", id);
                cmd.Parameters.AddWithValue("hash", ContentIdentity.Hash(body));
                cmd.Parameters.AddWithValue("body", body);
                await cmd.ExecuteNonQueryAsync();
            }
        }
        var reviewBody = JsonSerializer.SerializeToElement(
            new
            {
                kind = "ReferenceGap",
                executionId = "execution",
                datasetId = original.Id,
                caseId = "case",
                proposedValue = "C",
                sourceLocator = new
                {
                    assetHash = asset.Hash,
                    start = 16,
                    end = 17,
                    quote = "C",
                },
            }
        );
        var review = await store.CreateReviewAsync(scope, actor, new("gap", reviewBody));
        review = await store.ClaimReviewAsync(scope, actor, review.Id, review.Revision);
        var accepted = await store.AcceptReferenceReviewAsync(
            scope,
            actor,
            review.Id,
            review.Revision,
            new("Accept", "source confirms it", draft.Id),
            assets
        );
        await Assert.That(accepted.State).IsEqualTo("AcceptedIntoDraft");
        var updated = (await store.ReadAsync(scope, "datasets", draft.Id))!;
        await Assert
            .That(
                updated
                    .Body.GetProperty("cases")[0]
                    .GetProperty("reference")
                    .GetProperty("value")
                    .GetArrayLength()
            )
            .IsEqualTo(2);
        var retained = (await store.ReadAsync(scope, "datasets", original.Id))!;
        await Assert
            .That(
                retained
                    .Body.GetProperty("cases")[0]
                    .GetProperty("reference")
                    .GetProperty("value")
                    .GetArrayLength()
            )
            .IsEqualTo(1);
    }
}
