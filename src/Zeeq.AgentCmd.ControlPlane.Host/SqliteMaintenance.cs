using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Zeeq.AgentCmd.ControlPlane;

namespace Zeeq.AgentCmd.ControlPlane.Host;

/// <summary>Offline, host-wide snapshots keep the management database and its immutable local assets together.</summary>
internal static class SqliteMaintenance
{
    internal sealed record BackupFile(string Path, string Sha256, long Length);

    internal sealed record BackupIndex(
        int FormatVersion,
        DateTimeOffset CreatedAt,
        BackupFile Database,
        BackupFile[] Assets
    );

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    internal static async Task<bool> TryRunAsync(string[] args)
    {
        var config = new ConfigurationBuilder().AddCommandLine(args).Build();
        var action = config["maintenance"];
        if (action is null)
        {
            return false;
        }
        if (
            action != "verify"
            && (
                !Path.IsPathFullyQualified(config["ControlPlane:DatabasePath"] ?? "")
                || !Path.IsPathFullyQualified(config["ControlPlane:AssetPath"] ?? "")
            )
        )
        {
            // Maintenance is host-wide. Require explicit identities instead of silently replacing deployment settings with defaults.
            throw new ManagementException("maintenance_explicit_paths_required");
        }
        var options = config.GetSection("ControlPlane").Get<ControlPlaneOptions>() ?? new();
        var archive = config["archive"] ?? throw new ManagementException("backup_archive_required");
        await RunAsync(options, action, archive);
        Console.WriteLine(JsonSerializer.Serialize(new { action, status = "Completed" }));
        return true;
    }

    internal static async Task RunAsync(ControlPlaneOptions options, string action, string archive)
    {
        if (options.Provider != "Sqlite")
        {
            throw new ManagementException("maintenance_requires_sqlite");
        }
        var directory = Path.GetFullPath(archive);
        if (action == "verify")
        {
            _ = await VerifyAsync(directory);
            return;
        }
        using var lease = new SqliteHostLease(options);
        if (action == "backup")
        {
            await BackupAsync(options, directory);
        }
        else if (action == "restore")
        {
            await RestoreAsync(options, directory);
        }
        else
        {
            throw new ManagementException("maintenance_action_invalid");
        }
    }

