using System.Data;
using System.Data.Common;
using Npgsql;

namespace Zeeq.AgentCmd.ControlPlane;

/// <summary>
/// Optional PostgreSQL runtime publication storage. It is independent of the control-plane database.
/// Initialization is explicit; construction performs no I/O and never resolves a credential.
/// </summary>
public sealed class PublicationPostgresTarget : IPublicationTarget
{
    private readonly string credentialReference;
    private readonly Func<string, CancellationToken, ValueTask<NpgsqlConnection>> createConnection;

    /// <summary>Captures a server binding and a reference-only credential resolver.</summary>
    /// <param name="profile">Authorized target and exact runtime binding.</param>
    /// <param name="credentialReference">Server secret reference, never a raw connection string.</param>
    /// <param name="createConnection">Server-only resolver producing a new unopened connection for the reference.</param>
    public PublicationPostgresTarget(
        PublicationTargetProfile profile,
        string credentialReference,
        Func<string, CancellationToken, ValueTask<NpgsqlConnection>> createConnection
    )
    {
        if (
            string.IsNullOrWhiteSpace(credentialReference)
            || credentialReference.Contains('=')
            || credentialReference.Contains(';')
        )
        {
            throw new ManagementException("publication_credential_reference_required");
        }
        Profile = profile;
        this.credentialReference = credentialReference;
        this.createConnection = createConnection;
    }

    /// <inheritdoc />
    public PublicationTargetProfile Profile { get; }

    /// <inheritdoc />
    // Qualified against the explicit PostgreSQL 17.6 contract fixture. This is not a reachability probe for a configured deployment.
    public PublicationTargetCapabilities Capabilities => new("Postgres", true, true, true);

