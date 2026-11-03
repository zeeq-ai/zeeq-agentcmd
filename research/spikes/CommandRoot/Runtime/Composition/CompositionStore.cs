using System.Collections.Immutable;
using HumanGrading;
namespace CommandRoot;

// WHY a store SPI at all: the authoritative record of "which exact output fills which slot" must not be derived from
// traces, baggage or RunId. It is explicit state with explicit transitions, so a persistent implementation (SQL, etc.)
// can replace the in-memory one without touching the engine.
//
// WHY every operation is a single atomic transition and not read-modify-write for the caller: a persistent store maps
// each one to a conditional update. The engine never holds a stale DraftRecord and writes it back.

public enum SlotStatus { Empty, Claimed, Attached, Failed, Cancelled }
public enum DraftStatus { Open, Sealed }

/// <param name="Tenant">Part of the identity so one tenant cannot open or read another tenant's draft by guessing a RunId.</param>
public sealed record DraftKey(string Tenant, Guid RunId, string DefinitionId, string InputRevision, string Key, string? TrialId);

public sealed record SlotRecord(string SlotId, SlotStatus Status, int Attempts = 0, Guid? StepId = null,
    OutputRef? Output = null, Guid? ExecutionId = null, MemberProvenance? Provenance = null, string? Code = null,
    string? Fingerprint = null);

/// <summary>Append-only transition log, written inside the same atomic transition as the state change (a transactional outbox).</summary>
public sealed record CompositionOutboxEvent(long Sequence, string Kind, Guid DraftId, string? SlotId, Guid? StepId,
    Guid? ExecutionId, string? Detail, DateTimeOffset At);

/// <param name="ProjectionPolicy">How Json was produced. See <see cref="AggregateProjection"/>.</param>
public sealed record AggregateArtifact(AggregateRef Reference, string Json, IReadOnlyList<CompositeMember> Members,
    string ProjectionPolicy, DateTimeOffset SealedAt);

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

public sealed record DraftRecord(Guid DraftId, DraftKey Key, int Revision, string DefinitionHash, int Generation,
    DraftStatus Status, long Version, IReadOnlyDictionary<string, SlotRecord> Slots,
    AggregateArtifact? Aggregate, IReadOnlyList<CompositionOutboxEvent> Events);

public sealed record OpenDraft(DraftKey Key, int Revision, string DefinitionHash, IReadOnlyList<string> SlotIds);
/// <param name="ExecutionId">Reserved BEFORE the provider call. If the process dies after spend, the Pending slot still names the execution to look up.</param>
/// <param name="Fingerprint">Command + parameter identity. A later replay with another fingerprint is a conflict, not a silent reuse.</param>
public sealed record ClaimSlot(Guid DraftId, string SlotId, Guid StepId, MemberProvenance Provenance, Guid? ExecutionId = null, string? Fingerprint = null);
/// <summary>Journal entry for a claimed attempt (attempt.started / attempt.finished). Fenced by StepId like attach and release.</summary>
public sealed record AppendEvent(Guid DraftId, string SlotId, Guid StepId, string Kind, Guid? ExecutionId, string? Detail);
public sealed record AttachSlot(Guid DraftId, string SlotId, Guid StepId, OutputRef Output, Guid? ExecutionId);
public sealed record ReleaseSlot(Guid DraftId, string SlotId, Guid StepId, SlotStatus To, string Code, Guid? ExecutionId = null);
public sealed record SealDraft(Guid DraftId, AggregateArtifact Aggregate);

public closed record OpenDraftResult
{
    public sealed record Opened(DraftRecord Draft, bool Created) : OpenDraftResult;
    /// <summary>Same key, different definition revision or hash. Mixing parts across definitions is refused.</summary>
    public sealed record DefinitionMismatch(DraftRecord Existing) : OpenDraftResult;
}

public closed record ClaimResult
{
    public sealed record Claimed(SlotRecord Slot) : ClaimResult;
    public sealed record AlreadyAttached(SlotRecord Slot) : ClaimResult;
    /// <summary>Someone claimed and never resolved. Spend is unknown, so the caller must not execute again.</summary>
    public sealed record InFlight(SlotRecord Slot) : ClaimResult;
    public sealed record DraftSealed : ClaimResult;
    public sealed record Rejected(string Code) : ClaimResult;
}

public closed record TransitionResult
{
    public sealed record Applied(SlotRecord Slot) : TransitionResult;
    public sealed record Rejected(string Code) : TransitionResult;
}

