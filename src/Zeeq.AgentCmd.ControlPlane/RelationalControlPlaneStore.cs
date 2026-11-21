using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Zeeq.AgentCmd.ControlPlane;

/// <summary>SQLite/PostgreSQL management transitions with scoped keys and transactional outbox authority.</summary>
/// <param name="database">Server-owned provider and connection factory.</param>
/// <param name="clock">Clock used for committed transitions and leases.</param>
/// <param name="assets">Optional scoped immutable assets required before replaying asset-backed cases.</param>
public sealed partial class RelationalControlPlaneStore(
    ManagementDatabase database,
    TimeProvider? clock = null,
    IControlPlaneAssetStore? assets = null
) : IControlPlaneStore
{
    internal IControlPlaneAssetStore? Assets { get; } = assets;
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public string Provider => database.Provider;

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Provider is not ("Sqlite" or "Postgres"))
        {
            throw new ManagementException("store_provider_unsupported");
        }
        await using var connection = await OpenAsync(cancellationToken);
        if (Provider == "Sqlite")
        {
            await ExecuteAsync(connection, null, "PRAGMA journal_mode=WAL", cancellationToken);
        }
        await TransactionAsync(
            async (db, tx) =>
            {
                if (Provider == "Postgres")
                {
                    await ExecuteAsync(
                        db,
                        tx,
                        "SELECT pg_advisory_xact_lock(1734820191)",
                        cancellationToken
                    );
                }
                await ExecuteAsync(
                    db,
                    tx,
                    """
                    CREATE TABLE IF NOT EXISTS cp_schema (version INTEGER NOT NULL);
                    INSERT INTO cp_schema(version) SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM cp_schema);
                    CREATE TABLE IF NOT EXISTS cp_documents (
                      scope TEXT NOT NULL, kind TEXT NOT NULL, id TEXT NOT NULL, revision BIGINT NOT NULL,
                      name TEXT NOT NULL, state TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
                      content_hash TEXT NOT NULL, body TEXT NOT NULL, PRIMARY KEY(scope,kind,id));
                    CREATE INDEX IF NOT EXISTS cp_documents_scope_kind ON cp_documents(scope,kind,updated_at);
                    CREATE TABLE IF NOT EXISTS cp_events (
                      id TEXT PRIMARY KEY, scope TEXT NOT NULL, actor TEXT NOT NULL, kind TEXT NOT NULL,
                      resource_kind TEXT NOT NULL, resource_id TEXT NOT NULL, revision BIGINT NOT NULL,
                      created_at TEXT NOT NULL, body TEXT NOT NULL);
                    CREATE TABLE IF NOT EXISTS cp_outbox (
                      id TEXT PRIMARY KEY, scope TEXT NOT NULL, state TEXT NOT NULL,
                      kind TEXT NOT NULL, resource_id TEXT NOT NULL, created_at TEXT NOT NULL,
                      body TEXT NOT NULL, receipt TEXT);
                    CREATE TABLE IF NOT EXISTS cp_work (
                      scope TEXT NOT NULL, id TEXT NOT NULL, job_id TEXT NOT NULL, build_id TEXT NOT NULL,
                      candidate_id TEXT NOT NULL, case_id TEXT NOT NULL, repeat_index INTEGER NOT NULL,
                      status TEXT NOT NULL, owner TEXT, epoch BIGINT NOT NULL DEFAULT 0, lease_until TEXT,
                      request TEXT NOT NULL, result TEXT, PRIMARY KEY(scope,id),
                      UNIQUE(scope,job_id,candidate_id,case_id,repeat_index));
                    CREATE INDEX IF NOT EXISTS cp_work_dispatch ON cp_work(scope,build_id,status);
                    CREATE TABLE IF NOT EXISTS cp_attempts (
                      scope TEXT NOT NULL, id TEXT NOT NULL, work_id TEXT NOT NULL, epoch BIGINT NOT NULL,
                      owner TEXT NOT NULL, state TEXT NOT NULL, started_at TEXT NOT NULL,
                      finished_at TEXT, receipt_hash TEXT, receipt TEXT, PRIMARY KEY(scope,id));
                    CREATE INDEX IF NOT EXISTS cp_attempts_work ON cp_attempts(scope,work_id,state);
                    CREATE TABLE IF NOT EXISTS cp_dataset_families (
                      scope TEXT NOT NULL, family TEXT NOT NULL, purpose TEXT NOT NULL, dataset_id TEXT NOT NULL,
                      PRIMARY KEY(scope,family,dataset_id));
                    CREATE TABLE IF NOT EXISTS cp_family_exposure (
                      scope TEXT NOT NULL, family TEXT NOT NULL, dataset_id TEXT NOT NULL, case_id TEXT NOT NULL,
                      actor TEXT NOT NULL, at TEXT NOT NULL, reason TEXT NOT NULL,
                      PRIMARY KEY(scope,family,dataset_id,case_id));
                    """,
                    cancellationToken
                );
                await using var command = Command(db, tx, "SELECT version FROM cp_schema");
                var version = Convert.ToInt32(
                    await command.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture
                );
                if (version != 1)
                {
                    throw new ManagementException("store_schema_version_unsupported", 503);
                }
                return true;
            },
            cancellationToken
        );
    }

    /// <inheritdoc />
    public async Task<ManagementResource?> ReadAsync(
        WorkspaceScope scope,
        string kind,
        string id,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadAsync(connection, null, scope, kind, id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ManagementResource>> ListAsync(
        WorkspaceScope scope,
        string kind,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(
            connection,
            null,
            "SELECT id,kind,revision,name,state,created_at,updated_at,content_hash,body FROM cp_documents WHERE scope=@scope AND kind=@kind ORDER BY updated_at DESC,id LIMIT 250",
            ("scope", scope.Key),
            ("kind", kind)
        );
        var resources = new List<ManagementResource>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            resources.Add(Resource(reader));
        }
        return resources;
    }

    /// <inheritdoc />
    public Task<ManagementResource> RegisterBuildAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        SaveResource request,
        CancellationToken cancellationToken = default
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                var id = request.Id
                    ?? Text(request.Body, "buildId")
                    ?? throw new ManagementException("build_identity_required");
                var body = ContentIdentity.Normalize(request.Body);
                var existing = await ReadAsync(db, tx, scope, "builds", id, cancellationToken);
                if (existing is not null)
                {
                    if (existing.ContentHash != ContentIdentity.Hash(body))
                    {
                        throw new ManagementException(
                            "build_identity_conflict",
                            409,
                            existing.Revision
                        );
                    }
                    return existing;
                }
                return await InsertAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    "builds",
                    id,
                    request.Name,
                    "Registered",
                    body,
                    cancellationToken
                );
            },
            cancellationToken
        );

    /// <inheritdoc />
    public Task<ManagementResource> SaveDraftAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string kind,
        SaveResource request,
        string? id = null,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default
    )
    {
        if (
            kind
            is not (
                "publications"
                or "datasets"
                or "benchmarks"
                or "judge-configurations"
                or "source-profiles"
                or "bindings"
            )
        )
        {
            throw new ManagementException("draft_kind_invalid");
        }
        return TransactionAsync(
            async (db, tx) =>
            {
                var body = ContentIdentity.Normalize(request.Body);
                if (id is null)
                {
                    var created = await InsertAsync(
                        db,
                        tx,
                        scope,
                        actor,
                        kind,
                        Guid.NewGuid().ToString("N"),
                        request.Name,
                        "Draft",
                        body,
                        cancellationToken
                    );
                    await RecordDeclaredExposureAsync(
                        db,
                        tx,
                        scope,
                        actor,
                        created,
                        cancellationToken
                    );
                    return created;
                }
                var existing = await ReadAsync(db, tx, scope, kind, id, cancellationToken)
                    ?? throw new ManagementException("not_found", 404);
                RequireRevision(existing, expectedRevision);
                if (existing.State != "Draft")
                {
                    throw new ManagementException("immutable_resource", 409, existing.Revision);
                }
                await RecordDeclaredExposureAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    existing,
                    cancellationToken
                );
                var saved = await ReplaceAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    existing,
                    request.Name,
                    "Draft",
                    body,
                    "draft.updated",
                    cancellationToken
                );
                await RecordDeclaredExposureAsync(db, tx, scope, actor, saved, cancellationToken);
                return saved;
            },
            cancellationToken
        );
    }

    /// <inheritdoc />
    public Task<ManagementResource> FreezeAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string kind,
        string id,
        long expectedRevision,
        CancellationToken cancellationToken = default
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                if (
                    kind
                    is not ("datasets" or "benchmarks" or "publications" or "judge-configurations")
                )
                {
                    throw new ManagementException("freeze_kind_invalid");
                }
                var resource = await ReadAsync(db, tx, scope, kind, id, cancellationToken)
                    ?? throw new ManagementException("not_found", 404);
                RequireRevision(resource, expectedRevision);
                if (resource.State != "Draft")
                {
                    throw new ManagementException("immutable_resource", 409, resource.Revision);
                }
                if (kind == "datasets")
                {
                    await FreezeDatasetFamiliesAsync(db, tx, scope, resource, cancellationToken);
                }
                return await ReplaceAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    resource,
                    resource.Name,
                    "Frozen",
                    ContentIdentity.Normalize(resource.Body),
                    "content.frozen",
                    cancellationToken
                );
            },
            cancellationToken
        );

    internal async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = database.CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken);
            if (connection is SqliteConnection)
            {
                await ExecuteAsync(
                    connection,
                    null,
                    "PRAGMA busy_timeout=10000; PRAGMA foreign_keys=ON",
                    cancellationToken
                );
            }
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    internal async Task<T> TransactionAsync<T>(
        Func<DbConnection, DbTransaction, Task<T>> action,
        CancellationToken cancellationToken
    )
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection is SqliteConnection sqlite
            ? sqlite.BeginTransaction(deferred: false)
            : await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken
            );
        try
        {
            var result = await action(connection, transaction);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (SqliteException failure) when (failure.SqliteErrorCode == 19)
        {
            throw new ManagementException("identity_conflict", 409);
        }
        catch (PostgresException failure)
            when (failure.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ManagementException("identity_conflict", 409);
        }
    }

    internal static DbCommand Command(
        DbConnection db,
        DbTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] values
    )
    {
        var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    internal static async Task<int> ExecuteAsync(
        DbConnection db,
        DbTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] values
    )
    {
        await using var command = Command(db, transaction, sql, values);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task<ManagementResource?> ReadAsync(
        DbConnection db,
        DbTransaction? transaction,
        WorkspaceScope scope,
        string kind,
        string id,
        CancellationToken cancellationToken
    )
    {
        await using var command = Command(
            db,
            transaction,
            "SELECT id,kind,revision,name,state,created_at,updated_at,content_hash,body FROM cp_documents WHERE scope=@scope AND kind=@kind AND id=@id",
            ("scope", scope.Key),
            ("kind", kind),
            ("id", id)
        );
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Resource(reader) : null;
    }

    internal async Task<ManagementResource> InsertAsync(
        DbConnection db,
        DbTransaction tx,
        WorkspaceScope scope,
        ManagementActor actor,
        string kind,
        string id,
        string name,
        string state,
        string body,
        CancellationToken cancellationToken
    )
    {
        if (
            string.IsNullOrWhiteSpace(name)
            || name.Length > 200
            || string.IsNullOrWhiteSpace(id)
            || id.Length > 512
        )
        {
            throw new ManagementException("resource_identity_invalid");
        }
        var at = time.GetUtcNow();
        var hash = ContentIdentity.Hash(body);
        await ExecuteAsync(
            db,
            tx,
            "INSERT INTO cp_documents(scope,kind,id,revision,name,state,created_at,updated_at,content_hash,body) VALUES(@scope,@kind,@id,1,@name,@state,@at,@at,@hash,@body)",
            cancellationToken,
            ("scope", scope.Key),
            ("kind", kind),
            ("id", id),
            ("name", name),
            ("state", state),
            ("at", Timestamp(at)),
            ("hash", hash),
            ("body", body)
        );
        await EventAsync(
            db,
            tx,
            scope,
            actor,
            kind + ".created",
            kind,
            id,
            1,
            new { state, contentHash = hash },
            cancellationToken
        );
        return new(id, kind, 1, name, state, at, at, hash, Element(body));
    }

    internal async Task<ManagementResource> ReplaceAsync(
        DbConnection db,
        DbTransaction tx,
        WorkspaceScope scope,
        ManagementActor actor,
        ManagementResource resource,
        string name,
        string state,
        string body,
        string eventKind,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
        {
            throw new ManagementException("resource_name_invalid");
        }
        var at = time.GetUtcNow();
        var hash = ContentIdentity.Hash(body);
        var count = await ExecuteAsync(
            db,
            tx,
            "UPDATE cp_documents SET revision=revision+1,name=@name,state=@state,updated_at=@at,content_hash=@hash,body=@body WHERE scope=@scope AND kind=@kind AND id=@id AND revision=@expected",
            cancellationToken,
            ("scope", scope.Key),
            ("kind", resource.Kind),
            ("id", resource.Id),
            ("expected", resource.Revision),
            ("name", name),
            ("state", state),
            ("at", Timestamp(at)),
            ("hash", hash),
            ("body", body)
        );
        if (count != 1)
        {
            throw new ManagementException("revision_conflict", 409);
        }
        await EventAsync(
            db,
            tx,
            scope,
            actor,
            eventKind,
            resource.Kind,
            resource.Id,
            resource.Revision + 1,
            new { state, contentHash = hash },
            cancellationToken
        );
        return resource with
        {
            Revision = resource.Revision + 1,
            Name = name,
            State = state,
            UpdatedAt = at,
            ContentHash = hash,
            Body = Element(body),
        };
    }

    internal async Task EventAsync(
        DbConnection db,
        DbTransaction tx,
        WorkspaceScope scope,
        ManagementActor actor,
        string kind,
        string resourceKind,
        string resourceId,
        long revision,
        object value,
        CancellationToken cancellationToken
    )
    {
        var id = Guid.NewGuid().ToString("N");
        var at = Timestamp(time.GetUtcNow());
        var body = JsonSerializer.Serialize(value, Json);
        await ExecuteAsync(
            db,
            tx,
            "INSERT INTO cp_events(id,scope,actor,kind,resource_kind,resource_id,revision,created_at,body) VALUES(@id,@scope,@actor,@kind,@resourceKind,@resourceId,@revision,@at,@body)",
            cancellationToken,
            ("id", id),
            ("scope", scope.Key),
            ("actor", actor.Subject),
            ("kind", kind),
            ("resourceKind", resourceKind),
            ("resourceId", resourceId),
            ("revision", revision),
            ("at", at),
            ("body", body)
        );
        await ExecuteAsync(
            db,
            tx,
            "INSERT INTO cp_outbox(id,scope,state,kind,resource_id,created_at,body) VALUES(@id,@scope,'Pending',@kind,@resourceId,@at,@body)",
            cancellationToken,
            ("id", id),
            ("scope", scope.Key),
            ("kind", kind),
            ("resourceId", resourceId),
            ("at", at),
            ("body", body)
        );
    }

    internal static void RequireRevision(ManagementResource resource, long? expected)
    {
        if (expected is null)
        {
            throw new ManagementException("revision_precondition_required", 428);
        }
        if (resource.Revision != expected)
        {
            throw new ManagementException("revision_conflict", 409, resource.Revision);
        }
    }

    internal static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    internal static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    internal static string? Text(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static ManagementResource Resource(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.GetString(4),
            DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
            reader.GetString(7),
            Element(reader.GetString(8))
        );
}
