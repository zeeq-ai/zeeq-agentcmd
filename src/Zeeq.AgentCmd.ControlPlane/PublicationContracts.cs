using System.Text.Json;

namespace Zeeq.AgentCmd.ControlPlane;

/// <summary>Exact current-pointer token. Generation prevents an ABA change from satisfying an old review.</summary>
/// <param name="Generation">Monotonically increasing target generation.</param>
/// <param name="Revision">Immutable runtime publication revision.</param>
/// <param name="ContentHash">Digest of the complete normalized publication.</param>
/// <param name="OperationId">Reviewed operation that activated this generation.</param>
public sealed record PublicationPointer(
    long Generation,
    string Revision,
    string ContentHash,
    string OperationId
);

/// <summary>Complete content stored before activation; existing revisions can never change meaning.</summary>
/// <param name="Revision">Immutable runtime publication revision.</param>
/// <param name="ContentHash">SHA-256 of Json.</param>
/// <param name="Json">Normalized complete partition content, never a patch.</param>
public sealed record PublicationContent(string Revision, string ContentHash, string Json);

/// <summary>Server-owned binding metadata. No connection string or credential reference is exposed.</summary>
/// <param name="Id">Target identity in one workspace.</param>
/// <param name="Scope">Authorized workspace.</param>
/// <param name="BindingId">Host runtime binding identity.</param>
/// <param name="BindingRevision">Exact deployed binding configuration revision.</param>
/// <param name="BuildId">Build accepted by this binding.</param>
/// <param name="AllowManualDecisions">Explicit policy permitting an authored decision over pinned evidence.</param>
public sealed record PublicationTargetProfile(
    string Id,
    WorkspaceScope Scope,
    string BindingId,
    string BindingRevision,
    string BuildId,
    bool AllowManualDecisions = false
);

/// <summary>Truthful adapter capability, independent of the management database provider.</summary>
/// <param name="Provider">Adapter name.</param>
/// <param name="ImmutableHistory">Supports exact historical revision reads.</param>
/// <param name="CompareExchange">Implements pointer CAS.</param>
/// <param name="Qualified">Passed an explicit real-provider contract suite.</param>
public sealed record PublicationTargetCapabilities(
    string Provider,
    bool ImmutableHistory,
    bool CompareExchange,
    bool Qualified
);

/// <summary>CAS result; a transport exception has an unknown outcome and must be reconciled.</summary>
/// <param name="Applied">Whether this operation activated the pointer.</param>
/// <param name="Current">Pointer observed in the target transaction.</param>
public sealed record PublicationActivation(bool Applied, PublicationPointer? Current);

/// <summary>Optional singleton-safe target port. Constructors must not connect or initialize external storage.</summary>
public interface IPublicationTarget
{
    /// <summary>Server-owned binding and policy.</summary>
    PublicationTargetProfile Profile { get; }

    /// <summary>Implemented and qualified capabilities.</summary>
    PublicationTargetCapabilities Capabilities { get; }

    /// <summary>Reads the actual target pointer; absence is distinct from a failed read.</summary>
    Task<PublicationPointer?> ReadCurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads exact immutable history without consulting the current pointer.</summary>
    Task<PublicationContent?> ReadRevisionAsync(
        string revision,
        CancellationToken cancellationToken = default
    );

    /// <summary>Creates content once, or verifies that the existing revision is byte-equivalent.</summary>
    Task PutAsync(PublicationContent content, CancellationToken cancellationToken = default);

    /// <summary>Activates only against the reviewed token after verifying complete stored content.</summary>
    Task<PublicationActivation> ActivateAsync(
        PublicationPointer? expected,
        PublicationContent content,
        string operationId,
        CancellationToken cancellationToken = default
    );
}

/// <summary>Explicit server-only target registration; an empty registry has no writer or external connection.</summary>
public sealed class PublicationTargetRegistry
{
    private readonly IPublicationTarget[] targets;

    /// <summary>Captures explicitly configured targets without initializing them.</summary>
    public PublicationTargetRegistry(IEnumerable<IPublicationTarget> targets)
    {
        this.targets = targets.ToArray();
        if (this.targets.GroupBy(t => (t.Profile.Scope.Key, t.Profile.Id)).Any(g => g.Count() != 1))
        {
            throw new ManagementException("publication_target_duplicate");
        }
    }

    /// <summary>Returns only targets in the authorized workspace.</summary>
    public IReadOnlyList<IPublicationTarget> ForScope(WorkspaceScope scope) =>
        targets.Where(t => t.Profile.Scope == scope).ToArray();

