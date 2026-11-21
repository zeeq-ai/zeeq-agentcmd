using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd.ControlPlane;

namespace AgentCmd.ControlPlane.Tests;

public class JobAssetTests
{
    [Test]
    public async Task BacktestRequiresRetainedAssetInAuthorizedScope()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "agentcmd-assets-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(root);
        var store = new RelationalControlPlaneStore(
            new ManagementDatabase("Sqlite", () =>
                new SqliteConnection($"Data Source={Path.Combine(root, "data.db")}")
            )
        );
        var assets = new FileAssetStore(Path.Combine(root, "assets"));
        var scope = new WorkspaceScope("app", "test", "one");
        var actor = new ManagementActor("operator");
        await store.InitializeAsync();
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("retained source"));
        var asset = await assets.PutAsync(scope, content);
        var dataset = await store.SaveDraftAsync(
            scope,
            actor,
            "datasets",
            new(
                "dataset",
                JsonSerializer.SerializeToElement(
                    new
                    {
                        cases = new[]
                        {
                            new
                            {
                                id = "case",
                                input = new { document = asset.Hash },
                                assets = new[] { asset.Hash },
                            },
                        },
                    }
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
                        primaryCriterionId = "metric",
                        repeats = 1,
                        criteria = new[] { new { id = "metric" } },
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
        await store.RegisterBuildAsync(
            scope,
            actor,
            new(
                "build",
                JsonSerializer.SerializeToElement(
                    new
                    {
                        buildId = "build",
                        commands = new[]
                        {
                            new
                            {
                                id = "c",
                                revision = 1,
                                contractSignature = "sig",
                            },
                        },
                    }
                ),
                "build"
            )
        );
        var publication = await store.SaveDraftAsync(
            scope,
            actor,
            "publications",
            new(
                "publication",
                JsonSerializer.SerializeToElement(
                    new
                    {
                        buildId = "build",
                        commands = new[]
                        {
                            new
                            {
                                commandId = "c",
                                commandRevision = 1,
                                contractSignature = "sig",
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
        var request = new CreateJobRequest(
            "Backtest",
            dataset.Id,
            benchmark.Id,
            [new("candidate", "Candidate", "build", publication.Id, "c", "v")],
            true
        );
        var denied = false;
        try
        {
            await store.CreateBacktestAsync(scope, actor, "without assets", request);
        }
        catch (ManagementException error)
        {
            denied = error.Code == "case_asset_unavailable";
        }
        await Assert.That(denied).IsTrue();
        var unavailable = false;
        try
        {
            await store.CreateBacktestAsync(
                scope,
                actor,
                "wrong store",
                request,
                new FileAssetStore(Path.Combine(root, "other"))
            );
        }
        catch (ManagementException error)
        {
            unavailable = error.Code == "asset_not_retained";
        }
        await Assert.That(unavailable).IsTrue();
        var job = await store.CreateBacktestAsync(scope, actor, "ready", request, assets);
        await Assert.That((await store.GetJobAsync(scope, job.Id)).Work.Count).IsEqualTo(1);
    }
}
