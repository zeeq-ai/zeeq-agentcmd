using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Zeeq.AgentCmd;

// WHY a store SPI at all: the authoritative record of "which exact output fills which slot" must not be derived from
// traces, baggage or RunId. It is explicit state with explicit transitions, so a persistent implementation (SQL, etc.)
// can replace the in-memory one without touching the engine.
//
// WHY every operation is a single atomic transition and not read-modify-write for the caller: a persistent store maps
// each one to a conditional update. The engine never holds a stale DraftRecord and writes it back.

/// <summary>Lifecycle of one composition slot. Attached is terminal.</summary>
public enum SlotStatus
{
    /// <summary>No work has claimed the slot.</summary>
    Empty,
    /// <summary>A step owns the slot; spend may be indeterminate.</summary>
    Claimed,
    /// <summary>An exact output version fills the slot.</summary>
    Attached,
    /// <summary>The claimed step failed and may be retried.</summary>
    Failed,
    /// <summary>The claimed step was cancelled and may be retried.</summary>
    Cancelled
}
/// <summary>Publication state of a composition draft.</summary>
public enum DraftStatus
{
    /// <summary>The draft can accept slot transitions.</summary>
    Open,
    /// <summary>The aggregate has been published and is immutable.</summary>
    Sealed
}

/// <summary>Idempotency identity for a composition draft.</summary>
/// <param name="Tenant">Tenant partition preventing cross-tenant draft lookup.</param>
/// <param name="RunId">Owning run.</param>
/// <param name="DefinitionId">Composition definition key.</param>
/// <param name="InputRevision">Caller-assigned input revision.</param>
/// <param name="Key">Caller-assigned idempotency key.</param>
/// <param name="TrialId">Optional trial identifier.</param>
public sealed record DraftKey(string Tenant, Guid RunId, string DefinitionId, string InputRevision, string Key, string? TrialId);

/// <summary>Immutable snapshot of one slot and its claimed or attached work.</summary>
/// <param name="SlotId">Definition slot key.</param>
/// <param name="Status">Current slot state.</param>
/// <param name="Attempts">Number of claims made.</param>
/// <param name="StepId">Current or last step identifier.</param>
/// <param name="Output">Exact attached output version, if any.</param>
/// <param name="ExecutionId">Reserved command execution identifier, if any.</param>
/// <param name="Provenance">Whether the output was executed or reused.</param>
/// <param name="Code">Failure or transition code, if any.</param>
/// <param name="Fingerprint">Command and input identity used to detect incompatible replay.</param>
public sealed record SlotRecord(string SlotId, SlotStatus Status, int Attempts = 0, Guid? StepId = null,
    OutputRef? Output = null, Guid? ExecutionId = null, MemberProvenance? Provenance = null, string? Code = null,
    string? Fingerprint = null);

/// <summary>Append-only transition event committed atomically with the draft state.</summary>
/// <param name="Sequence">Monotonic sequence within the draft outbox.</param>
/// <param name="Kind">Transition or attempt event kind.</param>
/// <param name="DraftId">Owning draft.</param>
/// <param name="SlotId">Related slot, if any.</param>
/// <param name="StepId">Related step, if any.</param>
/// <param name="ExecutionId">Actual execution, if any.</param>
/// <param name="Detail">Optional event detail.</param>
/// <param name="At">Time the event was recorded.</param>
public sealed record CompositionOutboxEvent(long Sequence, string Kind, Guid DraftId, string? SlotId, Guid? StepId,
    Guid? ExecutionId, string? Detail, DateTimeOffset At);

/// <summary>Published aggregate and the exact member outputs from which it was assembled.</summary>
/// <param name="Reference">Exact aggregate output reference.</param>
/// <param name="Json">Stored aggregate JSON.</param>
/// <param name="Members">Exact component versions and provenance.</param>
/// <param name="ProjectionPolicy">How the JSON was assembled; see <see cref="AggregateProjection"/>.</param>
/// <param name="SealedAt">Time the aggregate was sealed.</param>
public sealed record AggregateArtifact(AggregateRef Reference, string Json, IReadOnlyList<CompositeMember> Members,
    string ProjectionPolicy, DateTimeOffset SealedAt);

