using System.Text.Json;
using Microsoft.Data.Sqlite;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd.ControlPlane;

namespace AgentCmd.ControlPlane.Tests;

public class DatasetExposureTests
{
    [Test]
    public async Task ProjectionUsesItsOwnSnapshotAndExposureDeclarationsCannotBeCleared()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "agentcmd-snapshot-" + Guid.NewGuid().ToString("N")
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
        var actor = new ManagementActor("curator");
        JsonElement Body(string family, bool exposed) =>
            JsonSerializer.SerializeToElement(
                new
                {
                    purpose = "Acceptance",
                    cases = new[]
                    {
                        new
                        {
                            id = "same-case-id",
                            sourceFamilyId = family,
                            exposed,
                            input = new { Document = "source for " + family },
                        },
                    },
                }
            );
        try
        {
            await store.InitializeAsync();
            var older = await store.SaveDraftAsync(
                scope,
                actor,
                "datasets",
                new("unexposed", Body("F", false))
            );
            _ = await store.SaveDraftAsync(
                scope,
                actor,
                "datasets",
                new("declared exposure", Body("G", true))
            );
            var newer = await store.SaveDraftAsync(
                scope,
                actor,
                "datasets",
                new("changed family", Body("G", false)),
                older.Id,
                older.Revision
            );
            var oldProjection = await store.ProjectDatasetAsync(scope, older);
            await Assert
                .That(oldProjection.Body.GetProperty("cases")[0].GetProperty("input").ValueKind)
                .IsEqualTo(JsonValueKind.Null);
            var currentProjection = await store.ProjectDatasetAsync(scope, newer);
            await Assert
                .That(currentProjection.Body.GetProperty("cases")[0].GetProperty("input").ValueKind)
                .IsEqualTo(JsonValueKind.Object);
            var blocked = false;
            try
            {
                await store.FreezeAsync(scope, actor, "datasets", newer.Id, newer.Revision);
            }
            catch (ManagementException error) when (error.Code == "acceptance_source_exposed")
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

    [Test]
    public async Task ProtectedCasesRequireAuditedRevealAndCannotBecomeIndependentHoldoutsAgain()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "agentcmd-splits-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "management.db");
        var store = new RelationalControlPlaneStore(
            new("Sqlite", () =>
                new SqliteConnection(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = path,
                        Pooling = false,
                    }.ToString()
                )
            )
        );
        var scope = new WorkspaceScope("app", "test", "one");
        var actor = new ManagementActor("reviewer");
        try
        {
            await store.InitializeAsync();
            async Task<ManagementResource> Draft(string purpose) =>
                await store.SaveDraftAsync(
                    scope,
                    actor,
                    "datasets",
                    new(
                        purpose,
                        JsonSerializer.SerializeToElement(
                            new
                            {
                                purpose,
                                cases = new[]
                                {
                                    new
                                    {
                                        id = "case",
                                        sourceFamilyId = "source-family",
                                        input = new { Document = "held-out source" },
                                        reference = new
                                        {
                                            value = new { text = "answer" },
                                            completeness = "Exhaustive",
                                        },
                                    },
                                },
                            }
                        )
                    )
                );
            var draft = await Draft("Acceptance");
            var frozen = await store.FreezeAsync(
                scope,
                actor,
                "datasets",
                draft.Id,
                draft.Revision
            );
            var projected = await store.ProjectDatasetAsync(scope, frozen);
            await Assert
                .That(projected.Body.GetProperty("cases")[0].GetProperty("input").ValueKind)
                .IsEqualTo(JsonValueKind.Null);
            await Assert.That(projected.ContentHash).IsEqualTo(frozen.ContentHash);
            await Assert
                .That(
                    (
                        await store.GetDatasetExposureAsync(scope, frozen.Id)
                    ).IndependentAcceptanceEligible
                )
                .IsTrue();
            var revealed = await store.RevealDatasetCaseAsync(
                scope,
                actor,
                frozen.Id,
                "case",
                "Inspect failed case"
            );
            await Assert
                .That(revealed.GetProperty("input").GetProperty("Document").GetString())
                .IsEqualTo("held-out source");
            await Assert
                .That(
                    (
                        await store.GetDatasetExposureAsync(scope, frozen.Id)
                    ).IndependentAcceptanceEligible
                )
                .IsFalse();
            var next = await Draft("Acceptance");
            var rejected = false;
            try
            {
                await store.FreezeAsync(scope, actor, "datasets", next.Id, next.Revision);
            }
            catch (ManagementException error) when (error.Code == "acceptance_source_exposed")
            {
                rejected = true;
            }
            await Assert.That(rejected).IsTrue();
            var regression = await Draft("Regression");
            await store.FreezeAsync(scope, actor, "datasets", regression.Id, regression.Revision);
            var original = await store.ReadAsync(scope, "datasets", frozen.Id);
            await Assert.That(original!.ContentHash).IsEqualTo(frozen.ContentHash);
            await Assert.That(original.Revision).IsEqualTo(frozen.Revision);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