public closed record SealResult
{
    public sealed record Sealed(DraftRecord Draft) : SealResult;
    /// <summary>Idempotent seal: another caller won. The stored aggregate is authoritative.</summary>
    public sealed record AlreadySealed(DraftRecord Draft) : SealResult;
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
    ValueTask<DraftRecord?> GetDraftAsync(Guid draftId, CancellationToken ct);
    ValueTask<ClaimResult> ClaimSlotAsync(ClaimSlot request, CancellationToken ct);
    ValueTask<TransitionResult> AttachSlotAsync(AttachSlot request, CancellationToken ct);
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
            drafts[state.DraftId] = state; byKey[request.Key] = state.DraftId;
            Log(state, "draft.opened", null, null, null, null);
            return ValueTask.FromResult<OpenDraftResult>(new OpenDraftResult.Opened(Snapshot(state), true));
        }
    }

    public ValueTask<DraftRecord?> GetDraftAsync(Guid draftId, CancellationToken ct)
    {
        lock (gate) return ValueTask.FromResult(drafts.TryGetValue(draftId, out var s) ? Snapshot(s) : null);
    }

    public ValueTask<ClaimResult> ClaimSlotAsync(ClaimSlot request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!drafts.TryGetValue(request.DraftId, out var d)) return Done<ClaimResult>(new ClaimResult.Rejected("draft_missing"));
            if (!d.Slots.TryGetValue(request.SlotId, out var slot)) return Done<ClaimResult>(new ClaimResult.Rejected("slot_unknown"));
            switch (slot.Status)
            {
                case SlotStatus.Attached: return Done<ClaimResult>(new ClaimResult.AlreadyAttached(slot));
                case SlotStatus.Claimed: return Done<ClaimResult>(new ClaimResult.InFlight(slot));
            }
            if (d.Status == DraftStatus.Sealed) return Done<ClaimResult>(new ClaimResult.DraftSealed());
            // The claim is recorded in the authoritative store BEFORE any provider call. WHY: if the process dies after spend, the slot is visibly
            // Claimed (Pending) instead of looking Empty and being paid for twice.
            var claimed = slot with { Status = SlotStatus.Claimed, Attempts = slot.Attempts + 1, StepId = request.StepId, Output = null,
                ExecutionId = request.ExecutionId, Provenance = request.Provenance, Code = null, Fingerprint = request.Fingerprint };
            d.Slots = d.Slots.SetItem(slot.SlotId, claimed);
            Log(d, "slot.claimed", slot.SlotId, request.StepId, request.ExecutionId, request.Provenance.ToString());
            return Done<ClaimResult>(new ClaimResult.Claimed(claimed));
        }
    }

    public ValueTask<TransitionResult> AttachSlotAsync(AttachSlot request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        // No ct.ThrowIfCancellationRequested here: the caller already paid for this output. Recording it must win over cancellation.
        lock (gate)
        {
            if (!TryClaimed(request.DraftId, request.SlotId, request.StepId, out var d, out var slot, out var error)) return Done<TransitionResult>(new TransitionResult.Rejected(error));
            var attached = slot with { Status = SlotStatus.Attached, Output = request.Output, ExecutionId = request.ExecutionId ?? slot.ExecutionId, Code = null };
            d.Slots = d.Slots.SetItem(slot.SlotId, attached);
            Log(d, "slot.attached", slot.SlotId, request.StepId, request.ExecutionId, slot.Provenance?.ToString());
            return Done<TransitionResult>(new TransitionResult.Applied(attached));
        }
    }

    public ValueTask<TransitionResult> ReleaseSlotAsync(ReleaseSlot request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.To is not (SlotStatus.Failed or SlotStatus.Cancelled)) throw new ArgumentException("Release moves to Failed or Cancelled", nameof(request));
        lock (gate)
        {
            if (!TryClaimed(request.DraftId, request.SlotId, request.StepId, out var d, out var slot, out var error)) return Done<TransitionResult>(new TransitionResult.Rejected(error));
            var released = slot with { Status = request.To, Code = request.Code, ExecutionId = request.ExecutionId ?? slot.ExecutionId };
            d.Slots = d.Slots.SetItem(slot.SlotId, released);
            Log(d, request.To == SlotStatus.Failed ? "slot.failed" : "slot.cancelled", slot.SlotId, request.StepId, request.ExecutionId, request.Code);
            return Done<TransitionResult>(new TransitionResult.Applied(released));
        }
    }

    public ValueTask<TransitionResult> AppendEventAsync(AppendEvent request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!TryClaimed(request.DraftId, request.SlotId, request.StepId, out var d, out var slot, out var error)) return Done<TransitionResult>(new TransitionResult.Rejected(error));
            Log(d, request.Kind, slot.SlotId, request.StepId, request.ExecutionId, request.Detail);
            return Done<TransitionResult>(new TransitionResult.Applied(slot));
        }
    }

    public ValueTask<SealResult> SealAsync(SealDraft request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            if (!drafts.TryGetValue(request.DraftId, out var d)) return Done<SealResult>(new SealResult.Rejected("draft_missing"));
            if (d.Status == DraftStatus.Sealed) return Done<SealResult>(new SealResult.AlreadySealed(Snapshot(d)));
            // Membership CAS: the aggregate may only claim members that are attached right now, with exactly these versions.
            var attached = d.Slots.Values.Where(s => s.Status == SlotStatus.Attached).ToDictionary(s => s.SlotId, StringComparer.Ordinal);
            if (attached.Count != request.Aggregate.Members.Count) return Done<SealResult>(new SealResult.Rejected("membership_changed"));
            foreach (var m in request.Aggregate.Members)
                if (!attached.TryGetValue(m.SlotId, out var s) || s.Output != m.Output) return Done<SealResult>(new SealResult.Rejected("membership_changed"));
            // Aggregate and manifest are one record, set in one critical section: no reader sees one without the other.
            d.Status = DraftStatus.Sealed; d.Aggregate = request.Aggregate;
            Log(d, "draft.sealed", null, null, null, request.Aggregate.Reference.AggregateId.ToString());
            return Done<SealResult>(new SealResult.Sealed(Snapshot(d)));
        }
    }

    bool TryClaimed(Guid draftId, string slotId, Guid stepId, out State d, out SlotRecord slot, out string error)
    {
        slot = null!; d = null!; error = "";
        if (!drafts.TryGetValue(draftId, out d!)) { error = "draft_missing"; return false; }
        if (!d.Slots.TryGetValue(slotId, out slot!)) { error = "slot_unknown"; return false; }
        // StepId is the fencing token inside one process: a stale step cannot overwrite a newer attempt.
        if (slot.Status != SlotStatus.Claimed || slot.StepId != stepId) { error = "not_claimed_by_step"; return false; }
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