/// <summary>Identifiers for aggregate JSON construction policies.</summary>
public static class AggregateProjection
{
    /// <summary>
    /// The aggregate JSON is a JsonObject of the exact stored component wire JSON, keyed by slot JsonName.
    /// WHY not re-serialize the typed whole: a naming policy or WhenWritingNull on the whole could silently drop a required
    /// nullable member, and the stored aggregate would then disagree with the component artifacts it claims to be made of.
    /// The typed whole is only deserialized from this JSON to validate it.
    /// </summary>
    public const string ExactComponentWire = "component-wire-v1";
}

/// <summary>
/// Retained experiment publication or explicit default and host assignment for one draft.
/// </summary>
/// <remarks>
/// The factory detaches source-owned collections and JSON before a pin can enter a store.
/// Assignment is host supplied and is neither authorization nor evidence of actor identity.
/// </remarks>
public sealed class ExperimentDraftPin
{
    private ExperimentDraftPin(ExperimentSnapshot? snapshot, ExperimentFallbackReason? defaultReason,
        string partition, string? unitId, string? assignmentId, IReadOnlyDictionary<string, string>? assignments, string? defaultConfigurationFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        Snapshot = snapshot is null ? null : CopySnapshot(snapshot);
        DefaultReason = defaultReason;
        DefaultConfigurationFingerprint = defaultConfigurationFingerprint;
        Partition = partition;
        UnitId = unitId;
        AssignmentId = assignmentId;
        Assignments = (assignments ?? ImmutableDictionary<string, string>.Empty)
            .ToImmutableDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (Assignments.Any(pair => pair.Key is null || pair.Value is null))
        {
            throw new ArgumentException("Assignment keys and values are required.", nameof(assignments));
        }
        Fingerprint = ComputeFingerprint();
    }

    /// <summary>Gets the retained complete publication, or null for a configured default.</summary>
    public ExperimentSnapshot? Snapshot
    {
        get;
    }
    /// <summary>Gets the reason for selecting the configured default, or null for a publication.</summary>
    public ExperimentFallbackReason? DefaultReason
    {
        get;
    }
    /// <summary>Gets the host fingerprint of the complete configured default, or null for a publication.</summary>
    /// <remarks>The host must retain or verify this exact default identity before executing a replay.</remarks>
    public string? DefaultConfigurationFingerprint
    {
        get;
    }
    /// <summary>Gets the host-authorized partition at selection time.</summary>
    public string Partition
    {
        get;
    }
    /// <summary>Gets the host-supplied assignment unit, when supplied.</summary>
    public string? UnitId
    {
        get;
    }
    /// <summary>Gets a host-supplied assignment identity, when supplied.</summary>
    public string? AssignmentId
    {
        get;
    }
    /// <summary>Gets host-selected command or experiment assignments by ordinal key.</summary>
    public IReadOnlyDictionary<string, string> Assignments
    {
        get;
    }
    /// <summary>Gets the SHA-256 fingerprint of the entire retained pin and assignment context.</summary>
    public string Fingerprint
    {
        get;
    }

    /// <summary>Freezes a complete source publication and its host-selected assignments.</summary>
    /// <param name="snapshot">Complete publication selected before any part runs.</param>
    /// <param name="partition">Host-authorized source partition.</param>
    /// <param name="unitId">Host-supplied assignment unit, if any.</param>
    /// <param name="assignmentId">Host-supplied assignment identity, if any.</param>
    /// <param name="assignments">Host-selected command or experiment assignments, if any.</param>
    /// <returns>A detached immutable pin.</returns>
    public static ExperimentDraftPin ForSnapshot(ExperimentSnapshot snapshot, string partition,
        string? unitId = null, string? assignmentId = null, IReadOnlyDictionary<string, string>? assignments = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(snapshot, null, partition, unitId, assignmentId, assignments, null);
    }

