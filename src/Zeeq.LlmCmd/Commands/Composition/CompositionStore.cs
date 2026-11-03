using System.Collections.Immutable;
namespace Zeeq.LlmCmd;

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
public sealed record DraftRecord(Guid DraftId, DraftKey Key, int Revision, string DefinitionHash, int Generation,
    DraftStatus Status, long Version, IReadOnlyDictionary<string, SlotRecord> Slots,
    AggregateArtifact? Aggregate, IReadOnlyList<CompositionOutboxEvent> Events);

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

    static DraftRecord Snapshot(State s) => new(s.DraftId, s.Key, s.Revision, s.Hash, 1, s.Status, s.Version, s.Slots, s.Aggregate, s.Events);
    static ValueTask<T> Done<T>(T value) => ValueTask.FromResult(value);
}
