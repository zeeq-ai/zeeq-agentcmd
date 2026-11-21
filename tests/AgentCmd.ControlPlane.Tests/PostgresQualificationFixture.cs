using System.Security.Cryptography;
using System.Text.Json;
using DotNet.Testcontainers.Configurations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using TUnit.Core.Interfaces;
using Zeeq.AgentCmd.ControlPlane;

namespace AgentCmd.ControlPlane.Tests;

/// <summary>One opt-in owner for an isolated PostgreSQL container shared by the adapter contract tests.</summary>
public sealed class PostgresQualificationFixture : IAsyncInitializer, IAsyncDisposable
{
    private PostgreSqlContainer? container;
    private readonly string runId = Guid.NewGuid().ToString("N");
    private string? evidenceDirectory;
    private string? containerId;
    private bool started;
    private bool disposed;
    private string stage = "NotStarted";
    private const string Image = "postgres:17.6-alpine";

    public async Task InitializeAsync()
    {
        // TUnit constructs shared data sources during discovery. No constructor, field initializer,
        // or unselected test may initialize Docker or resolve any application credential.
        if (Environment.GetEnvironmentVariable("AGENTCMD_POSTGRES_TEST") != "1")
        {
            throw new InvalidOperationException(
                "Set AGENTCMD_POSTGRES_TEST=1 to run isolated PostgreSQL qualification."
            );
        }
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (
            directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json"))
        )
        {
            directory = directory.Parent;
        }
        evidenceDirectory = Path.Combine(
            directory?.FullName
                ?? throw new InvalidOperationException("Repository root unavailable."),
            "artifacts",
            "control-plane",
            "postgres",
            runId
        );
        Directory.CreateDirectory(evidenceDirectory);
        var config = Path.Combine(evidenceDirectory, "docker-config");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "config.json"), "{\"auths\":{}}\n");
        // This dedicated test process uses public images and no credential helpers or user registry auth.
        Environment.SetEnvironmentVariable("DOCKER_CONFIG", config);
        Environment.SetEnvironmentVariable("DOCKER_AUTH_CONFIG", "{\"auths\":{}}");
        Environment.SetEnvironmentVariable("DOCKER_CONTEXT", null);
        var endpoint = Environment.GetEnvironmentVariable("DOCKER_HOST");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            var desktop = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".docker",
                "run",
                "docker.sock"
            );
            endpoint = "unix://" + (File.Exists(desktop) ? desktop : "/var/run/docker.sock");
        }
        if (
            !endpoint.StartsWith("unix://", StringComparison.Ordinal)
            && !endpoint.StartsWith("npipe://", StringComparison.Ordinal)
        )
        {
            throw new InvalidOperationException(
                "Qualification requires a local Unix socket or named-pipe Docker endpoint."
            );
        }
        Environment.SetEnvironmentVariable("DOCKER_HOST", endpoint);
        stage = "Starting";
        try
        {
            container = new PostgreSqlBuilder(Image)
                .WithDockerEndpoint(endpoint)
                .WithDatabase("qualification")
                .WithUsername("qualification")
                .WithPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))
                .WithLabel("agentcmd.qualification", runId)
                .WithLogger(NullLogger.Instance)
                // Module defaults disable fsync/full_page_writes/synchronous_commit for speed.
                // Qualification must retain normal PostgreSQL durability across a real restart.
                .WithCommand(new OverwriteEnumerable<string>(Array.Empty<string>()))
                .Build();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await container.StartAsync(deadline.Token);
            containerId = container.Id;
            started = true;
            stage = "Ready";
            await using var connection = CreateConnection();
            await connection.OpenAsync(deadline.Token);
            await using var command = new NpgsqlCommand(
                "SELECT version(),current_setting('fsync'),current_setting('full_page_writes'),current_setting('synchronous_commit')",
                connection
            );
            await using var reader = await command.ExecuteReaderAsync(deadline.Token);
            await reader.ReadAsync(deadline.Token);
            var server = reader.GetString(0);
            var durability = new[]
            {
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
            };
            if (durability.Any(value => value != "on"))
            {
                throw new InvalidOperationException(
                    "PostgreSQL durability settings were not enabled."
                );
            }
            await File.WriteAllTextAsync(
                Path.Combine(evidenceDirectory, "server.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        runId,
                        containerId,
                        image = Image,
                        serverVersion = server,
                        fsync = durability[0],
                        fullPageWrites = durability[1],
                        synchronousCommit = durability[2],
                    },
                    new JsonSerializerOptions { WriteIndented = true }
                )
            );
            await EvidenceAsync();
        }
        catch (Exception error)
        {
            stage = "Failed:" + error.GetType().Name;
            await DisposeAsync();
            throw new InvalidOperationException(
                "Isolated PostgreSQL fixture failed at "
                    + stage
                    + ". See sanitized lifecycle evidence."
            );
        }
    }

    internal NpgsqlConnection CreateConnection(string? database = null)
    {
        var current = container
            ?? throw new InvalidOperationException("PostgreSQL fixture is not initialized.");
        var connection = new NpgsqlConnectionStringBuilder(current.GetConnectionString())
        {
            Pooling = false,
            IncludeErrorDetail = false,
            Timeout = 10,
            CommandTimeout = 30,
        };
        if (database is not null)
        {
            connection.Database = database;
        }
        return new(connection.ConnectionString);
    }

    internal RelationalControlPlaneStore Store(string database, TimeProvider? clock = null) =>
        new(new("Postgres", () => CreateConnection(database)), clock);

    internal async Task<string> NewDatabaseAsync()
    {
        var database = "qualification_" + Guid.NewGuid().ToString("N");
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        // The identifier is entirely fixture-generated ASCII, never a caller-supplied database name.
        await using var command = new NpgsqlCommand("CREATE DATABASE " + database, connection);
        await command.ExecuteNonQueryAsync();
        return database;
    }

    internal async Task RestartAsync()
    {
        var current = container ?? throw new InvalidOperationException("Fixture not started.");
        stage = "Restarting";
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await current.StopAsync(deadline.Token);
        await current.StartAsync(deadline.Token);
        if (current.Id != containerId)
        {
            throw new InvalidOperationException("Restart unexpectedly replaced the container.");
        }
        stage = "Restarted";
        await EvidenceAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        try
        {
            if (container is not null)
            {
                await container.DisposeAsync();
            }
            disposed = true;
        }
        finally
        {
            await EvidenceAsync();
        }
    }

    private Task EvidenceAsync() =>
        evidenceDirectory is null
            ? Task.CompletedTask
            : File.WriteAllTextAsync(
                Path.Combine(evidenceDirectory, "lifecycle.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        runId,
                        containerId,
                        image = Image,
                        stage,
                        started,
                        disposalCompleted = disposed,
                        ownsLifecycle = "Testcontainers",
                        generatedCredentialsOnly = true,
                    },
                    new JsonSerializerOptions { WriteIndented = true }
                )
            );
}
