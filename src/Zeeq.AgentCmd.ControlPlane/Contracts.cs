using System.Data.Common;
using System.Text.Json;

namespace Zeeq.AgentCmd.ControlPlane;

/// <summary>Explicit application, deployment and data boundary; identifiers do not grant access.</summary>
/// <param name="Application">Application namespace authorized by the host.</param>
/// <param name="Environment">Deployment environment.</param>
/// <param name="Partition">Authorized data partition.</param>
public sealed record WorkspaceScope(string Application, string Environment, string Partition)
{
    /// <summary>Unambiguous storage key; separators inside names cannot merge scopes.</summary>
    public string Key => JsonSerializer.Serialize(new[] { Application, Environment, Partition });
}

/// <summary>Persisted management artifact. Its JSON body is governed by a module's validation and freeze rules.</summary>
/// <param name="Id">Stable artifact identity.</param>
/// <param name="Kind">Owning feature module.</param>
/// <param name="Revision">Optimistic concurrency token.</param>
/// <param name="Name">Operator-facing label.</param>
/// <param name="State">Lifecycle state, separate from execution or telemetry status.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="UpdatedAt">Last committed transition time.</param>
/// <param name="ContentHash">Digest of the exact normalized stored body.</param>
/// <param name="Body">Detached immutable content for this revision.</param>
public sealed record ManagementResource(
    string Id,
    string Kind,
    long Revision,
    string Name,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ContentHash,
    JsonElement Body
);

/// <summary>Authenticated actor supplied by the host, never accepted from a mutation body.</summary>
/// <param name="Subject">Authenticated actor identifier.</param>
public sealed record ManagementActor(string Subject);

/// <summary>Closed transition failure, sanitized for API clients.</summary>
/// <param name="Code">Stable machine-readable reason.</param>
/// <param name="Status">HTTP-compatible failure category.</param>
/// <param name="Revision">Current revision when an authorized caller conflicts.</param>
public sealed class ManagementException(string Code, int Status = 400, long? Revision = null)
    : Exception(Code)
{
    /// <summary>Stable reason without sensitive database details.</summary>
    public string Code { get; } = Code;

    /// <summary>Failure category.</summary>
    public int Status { get; } = Status;

    /// <summary>Current authorized revision, if available.</summary>
    public long? Revision { get; } = Revision;
}

/// <summary>Input to an explicit draft transition; it is never an implicit publish or model launch.</summary>
/// <param name="Name">Operator label.</param>
/// <param name="Body">Module-specific content.</param>
/// <param name="Id">Optional immutable build identity during registration.</param>
public sealed record SaveResource(string Name, JsonElement Body, string? Id = null);

/// <summary>Transactional management port. Providers must preserve CAS, immutable artifacts and mutation/outbox atomicity.</summary>
public interface IControlPlaneStore
{
    /// <summary>Reported provider; it does not imply untested deployment capabilities.</summary>
    string Provider { get; }

    /// <summary>Creates or verifies the provider's local schema.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads one exact scoped resource.</summary>
    Task<ManagementResource?> ReadAsync(
        WorkspaceScope scope,
        string kind,
        string id,
        CancellationToken cancellationToken = default
    );

    /// <summary>Reads a bounded module projection within one authorized scope.</summary>
    Task<IReadOnlyList<ManagementResource>> ListAsync(
        WorkspaceScope scope,
        string kind,
        CancellationToken cancellationToken = default
    );

    /// <summary>Registers immutable build metadata, replaying only byte-equivalent content.</summary>
    Task<ManagementResource> RegisterBuildAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        SaveResource request,
        CancellationToken cancellationToken = default
    );

    /// <summary>Creates or conditionally updates a draft and its audit/outbox event in one transaction.</summary>
    Task<ManagementResource> SaveDraftAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string kind,
        SaveResource request,
        string? id = null,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>Freezes exact content with an optimistic token; frozen content is not edited in place.</summary>
    Task<ManagementResource> FreezeAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string kind,
        string id,
        long expectedRevision,
        CancellationToken cancellationToken = default
    );
}

/// <summary>Provider injection point; secrets remain inside the server-side factory.</summary>
/// <param name="Provider">Sqlite or Postgres SQL dialect.</param>
/// <param name="CreateConnection">Creates an unopened, independently owned connection.</param>
public sealed record ManagementDatabase(string Provider, Func<DbConnection> CreateConnection);

/// <summary>Scoped immutable asset identity, independent of expiring trace storage.</summary>
/// <param name="Hash">SHA-256 content identity.</param>
/// <param name="Length">Exact byte length.</param>
public sealed record StoredAsset(string Hash, long Length);

/// <summary>Content-addressed asset storage; implementations enforce scope and verify retained bytes.</summary>
public interface IControlPlaneAssetStore
{
    /// <summary>Stores bounded bytes atomically under the authorized scope.</summary>
    Task<StoredAsset> PutAsync(
        WorkspaceScope scope,
        Stream source,
        CancellationToken cancellationToken = default
    );

    /// <summary>Opens a verified exact asset; unavailable or changed bytes are an explicit failure.</summary>
    Task<Stream> OpenAsync(
        WorkspaceScope scope,
        string hash,
        CancellationToken cancellationToken = default
    );
}