    /// <summary>Resolves one configured target without falling back to another scope or provider.</summary>
    public IPublicationTarget Resolve(WorkspaceScope scope, string id) =>
        targets.SingleOrDefault(t => t.Profile.Scope == scope && t.Profile.Id == id)
        ?? throw new ManagementException("publication_target_unconfigured", 409);
}

/// <summary>Manual decision input. The server supplies the author and validates linked evidence.</summary>
/// <param name="TargetId">Server target whose manual-decision policy applies.</param>
/// <param name="PublicationId">Frozen complete publication.</param>
/// <param name="PublicationRevision">Exact management revision.</param>
/// <param name="PublicationHash">Exact publication content hash.</param>
/// <param name="BuildId">Registered target build.</param>
/// <param name="Action">Activate, ChangeAllocation, ChangeControl, Rollback, or RestoreConfiguredDefaults.</param>
/// <param name="Verdict">Manual Pass, Fail, or Inconclusive; never an automated test claim.</param>
/// <param name="ComparisonId">Immutable comparison with pinned benchmark and candidate evidence.</param>
/// <param name="CandidateId">Candidate within the named comparison.</param>
/// <param name="Rationale">Required human explanation.</param>
/// <param name="EvidencePublicationId">For restore-defaults only, the old publication investigated by the report.</param>
public sealed record PublicationDecisionRequest(
    string TargetId,
    string PublicationId,
    long PublicationRevision,
    string PublicationHash,
    string BuildId,
    string Action,
    string Verdict,
    string ComparisonId,
    string CandidateId,
    string Rationale,
    string? EvidencePublicationId = null
);

/// <summary>Explicit review confirmation; reads the actual target before saving a delivery intent.</summary>
/// <param name="DecisionId">Immutable manually reviewed decision.</param>
/// <param name="DecisionHash">Exact decision content identity.</param>
/// <param name="WholePartitionReplacementReviewed">Acknowledges that omitted commands use configured defaults.</param>
public sealed record PublicationReviewRequest(
    string DecisionId,
    string DecisionHash,
    bool WholePartitionReplacementReviewed
);

/// <summary>Durable delivery pins. Progress changes never retarget the publication or expected pointer.</summary>
/// <param name="Profile">Exact server binding reviewed before activation.</param>
/// <param name="DecisionId">Saved decision identity.</param>
/// <param name="DecisionHash">Saved decision content identity.</param>
/// <param name="PublicationId">Frozen management publication.</param>
/// <param name="PublicationRevision">Frozen management revision.</param>
/// <param name="Action">Explicit reviewed action.</param>
/// <param name="Content">Exact complete runtime content.</param>
/// <param name="Expected">Actual pointer observed at review time.</param>
/// <param name="Observed">Most recent reconciliation observation, not a new precondition.</param>
public sealed record PublicationOperation(
    PublicationTargetProfile Profile,
    string DecisionId,
    string DecisionHash,
    string PublicationId,
    long PublicationRevision,
    string Action,
    PublicationContent Content,
    PublicationPointer? Expected,
    PublicationPointer? Observed = null
);

/// <summary>Host-reported adoption. Authenticated subject supplies host identity; these reports are not deployment proof.</summary>
/// <param name="TargetId">Server target identity.</param>
/// <param name="BuildId">Exact registered build running on the reporting host.</param>
/// <param name="BindingId">Exact runtime source binding.</param>
/// <param name="BindingRevision">Exact binding revision.</param>
/// <param name="PublicationRevision">Observed publication revision, or null when unknown.</param>
/// <param name="PublicationHash">Observed content hash, or null with unknown revision.</param>
/// <param name="Sequence">Monotonic per authenticated host and target; persist across restarts.</param>
public sealed record PublicationHostReport(
    string TargetId,
    string BuildId,
    string BindingId,
    string BindingRevision,
    string? PublicationRevision,
    string? PublicationHash,
    long Sequence
);

/// <summary>Per-host freshness and exact adoption evidence.</summary>
/// <param name="HostId">Authenticated subject.</param>
/// <param name="State">Reported, OtherPublication, or Unknown.</param>
/// <param name="ReceivedAt">Server receipt time.</param>
/// <param name="Report">Exact last accepted report.</param>
public sealed record PublicationHostStatus(
    string HostId,
    string State,
    DateTimeOffset ReceivedAt,
    PublicationHostReport Report
);

/// <summary>Bounded reporting-host view; it never claims fleet-wide adoption without an inventory.</summary>
/// <param name="State">Unknown without fresh matching reports; otherwise ReportingHostsOnly.</param>
/// <param name="Hosts">Known reports, including stale and mismatched hosts.</param>
public sealed record PublicationAdoption(string State, IReadOnlyList<PublicationHostStatus> Hosts);