    /// <summary>Records that the draft selected the complete configured default.</summary>
    /// <param name="reason">Sanitized reason for selecting the default.</param>
    /// <param name="partition">Host-authorized source partition.</param>
    /// <param name="defaultConfigurationFingerprint">Host identity of the complete configured default, retained and checked on replay.</param>
    /// <param name="unitId">Host-supplied assignment unit, if any.</param>
    /// <param name="assignmentId">Host-supplied assignment identity, if any.</param>
    /// <param name="assignments">Host-selected command or experiment assignments, if any.</param>
    /// <returns>An immutable default pin.</returns>
    public static ExperimentDraftPin ForDefault(ExperimentFallbackReason reason, string partition,
        string defaultConfigurationFingerprint, string? unitId = null, string? assignmentId = null,
        IReadOnlyDictionary<string, string>? assignments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultConfigurationFingerprint);
        return new(null, reason, partition, unitId, assignmentId, assignments, defaultConfigurationFingerprint);
    }

    private static ExperimentSnapshot CopySnapshot(ExperimentSnapshot snapshot)
    {
        var commands = snapshot.Commands.ToImmutableDictionary(pair => pair.Key, pair =>
        {
            var view = pair.Value;
            var variants = view.Variants.ToImmutableDictionary(v => v.Key, v => v.Value with
            {
                ParameterValues = v.Value.ParameterValues.ToImmutableDictionary(
                    p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal)
            }, StringComparer.Ordinal);
            return view with
            {
                Variants = variants
            };
        }, StringComparer.Ordinal);
        return new(snapshot.Revision, commands);
    }

    // BinaryWriter's length-prefixed UTF-8 preserves field boundaries and explicit nulls.
    // Ordering every dictionary makes the identity independent of source enumeration order.
    private string ComputeFingerprint()
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            void Write(string? value)
            {
                writer.Write(value is not null);
                if (value is not null)
                {
                    writer.Write(value);
                }
            }
            void Change(TemplateChange change)
            {
                switch (change)
                {
                    case TemplateChange.Inherited:
                        writer.Write((byte)0);
                        break;
                    case TemplateChange.Removed:
                        writer.Write((byte)1);
                        break;
                    case TemplateChange.Replaced text:
                        writer.Write((byte)2);
                        Write(text.Text);
                        break;
                    default:
                        throw new ArgumentException("Unknown template change.");
                }
            }
            void Route(ExecutionProfile? route)
            {
                writer.Write(route is not null);
                if (route is null)
                {
                    return;
                }
                Write(route.Key.Id);
                Write(route.Provider);
                Write(route.Model);
                Write(route.Temperature is float temperature ? BitConverter.SingleToInt32Bits(temperature).ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
                writer.Write(route.Retry is not null);
                if (route.Retry is { } retry)
                {
                    writer.Write(retry.MaxRetries);
                    Write(retry.InitialDelay?.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    writer.Write(retry.UseJitter);
                }
                Write(route.Fallback?.Id);
                Write(route.AttemptTimeout?.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
                writer.Write(route.Defaults is not null);
                if (route.Defaults is { } defaults)
                {
                    Write(defaults.Temperature is float t ? BitConverter.SingleToInt32Bits(t).ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
                    Write(defaults.MaxOutputTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    Write(defaults.ReasoningEffort?.ToString());
                    Write(defaults.TopP is float p ? BitConverter.SingleToInt32Bits(p).ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
                    Write(defaults.TopK?.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    Write(defaults.ReasoningOutput?.ToString());
                }
            }
            Write("composition-experiment-pin-v1");
            Write(Partition);
            Write(UnitId);
            Write(AssignmentId);
            writer.Write(Assignments.Count);
            foreach (var pair in Assignments.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                Write(pair.Key);
                Write(pair.Value);
            }
            writer.Write(Snapshot is not null);
            if (Snapshot is { } snapshot)
            {
                Write(snapshot.Revision.Value);
                writer.Write(snapshot.Commands.Count);
                foreach (var pair in snapshot.Commands.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    var view = pair.Value;
                    Write(pair.Key);
                    Write(view.CommandId);
                    writer.Write(view.CommandRevision);
                    Write(view.ContractSignature);
                    Write(view.Revision.Value);
                    Write(view.ExperimentId);
                    Write(view.ControlVariantId);
                    writer.Write(view.Variants.Count);
                    foreach (var item in view.Variants.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        var variant = item.Value;
                        Write(item.Key);
                        Write(variant.VariantId);
                        writer.Write(variant.Weight);
                        Change(variant.Prompt.System);
                        Change(variant.Prompt.User);
                        writer.Write(variant.ParameterValues.Count);
                        foreach (var parameter in variant.ParameterValues.OrderBy(p => p.Key, StringComparer.Ordinal))
                        {
                            Write(parameter.Key);
                            Write(parameter.Value.GetRawText());
                        }
                        Route(variant.Route);
                    }
                }
            }
            else
            {
                writer.Write((int)DefaultReason!.Value);
                Write(DefaultConfigurationFingerprint);
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }
}

/// <summary>Result of atomically pinning a draft's experiment publication.</summary>
public closed record ExperimentPinResult
{
    /// <summary>This call installed the pin.</summary>
    /// <param name="Pin">The stored authoritative pin.</param>
    public sealed record Applied(ExperimentDraftPin Pin) : ExperimentPinResult;
    /// <summary>Another call already installed the authoritative pin.</summary>
    /// <param name="Pin">The existing authoritative pin.</param>
    public sealed record Existing(ExperimentDraftPin Pin) : ExperimentPinResult;
    /// <summary>The store refused the transition.</summary>
    /// <param name="Code">Bounded machine-readable reason.</param>
    public sealed record Rejected(string Code) : ExperimentPinResult;
}

/// <summary>Immutable state and outbox snapshot of a composition draft.</summary>
/// <param name="DraftId">Stable draft identifier.</param>
/// <param name="Key">Idempotency identity.</param>
/// <param name="Revision">Definition revision.</param>
/// <param name="DefinitionHash">Hash of the definition.</param>
/// <param name="Generation">Draft generation.</param>
/// <param name="Status">Publication state.</param>
/// <param name="Version">State version.</param>
/// <param name="Slots">Slot state by slot key.</param>
/// <param name="Aggregate">Sealed aggregate, if any.</param>
/// <param name="Events">Append-only transition events.</param>
/// <param name="ExperimentPin">Retained publication or default selection, when pinned.</param>
public sealed record DraftRecord(Guid DraftId, DraftKey Key, int Revision, string DefinitionHash, int Generation,
    DraftStatus Status, long Version, IReadOnlyDictionary<string, SlotRecord> Slots,
    AggregateArtifact? Aggregate, IReadOnlyList<CompositionOutboxEvent> Events, ExperimentDraftPin? ExperimentPin = null);

/// <summary>Request to open or replay a draft.</summary>
/// <param name="Key">Idempotency identity.</param>
/// <param name="Revision">Definition revision.</param>
/// <param name="DefinitionHash">Definition hash.</param>
/// <param name="SlotIds">Slots initialized for a new draft.</param>
public sealed record OpenDraft(DraftKey Key, int Revision, string DefinitionHash, IReadOnlyList<string> SlotIds);
/// <summary>Request to atomically reserve a slot for a step.</summary>
/// <param name="DraftId">Owning draft.</param>
/// <param name="SlotId">Slot to claim.</param>
/// <param name="StepId">Claim owner used to fence later transitions.</param>
/// <param name="Provenance">Whether the work executes or reuses output.</param>
/// <param name="ExecutionId">Execution reserved before the provider call, allowing reconciliation after a crash.</param>
/// <param name="Fingerprint">Command + parameter identity. A later replay with another fingerprint is a conflict, not a silent reuse.</param>
public sealed record ClaimSlot(Guid DraftId, string SlotId, Guid StepId, MemberProvenance Provenance, Guid? ExecutionId = null, string? Fingerprint = null);
/// <summary>Journal entry for a claimed attempt, fenced by the claiming step.</summary>
/// <param name="DraftId">Owning draft.</param>
/// <param name="SlotId">Claimed slot.</param>
/// <param name="StepId">Claim owner.</param>
/// <param name="Kind">Attempt event kind.</param>
/// <param name="ExecutionId">Actual execution, if any.</param>
/// <param name="Detail">Optional event detail.</param>
public sealed record AppendEvent(Guid DraftId, string SlotId, Guid StepId, string Kind, Guid? ExecutionId, string? Detail);
/// <summary>Request to attach an exact output to a claimed slot.</summary>
/// <param name="DraftId">Owning draft.</param>
/// <param name="SlotId">Claimed slot.</param>
/// <param name="StepId">Claim owner.</param>
/// <param name="Output">Exact output version to attach.</param>
/// <param name="ExecutionId">Execution that produced the output, if any.</param>
public sealed record AttachSlot(Guid DraftId, string SlotId, Guid StepId, OutputRef Output, Guid? ExecutionId);
/// <summary>Request to resolve a claim as failed or cancelled.</summary>
/// <param name="DraftId">Owning draft.</param>
/// <param name="SlotId">Claimed slot.</param>
/// <param name="StepId">Claim owner.</param>
/// <param name="To">Failure or cancellation state.</param>
/// <param name="Code">Reason code.</param>
/// <param name="ExecutionId">Reserved execution, if any.</param>
public sealed record ReleaseSlot(Guid DraftId, string SlotId, Guid StepId, SlotStatus To, string Code, Guid? ExecutionId = null);
/// <summary>Request to atomically publish an aggregate.</summary>
/// <param name="DraftId">Draft to seal.</param>
/// <param name="Aggregate">Aggregate and exact members to publish.</param>
public sealed record SealDraft(Guid DraftId, AggregateArtifact Aggregate);

/// <summary>Result of opening or replaying a draft.</summary>
public closed record OpenDraftResult
{
    /// <summary>A matching draft was opened.</summary>
    /// <param name="Draft">Opened draft snapshot.</param>
    /// <param name="Created">Whether this call created the draft.</param>
    public sealed record Opened(DraftRecord Draft, bool Created) : OpenDraftResult;
    /// <summary>Same key, different definition revision or hash. Mixing parts across definitions is refused.</summary>
    /// <param name="Existing">Draft already stored under the same key.</param>
    public sealed record DefinitionMismatch(DraftRecord Existing) : OpenDraftResult;
}

/// <summary>Result of attempting to claim a slot.</summary>
public closed record ClaimResult
{
    /// <summary>The slot was reserved by this step.</summary>
    /// <param name="Slot">Claimed slot snapshot.</param>
    public sealed record Claimed(SlotRecord Slot) : ClaimResult;
    /// <summary>The slot already contains an output.</summary>
    /// <param name="Slot">Attached slot snapshot.</param>
    public sealed record AlreadyAttached(SlotRecord Slot) : ClaimResult;
    /// <summary>Someone claimed and never resolved. Spend is unknown, so the caller must not execute again.</summary>
    /// <param name="Slot">Unresolved claim snapshot.</param>
    public sealed record InFlight(SlotRecord Slot) : ClaimResult;
    /// <summary>The draft is already sealed and cannot accept a claim.</summary>
    public sealed record DraftSealed : ClaimResult;
    /// <summary>The requested operation was rejected.</summary>
    /// <param name="Code">Machine-readable rejection reason.</param>
    public sealed record Rejected(string Code) : ClaimResult;
}

/// <summary>Result of a fenced slot transition.</summary>
public closed record TransitionResult
{
    /// <summary>The requested transition was applied.</summary>
    /// <param name="Slot">Updated slot snapshot.</param>
    public sealed record Applied(SlotRecord Slot) : TransitionResult;
    /// <summary>The requested operation was rejected.</summary>
    /// <param name="Code">Machine-readable rejection reason.</param>
    public sealed record Rejected(string Code) : TransitionResult;
}

/// <summary>Result of publishing an aggregate.</summary>
public closed record SealResult
{
    /// <summary>The aggregate was sealed.</summary>
    /// <param name="Draft">Sealed draft snapshot.</param>
    public sealed record Sealed(DraftRecord Draft) : SealResult;
    /// <summary>Idempotent seal: another caller won. The stored aggregate is authoritative.</summary>
    /// <param name="Draft">Authoritative sealed draft snapshot.</param>
    public sealed record AlreadySealed(DraftRecord Draft) : SealResult;
    /// <summary>The requested operation was rejected.</summary>
    /// <param name="Code">Machine-readable rejection reason.</param>
    public sealed record Rejected(string Code) : SealResult;
}

/// <summary>
/// Slot state machine: Empty|Failed|Cancelled -> Claimed -> Attached | Failed | Cancelled. Attached is terminal.
/// A Claimed slot with no resolution means indeterminate spend ("Pending"); there are no leases, so only an operator
/// (or a future lease/fencing layer) can release it.
/// </summary>
public interface ICompositionStore
{
    /// <summary>Idempotent on <see cref="DraftKey"/>. One sealed generation per draft.</summary>
    ValueTask<OpenDraftResult> OpenDraftAsync(OpenDraft request, CancellationToken ct);
    /// <summary>Reads the current immutable draft snapshot, or null if absent.</summary>
    ValueTask<DraftRecord?> GetDraftAsync(Guid draftId, CancellationToken ct);
    /// <summary>Atomically installs the first experiment pin, or returns the existing authoritative pin.</summary>
    /// <param name="draftId">Draft to pin before any slot claim.</param>
    /// <param name="candidate">Detached candidate publication or configured default.</param>
    /// <param name="ct">Caller cancellation.</param>
    /// <returns>The applied or existing pin, or a rejection.</returns>
    ValueTask<ExperimentPinResult> PinExperimentAsync(Guid draftId, ExperimentDraftPin candidate, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ExperimentPinResult>(new ExperimentPinResult.Rejected("experiment_pin_unsupported"));
    }
    /// <summary>Atomically reserves a slot or reports its existing state.</summary>
    ValueTask<ClaimResult> ClaimSlotAsync(ClaimSlot request, CancellationToken ct);
    /// <summary>Attaches an exact output if the step still owns the claim.</summary>
    ValueTask<TransitionResult> AttachSlotAsync(AttachSlot request, CancellationToken ct);
    /// <summary>Resolves a claim as failed or cancelled if the step still owns it.</summary>
    ValueTask<TransitionResult> ReleaseSlotAsync(ReleaseSlot request, CancellationToken ct);
    /// <summary>Awaited journal write that gates a provider send. Rejected if the step no longer holds the claim.</summary>
    ValueTask<TransitionResult> AppendEventAsync(AppendEvent request, CancellationToken ct);
    /// <summary>Compare-and-set Open -> Sealed. Succeeds only if the draft's attached membership equals the aggregate's members.</summary>
    ValueTask<SealResult> SealAsync(SealDraft request, CancellationToken ct);
}

/// <summary>
/// Process-local reference implementation. Limits (deliberate, see README): no durability, no cross-process claims,
/// no leases or fencing. Copy-on-write records are returned so callers can never mutate stored state.
/// </summary>
/// <param name="clock">Optional clock for transition timestamps; defaults to system time.</param>
public sealed class InMemoryCompositionStore(TimeProvider? clock = null) : ICompositionStore
{
    sealed class State
    {
        public required Guid DraftId; public required DraftKey Key; public required int Revision; public required string Hash;
        public DraftStatus Status; public long Version; public ImmutableDictionary<string, SlotRecord> Slots = ImmutableDictionary<string, SlotRecord>.Empty;
        public AggregateArtifact? Aggregate; public ImmutableList<CompositionOutboxEvent> Events = ImmutableList<CompositionOutboxEvent>.Empty;
        public long Sequence;
        public ExperimentDraftPin? ExperimentPin;
    }
    readonly object gate = new();
    readonly TimeProvider time = clock ?? TimeProvider.System;
    readonly Dictionary<Guid, State> drafts = [];
    readonly Dictionary<DraftKey, Guid> byKey = [];

    /// <inheritdoc />
    public ValueTask<OpenDraftResult> OpenDraftAsync(OpenDraft request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (byKey.TryGetValue(request.Key, out var id))
            {
                var existing = drafts[id];
                return ValueTask.FromResult<OpenDraftResult>(existing.Revision == request.Revision && existing.Hash == request.DefinitionHash
                    ? new OpenDraftResult.Opened(Snapshot(existing), false) : new OpenDraftResult.DefinitionMismatch(Snapshot(existing)));
            }
            var state = new State { DraftId = Guid.NewGuid(), Key = request.Key, Revision = request.Revision, Hash = request.DefinitionHash };
            state.Slots = request.SlotIds.ToImmutableDictionary(s => s, s => new SlotRecord(s, SlotStatus.Empty), StringComparer.Ordinal);
            drafts[state.DraftId] = state;
            byKey[request.Key] = state.DraftId;
            Log(state, "draft.opened", null, null, null, null);
            return ValueTask.FromResult<OpenDraftResult>(new OpenDraftResult.Opened(Snapshot(state), true));
        }
    }

    /// <inheritdoc />
    public ValueTask<DraftRecord?> GetDraftAsync(Guid draftId, CancellationToken ct)
    {
        lock (gate)
        {
            return ValueTask.FromResult(drafts.TryGetValue(draftId, out var s) ? Snapshot(s) : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<ExperimentPinResult> PinExperimentAsync(Guid draftId, ExperimentDraftPin candidate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            ct.ThrowIfCancellationRequested();
            if (!drafts.TryGetValue(draftId, out var draft))
            {
                return Done<ExperimentPinResult>(new ExperimentPinResult.Rejected("draft_missing"));
            }
            if (draft.Status == DraftStatus.Sealed)
            {
                return Done<ExperimentPinResult>(new ExperimentPinResult.Rejected("draft_sealed"));
            }
            if (draft.ExperimentPin is { } existing)
            {
                return Done<ExperimentPinResult>(new ExperimentPinResult.Existing(existing));
            }
            // A pin cannot be retrofitted after any slot has been claimed or attached.
            if (draft.Slots.Values.Any(slot => slot.Attempts != 0 || slot.Status != SlotStatus.Empty))
            {
                return Done<ExperimentPinResult>(new ExperimentPinResult.Rejected("draft_already_started"));
            }
            draft.ExperimentPin = candidate;
            Log(draft, "draft.experiment_pinned", null, null, null, candidate.Fingerprint);
            return Done<ExperimentPinResult>(new ExperimentPinResult.Applied(candidate));
        }
    }

    /// <inheritdoc />
    public ValueTask<ClaimResult> ClaimSlotAsync(ClaimSlot request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!drafts.TryGetValue(request.DraftId, out var d))
            {
                return Done<ClaimResult>(new ClaimResult.Rejected("draft_missing"));
            }

            if (!d.Slots.TryGetValue(request.SlotId, out var slot))
            {
                return Done<ClaimResult>(new ClaimResult.Rejected("slot_unknown"));
            }

            switch (slot.Status)
            {
                case SlotStatus.Attached:
                    return Done<ClaimResult>(new ClaimResult.AlreadyAttached(slot));
                case SlotStatus.Claimed:
                    return Done<ClaimResult>(new ClaimResult.InFlight(slot));
            }
            if (d.Status == DraftStatus.Sealed)
            {
                return Done<ClaimResult>(new ClaimResult.DraftSealed());
            }
            // The claim is recorded in the authoritative store BEFORE any provider call. WHY: if the process dies after spend, the slot is visibly
            // Claimed (Pending) instead of looking Empty and being paid for twice.
            var claimed = slot with
            {
                Status = SlotStatus.Claimed,
                Attempts = slot.Attempts + 1,
                StepId = request.StepId,
                Output = null,
                ExecutionId = request.ExecutionId,
                Provenance = request.Provenance,
                Code = null,
                Fingerprint = request.Fingerprint
            };
            d.Slots = d.Slots.SetItem(slot.SlotId, claimed);
            Log(d, "slot.claimed", slot.SlotId, request.StepId, request.ExecutionId, request.Provenance.ToString());
            return Done<ClaimResult>(new ClaimResult.Claimed(claimed));
        }
    }

    /// <inheritdoc />
    public ValueTask<TransitionResult> AttachSlotAsync(AttachSlot request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        // No ct.ThrowIfCancellationRequested here: the caller already paid for this output. Recording it must win over cancellation.
        lock (gate)
        {
            if (!TryClaimed(request.DraftId, request.SlotId, request.StepId, out var d, out var slot, out var error))
            {
                return Done<TransitionResult>(new TransitionResult.Rejected(error));
            }

            var attached = slot with
            {
                Status = SlotStatus.Attached,
                Output = request.Output,
                ExecutionId = request.ExecutionId ?? slot.ExecutionId,
                Code = null
            };
            d.Slots = d.Slots.SetItem(slot.SlotId, attached);
            Log(d, "slot.attached", slot.SlotId, request.StepId, request.ExecutionId, slot.Provenance?.ToString());
            return Done<TransitionResult>(new TransitionResult.Applied(attached));
        }
    }

    /// <inheritdoc />
    public ValueTask<TransitionResult> ReleaseSlotAsync(ReleaseSlot request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.To is not (SlotStatus.Failed or SlotStatus.Cancelled))
        {
            throw new ArgumentException("Release moves to Failed or Cancelled", nameof(request));
        }

        lock (gate)
        {
            if (!TryClaimed(request.DraftId, request.SlotId, request.StepId, out var d, out var slot, out var error))
            {
                return Done<TransitionResult>(new TransitionResult.Rejected(error));
            }

            var released = slot with
            {
                Status = request.To,
                Code = request.Code,
                ExecutionId = request.ExecutionId ?? slot.ExecutionId
            };
            d.Slots = d.Slots.SetItem(slot.SlotId, released);
            Log(d, request.To == SlotStatus.Failed ? "slot.failed" : "slot.cancelled", slot.SlotId, request.StepId, request.ExecutionId, request.Code);
            return Done<TransitionResult>(new TransitionResult.Applied(released));
        }
    }

    /// <inheritdoc />
    public ValueTask<TransitionResult> AppendEventAsync(AppendEvent request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!TryClaimed(request.DraftId, request.SlotId, request.StepId, out var d, out var slot, out var error))
            {
                return Done<TransitionResult>(new TransitionResult.Rejected(error));
            }

            Log(d, request.Kind, slot.SlotId, request.StepId, request.ExecutionId, request.Detail);
            return Done<TransitionResult>(new TransitionResult.Applied(slot));
        }
    }

    /// <inheritdoc />
    public ValueTask<SealResult> SealAsync(SealDraft request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            if (!drafts.TryGetValue(request.DraftId, out var d))
            {
                return Done<SealResult>(new SealResult.Rejected("draft_missing"));
            }

            if (d.Status == DraftStatus.Sealed)
            {
                return Done<SealResult>(new SealResult.AlreadySealed(Snapshot(d)));
            }
            // Membership CAS: the aggregate may only claim members that are attached right now, with exactly these versions.
            var attached = d.Slots.Values.Where(s => s.Status == SlotStatus.Attached).ToDictionary(s => s.SlotId, StringComparer.Ordinal);
            if (attached.Count != request.Aggregate.Members.Count)
            {
                return Done<SealResult>(new SealResult.Rejected("membership_changed"));
            }

            foreach (var m in request.Aggregate.Members)
            {
                if (!attached.TryGetValue(m.SlotId, out var s) || s.Output != m.Output)
                {
                    return Done<SealResult>(new SealResult.Rejected("membership_changed"));
                }
            }
            // Aggregate and manifest are one record, set in one critical section: no reader sees one without the other.
            d.Status = DraftStatus.Sealed;
            d.Aggregate = request.Aggregate;
            Log(d, "draft.sealed", null, null, null, request.Aggregate.Reference.AggregateId.ToString());
            return Done<SealResult>(new SealResult.Sealed(Snapshot(d)));
        }
    }

    bool TryClaimed(Guid draftId, string slotId, Guid stepId, out State d, out SlotRecord slot, out string error)
    {
        slot = null!;
        d = null!;
        error = "";
        if (!drafts.TryGetValue(draftId, out d!))
        {
            error = "draft_missing";
            return false;
        }
        if (!d.Slots.TryGetValue(slotId, out slot!))
        {
            error = "slot_unknown";
            return false;
        }
        // StepId is the fencing token inside one process: a stale step cannot overwrite a newer attempt.
        if (slot.Status != SlotStatus.Claimed || slot.StepId != stepId)
        {
            error = "not_claimed_by_step";
            return false;
        }
        return true;
    }

    void Log(State d, string kind, string? slotId, Guid? stepId, Guid? executionId, string? detail)
    {
        d.Version++;
        d.Events = d.Events.Add(new(++d.Sequence, kind, d.DraftId, slotId, stepId, executionId, detail, time.GetUtcNow()));
    }

    static DraftRecord Snapshot(State s) => new(s.DraftId, s.Key, s.Revision, s.Hash, 1, s.Status, s.Version, s.Slots, s.Aggregate, s.Events, s.ExperimentPin);
    static ValueTask<T> Done<T>(T value) => ValueTask.FromResult(value);
}
