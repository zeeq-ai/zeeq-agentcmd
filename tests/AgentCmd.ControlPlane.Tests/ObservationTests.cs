using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd.ControlPlane;

namespace AgentCmd.ControlPlane.Tests;

public class ObservationTests
{
    [Test]
    public async Task IncidentCaptureKeepsOriginalFailureAndRequiresOwnedReviewBeforeFreeze()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "agentcmd-observation-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        var store = new RelationalControlPlaneStore(
            new("Sqlite", () =>
                new SqliteConnection(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = Path.Combine(directory, "management.db"),
                        Pooling = false,
                    }.ToString()
                )
            )
        );
        var scope = new WorkspaceScope("app", "test", "one");
        var actor = new ManagementActor("operator");
        try
        {
            await store.InitializeAsync();
            var build = await store.RegisterBuildAsync(
                scope,
                actor,
                new("build", JsonSerializer.SerializeToElement(new { buildId = "build" }))
            );
            var report = new ExecutionObservation(
                Guid.NewGuid(),
                "Production",
                build.Id,
                "extract",
                "Failed",
                "Unavailable",
                Input: JsonSerializer.SerializeToElement(new { Document = "source" }),
                SourceFamilyId: "source-family"
            );
            var execution = await store.RecordObservationAsync(scope, actor, report);
            await Assert
                .That((await store.RecordObservationAsync(scope, actor, report)).Id)
                .IsEqualTo(execution.Id);
            var draft = await store.SaveDraftAsync(
                scope,
                actor,
                "datasets",
                new(
                    "regression",
                    JsonSerializer.SerializeToElement(
                        new { purpose = "Regression", cases = Array.Empty<object>() }
                    )
                )
            );
            var captured = await store.CaptureIncidentAsync(
                scope,
                actor,
                execution.Id,
                new(draft.Id, draft.Revision, "Investigate extraction failure")
            );
            await Assert.That(captured.Review.State).IsEqualTo("Ready");
            await Assert
                .That(
                    captured.Dataset.Body.GetProperty("cases")[0].TryGetProperty("reference", out _)
                )
                .IsFalse();
            await Assert
                .That(
                    (
                        await store.CaptureIncidentAsync(
                            scope,
                            actor,
                            execution.Id,
                            new(draft.Id, draft.Revision, "Investigate extraction failure")
                        )
                    ).AlreadyPresent
                )
                .IsTrue();
            var held = false;
            try
            {
                await store.FreezeAsync(
                    scope,
                    actor,
                    "datasets",
                    captured.Dataset.Id,
                    captured.Dataset.Revision
                );
            }
            catch (ManagementException error) when (error.Code == "dataset_case_review_pending")
            {
                held = true;
            }
            await Assert.That(held).IsTrue();
            var claimed = await store.ClaimReviewAsync(
                scope,
                actor,
                captured.Review.Id,
                captured.Review.Revision
            );
            var released = await store.ReleaseReviewAsync(
                scope,
                actor,
                claimed.Id,
                claimed.Revision
            );
            claimed = await store.ClaimReviewAsync(scope, actor, released.Id, released.Revision);
            var accepted = await store.AcceptIncidentAsync(
                scope,
                actor,
                claimed.Id,
                claimed.Revision
            );
            await Assert.That(accepted.State).IsEqualTo("AcceptedIntoDraft");
            var editedBody = JsonNode.Parse(captured.Dataset.Body.GetRawText())!;
            editedBody["cases"]![0]!["input"] = new JsonObject
            {
                ["Document"] = "changed after review",
            };
            var edited = await store.SaveDraftAsync(
                scope,
                actor,
                "datasets",
                new("edited", JsonSerializer.SerializeToElement(editedBody)),
                captured.Dataset.Id,
                captured.Dataset.Revision
            );
            var changedBlocked = false;
            try
            {
                await store.FreezeAsync(scope, actor, "datasets", edited.Id, edited.Revision);
            }
            catch (ManagementException error)
                when (error.Code == "dataset_case_review_evidence_changed")
            {
                changedBlocked = true;
            }
            await Assert.That(changedBlocked).IsTrue();
            var restored = await store.SaveDraftAsync(
                scope,
                actor,
                "datasets",
                new(captured.Dataset.Name, captured.Dataset.Body),
                edited.Id,
                edited.Revision
            );
            await store.FreezeAsync(
                scope,
                actor,
                "datasets",
                captured.Dataset.Id,
                restored.Revision
            );
            await Assert
                .That((await store.ReadAsync(scope, "executions", execution.Id))!.ContentHash)
                .IsEqualTo(execution.ContentHash);
            var missing = await store.RecordObservationAsync(
                scope,
                actor,
                report with
                {
                    ExecutionId = Guid.NewGuid(),
                    Input = null,
                }
            );
            var empty = await store.SaveDraftAsync(
                scope,
                actor,
                "datasets",
                new(
                    "missing input draft",
                    JsonSerializer.SerializeToElement(
                        new { purpose = "Regression", cases = Array.Empty<object>() }
                    )
                )
            );
            var blocked = false;
            try
            {
                await store.CaptureIncidentAsync(
                    scope,
                    actor,
                    missing.Id,
                    new(empty.Id, empty.Revision, "missing input")
                );
            }
            catch (ManagementException error) when (error.Code == "incident_input_not_retained")
            {
                blocked = true;
            }
            await Assert.That(blocked).IsTrue();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