    /// <summary>Explicit bootstrap/migration action. No request handler or constructor calls this method.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken);
        await using var tx = await db.BeginTransactionAsync(cancellationToken);
        await using var command = RelationalControlPlaneStore.Command(
            db,
            tx,
            """
            SELECT pg_advisory_xact_lock(1734820192);
            CREATE TABLE IF NOT EXISTS agentcmd_publication_content (
              stream TEXT NOT NULL, scope TEXT NOT NULL, revision TEXT NOT NULL,
              content_hash TEXT NOT NULL, body TEXT NOT NULL,
              PRIMARY KEY(stream,scope,revision));
            CREATE TABLE IF NOT EXISTS agentcmd_publication_pointer (
              stream TEXT NOT NULL, scope TEXT NOT NULL, generation BIGINT NOT NULL CHECK(generation>0),
              revision TEXT NOT NULL, content_hash TEXT NOT NULL, operation_id TEXT NOT NULL,
              PRIMARY KEY(stream,scope),
              FOREIGN KEY(stream,scope,revision) REFERENCES agentcmd_publication_content(stream,scope,revision));
            CREATE TABLE IF NOT EXISTS agentcmd_publication_history (
              stream TEXT NOT NULL, scope TEXT NOT NULL, generation BIGINT NOT NULL,
              revision TEXT NOT NULL, content_hash TEXT NOT NULL, operation_id TEXT NOT NULL,
              PRIMARY KEY(stream,scope,generation),
              FOREIGN KEY(stream,scope,revision) REFERENCES agentcmd_publication_content(stream,scope,revision));
            """
        );
        await command.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PublicationPointer?> ReadCurrentAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await OpenAsync(cancellationToken);
        return await ReadPointerAsync(db, null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PublicationContent?> ReadRevisionAsync(
        string revision,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await OpenAsync(cancellationToken);
        return await ReadContentAsync(db, null, revision, cancellationToken);
    }

    /// <inheritdoc />
    public async Task PutAsync(
        PublicationContent content,
        CancellationToken cancellationToken = default
    )
    {
        _ = PublicationCodec.Snapshot(content);
        await using var db = await OpenAsync(cancellationToken);
        await using var tx = await db.BeginTransactionAsync(cancellationToken);
        await using (
            var command = RelationalControlPlaneStore.Command(
                db,
                tx,
                """
                INSERT INTO agentcmd_publication_content(stream,scope,revision,content_hash,body)
                VALUES(@stream,@scope,@revision,@hash,@body) ON CONFLICT(stream,scope,revision) DO NOTHING
                """,
                ("stream", Profile.Id),
                ("scope", Profile.Scope.Key),
                ("revision", content.Revision),
                ("hash", content.ContentHash),
                ("body", content.Json)
            )
        )
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (await ReadContentAsync(db, tx, content.Revision, cancellationToken) != content)
        {
            throw new ManagementException("publication_revision_conflict", 409);
        }
        await tx.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PublicationActivation> ActivateAsync(
        PublicationPointer? expected,
        PublicationContent content,
        string operationId,
        CancellationToken cancellationToken = default
    )
    {
        if (!ExperimentIdentifier.IsValid(operationId))
        {
            throw new ManagementException("publication_operation_invalid");
        }
        PublicationCodec.Verify(content);
        await using var db = await OpenAsync(cancellationToken);
        await using var tx = await db.BeginTransactionAsync(cancellationToken);
        if (await ReadContentAsync(db, tx, content.Revision, cancellationToken) != content)
        {
            throw new ManagementException("publication_content_missing_or_changed", 409);
        }
        var values = new List<(string Name, object? Value)>
        {
            ("stream", Profile.Id),
            ("scope", Profile.Scope.Key),
            ("revision", content.Revision),
            ("hash", content.ContentHash),
            ("operation", operationId),
        };
        string sql;
        if (expected is null)
        {
            sql = """
                INSERT INTO agentcmd_publication_pointer(stream,scope,generation,revision,content_hash,operation_id)
                VALUES(@stream,@scope,1,@revision,@hash,@operation)
                ON CONFLICT(stream,scope) DO NOTHING RETURNING generation
                """;
        }
        else
        {
            values.AddRange([
                ("expectedGeneration", expected.Generation),
                ("expectedRevision", expected.Revision),
                ("expectedHash", expected.ContentHash),
                ("expectedOperation", expected.OperationId),
            ]);
            sql = """
                UPDATE agentcmd_publication_pointer SET generation=generation+1,revision=@revision,content_hash=@hash,operation_id=@operation
                WHERE stream=@stream AND scope=@scope AND generation=@expectedGeneration AND revision=@expectedRevision
                  AND content_hash=@expectedHash AND operation_id=@expectedOperation RETURNING generation
                """;
        }
        long? generation;
        await using (
            var command = RelationalControlPlaneStore.Command(db, tx, sql, values.ToArray())
        )
        {
            var result = await command.ExecuteScalarAsync(cancellationToken);
            generation = result is long value ? value : null;
        }
        var pointer = await ReadPointerAsync(db, tx, cancellationToken);
        if (generation is not null)
        {
            await using var history = RelationalControlPlaneStore.Command(
                db,
                tx,
                """
                INSERT INTO agentcmd_publication_history(stream,scope,generation,revision,content_hash,operation_id)
                VALUES(@stream,@scope,@generation,@revision,@hash,@operation)
                """,
                ("stream", Profile.Id),
                ("scope", Profile.Scope.Key),
                ("generation", generation.Value),
                ("revision", content.Revision),
                ("hash", content.ContentHash),
                ("operation", operationId)
            );
            await history.ExecuteNonQueryAsync(cancellationToken);
        }
        // Commit acknowledgement can be lost. Callers must read the actual pointer, never resend blindly.
        await tx.CommitAsync(cancellationToken);
        return new(generation is not null, pointer);
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = await createConnection(credentialReference, cancellationToken);
        try
        {
            if (connection.State != ConnectionState.Closed)
            {
                throw new ManagementException("publication_connection_must_be_unopened");
            }
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task<PublicationPointer?> ReadPointerAsync(
        DbConnection db,
        DbTransaction? tx,
        CancellationToken ct
    )
    {
        await using var command = RelationalControlPlaneStore.Command(
            db,
            tx,
            "SELECT generation,revision,content_hash,operation_id FROM agentcmd_publication_pointer WHERE stream=@stream AND scope=@scope",
            ("stream", Profile.Id),
            ("scope", Profile.Scope.Key)
        );
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))
            : null;
    }

    private async Task<PublicationContent?> ReadContentAsync(
        DbConnection db,
        DbTransaction? tx,
        string revision,
        CancellationToken ct
    )
    {
        await using var command = RelationalControlPlaneStore.Command(
            db,
            tx,
            "SELECT content_hash,body FROM agentcmd_publication_content WHERE stream=@stream AND scope=@scope AND revision=@revision",
            ("stream", Profile.Id),
            ("scope", Profile.Scope.Key),
            ("revision", revision)
        );
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        var content = new PublicationContent(revision, reader.GetString(0), reader.GetString(1));
        PublicationCodec.Verify(content);
        return content;
    }
}