    private static async Task BackupAsync(ControlPlaneOptions options, string directory)
    {
        var database = Path.GetFullPath(options.DatabasePath);
        var assets = Path.GetFullPath(options.AssetPath);
        if (!File.Exists(database) || Directory.Exists(directory) || File.Exists(directory))
        {
            throw new ManagementException("backup_destination_or_source_invalid", 409);
        }
        if (Within(directory, assets) || Within(database, directory) || Within(assets, directory))
        {
            throw new ManagementException("backup_paths_overlap");
        }
        var temporary = directory + ".pending-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temporary);
        try
        {
            var snapshot = Path.Combine(temporary, "management.db");
            // SQLite's backup API includes committed WAL data; copying the live .db alone does not.
            using (
                var source = new SqliteConnection(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = database,
                        Mode = SqliteOpenMode.ReadOnly,
                    }.ToString()
                )
            )
            using (
                var destination = new SqliteConnection(
                    new SqliteConnectionStringBuilder { DataSource = snapshot }.ToString()
                )
            )
            {
                await source.OpenAsync();
                await destination.OpenAsync();
                source.BackupDatabase(destination);
            }
            await CheckDatabaseAsync(snapshot);
            var files = new List<BackupFile>();
            if (Directory.Exists(assets))
            {
                foreach (var path in AssetFiles(assets))
                {
                    var relative =
                        "assets/"
                        + Path.GetRelativePath(assets, path)
                            .Replace(Path.DirectorySeparatorChar, '/');
                    var file = await DescribeAsync(path, relative);
                    if (file.Sha256 != Path.GetFileName(path))
                    {
                        throw new ManagementException("asset_corrupt", 409);
                    }
                    var copy = Path.Combine(temporary, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                    File.Copy(path, copy, false);
                    files.Add(file);
                }
            }
            var index = new BackupIndex(
                1,
                DateTimeOffset.UtcNow,
                await DescribeAsync(snapshot, "management.db"),
                files.ToArray()
            );
            await File.WriteAllTextAsync(
                Path.Combine(temporary, "backup.json"),
                JsonSerializer.Serialize(index, Json)
            );
            _ = await VerifyAsync(temporary);
            Directory.Move(temporary, directory);
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, true);
            }
        }
    }

    private static async Task RestoreAsync(ControlPlaneOptions options, string directory)
    {
        var index = await VerifyAsync(directory);
        var database = Path.GetFullPath(options.DatabasePath);
        var assets = Path.GetFullPath(options.AssetPath);
        if (
            File.Exists(database)
            || File.Exists(database + "-wal")
            || File.Exists(database + "-shm")
            || Directory.Exists(assets)
        )
        {
            throw new ManagementException("restore_requires_new_destination", 409);
        }
        if (
            Within(directory, assets)
            || Within(database, directory)
            || Within(assets, directory)
            || Within(database, assets)
        )
        {
            throw new ManagementException("backup_paths_overlap");
        }
        var temporaryDatabase = database + ".restoring-" + Guid.NewGuid().ToString("N");
        var temporaryAssets = assets + ".restoring-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        Directory.CreateDirectory(temporaryAssets);
        var assetsMoved = false;
        try
        {
            File.Copy(Path.Combine(directory, index.Database.Path), temporaryDatabase, false);
            foreach (var file in index.Assets)
            {
                var destination = Path.Combine(temporaryAssets, file.Path["assets/".Length..]);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(directory, file.Path), destination, false);
                if (await DescribeAsync(destination, file.Path) != file)
                {
                    throw new ManagementException("backup_checksum_mismatch", 409);
                }
            }
            if (await DescribeAsync(temporaryDatabase, index.Database.Path) != index.Database)
            {
                throw new ManagementException("backup_checksum_mismatch", 409);
            }
            await CheckDatabaseAsync(temporaryDatabase);
            Directory.Move(temporaryAssets, assets);
            assetsMoved = true;
            File.Move(temporaryDatabase, database, false);
        }
        catch
        {
            if (assetsMoved)
            {
                Directory.Delete(assets, true);
            }
            throw;
        }
        finally
        {
            if (File.Exists(temporaryDatabase))
            {
                File.Delete(temporaryDatabase);
            }
            if (Directory.Exists(temporaryAssets))
            {
                Directory.Delete(temporaryAssets, true);
            }
        }
    }

    private static async Task<BackupIndex> VerifyAsync(string directory)
    {
        var index = JsonSerializer.Deserialize<BackupIndex>(
            await File.ReadAllTextAsync(Path.Combine(directory, "backup.json")),
            Json
        ) ?? throw new ManagementException("backup_index_invalid");
        if (
            index.FormatVersion != 1
            || index.Database.Path != "management.db"
            || index.Assets is null
            || index.Assets.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count()
                != index.Assets.Length
        )
        {
            throw new ManagementException("backup_index_invalid");
        }
        foreach (var file in index.Assets.Prepend(index.Database))
        {
            if (file != index.Database && !ValidAssetPath(file.Path))
            {
                throw new ManagementException("backup_asset_path_invalid");
            }
            var path = Path.Combine(directory, file.Path);
            RejectLink(path);
            var actual = await DescribeAsync(path, file.Path);
            if (
                actual != file
                || file != index.Database && actual.Sha256 != Path.GetFileName(file.Path)
            )
            {
                throw new ManagementException("backup_checksum_mismatch", 409);
            }
        }
        await CheckDatabaseAsync(Path.Combine(directory, index.Database.Path));
        return index;
    }

    private static async Task CheckDatabaseAsync(string path)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString()
        );
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check";
        if ((string?)await command.ExecuteScalarAsync() != "ok")
        {
            throw new ManagementException("backup_database_invalid", 409);
        }
        command.CommandText = "SELECT version FROM cp_schema";
        if (
            Convert.ToInt32(
                await command.ExecuteScalarAsync(),
                System.Globalization.CultureInfo.InvariantCulture
            ) != 1
        )
        {
            throw new ManagementException("store_schema_version_unsupported", 409);
        }
    }

    private static async Task<BackupFile> DescribeAsync(string path, string relative)
    {
        await using var stream = File.OpenRead(path);
        return new(
            relative,
            Convert.ToHexStringLower(await SHA256.HashDataAsync(stream)),
            stream.Length
        );
    }

    private static IEnumerable<string> AssetFiles(string root)
    {
        if (Directory.EnumerateFiles(root).Any())
        {
            throw new ManagementException("backup_asset_path_invalid");
        }
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            RejectLink(directory);
            if (Directory.EnumerateDirectories(directory).Any())
            {
                throw new ManagementException("backup_asset_path_invalid");
            }
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                RejectLink(path);
                if (Path.GetFileName(path).StartsWith(".pending-", StringComparison.Ordinal))
                {
                    continue;
                }
                if (
                    !ValidAssetPath(
                        "assets/" + Path.GetFileName(directory) + "/" + Path.GetFileName(path)
                    )
                )
                {
                    throw new ManagementException("backup_asset_path_invalid");
                }
                yield return path;
            }
        }
    }

    private static bool ValidAssetPath(string path)
    {
        var parts = path.Split('/');
        return parts.Length == 3
            && parts[0] == "assets"
            && parts.Skip(1).All(part => part.Length == 64 && part.All(char.IsAsciiHexDigitLower));
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ManagementException("backup_links_not_supported");
        }
    }

    private static bool Within(string path, string parent) =>
        path == parent
        || path.StartsWith(
            parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.Ordinal
        );
}
