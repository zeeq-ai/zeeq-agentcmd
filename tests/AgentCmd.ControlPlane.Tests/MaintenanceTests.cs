using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd.ControlPlane;
using Zeeq.AgentCmd.ControlPlane.Host;

namespace AgentCmd.ControlPlane.Tests;

public class MaintenanceTests
{
    [Test]
    public async Task BackupRestoresExactEvidenceAndRejectsRunningHostsExistingTargetsAndCorruption()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "agentcmd-maintenance-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(root);
        var original = new ControlPlaneOptions
        {
            DatabasePath = Path.Combine(root, "original", "management.db"),
            AssetPath = Path.Combine(root, "original", "assets"),
        };
        var target = new ControlPlaneOptions
        {
            DatabasePath = Path.Combine(root, "restored", "management.db"),
            AssetPath = Path.Combine(root, "restored", "assets"),
        };
        var scope = new WorkspaceScope("app", "test", "partition");
        Directory.CreateDirectory(Path.GetDirectoryName(original.DatabasePath)!);
        RelationalControlPlaneStore Store(ControlPlaneOptions options) =>
            new(
                new("Sqlite", () =>
                    new SqliteConnection(
                        new SqliteConnectionStringBuilder
                        {
                            DataSource = options.DatabasePath,
                            Pooling = false,
                        }.ToString()
                    )
                )
            );
        try
        {
            var store = Store(original);
            await store.InitializeAsync();
            await Reject(
                async () =>
                {
                    _ = await SqliteMaintenance.TryRunAsync([
                        "--maintenance=backup",
                        "--archive=" + Path.Combine(root, "implicit"),
                    ]);
                },
                "maintenance_explicit_paths_required"
            );
            if (!OperatingSystem.IsWindows())
            {
                var alias = Path.Combine(root, "database-alias.db");
                File.CreateSymbolicLink(alias, original.DatabasePath);
                await Reject(
                    () =>
                        Task.Run(() =>
                        {
                            using var lease = new SqliteHostLease(new() { DatabasePath = alias });
                        }),
                    "sqlite_database_alias_unsupported"
                );
            }
            var resource = await store.RegisterBuildAsync(
                scope,
                new("operator"),
                new(
                    "build",
                    JsonSerializer.SerializeToElement(
                        new { buildId = "offline", content = "immutable" }
                    )
                )
            );
            var asset = await new FileAssetStore(original.AssetPath).PutAsync(
                scope,
                new MemoryStream(Encoding.UTF8.GetBytes("retained source"))
            );
            var archive = Path.Combine(root, "snapshot");
            using (var lease = new SqliteHostLease(original))
            {
                await Reject(
                    () => SqliteMaintenance.RunAsync(original, "backup", archive),
                    "sqlite_host_already_running"
                );
            }
            await SqliteMaintenance.RunAsync(original, "backup", archive);
            await SqliteMaintenance.RunAsync(original, "verify", archive);
            await SqliteMaintenance.RunAsync(target, "restore", archive);
            var restored = await Store(target).ReadAsync(scope, "builds", resource.Id);
            await Assert.That(restored!.ContentHash).IsEqualTo(resource.ContentHash);
            await using (
                var bytes = await new FileAssetStore(target.AssetPath).OpenAsync(scope, asset.Hash)
            )
            using (var text = new StreamReader(bytes))
            {
                await Assert.That(await text.ReadToEndAsync()).IsEqualTo("retained source");
            }
            await Reject(
                () => SqliteMaintenance.RunAsync(target, "restore", archive),
                "restore_requires_new_destination"
            );
            var retained = Directory
                .GetFiles(Path.Combine(archive, "assets"), asset.Hash, SearchOption.AllDirectories)
                .Single();
            await File.AppendAllTextAsync(retained, "corrupt");
            await Reject(
                () => SqliteMaintenance.RunAsync(original, "verify", archive),
                "backup_checksum_mismatch"
            );
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task Reject(Func<Task> action, string code)
    {
        string? observed = null;
        try
        {
            await action();
        }
        catch (ManagementException error)
        {
            observed = error.Code;
        }
        await Assert.That(observed).IsEqualTo(code);
    }
}
