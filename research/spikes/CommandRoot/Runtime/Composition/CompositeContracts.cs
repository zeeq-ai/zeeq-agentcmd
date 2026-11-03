using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using HumanGrading;
using LlmCmd.ContextPropagation;
namespace CommandRoot;

// WHY this file exists: it holds only declarations (no behavior) so a source generator can emit against a
// small, stable surface. Everything else in Runtime/Composition is replaceable behind these types.

/// <summary>Marks a whole output type that is assembled from independently produced parts.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public sealed class CompositeOutputAttribute(string id) : Attribute
{
    public string Id { get; } = id;
    public int Revision { get; init; } = 1;
    /// <summary>Optional explicit serializer context. The generator uses it for JsonTypeInfo of the whole when set.</summary>
    public Type? JsonContext { get; init; }
}

/// <param name="SlotId">Stable responsibility id. WHY separate from JsonName: renaming a wire property must not
/// orphan stored claims, and a wire name may be changed by naming policy or [JsonPropertyName].</param>
/// <param name="JsonName">The serialized property name that the whole's JsonTypeInfo reads. Never a CLR name.</param>
public sealed record SlotDescriptor(string SlotId, string JsonName, Type PartType, bool Required);

/// <summary>
/// A typed slot. Sealed and invariant on purpose: a PartSlot&lt;W, Header&gt; can never be passed where a
/// PartSlot&lt;W, Parties&gt; is expected, so a wrong-part wiring is a compile error, not a runtime surprise.
/// </summary>
public sealed class PartSlot<TWhole, TPart>
{
    PartSlot(SlotDescriptor descriptor) { Descriptor = descriptor; }
    public SlotDescriptor Descriptor { get; }
    public string SlotId => Descriptor.SlotId;

    // Declaration happens in static initializers, so a bad name is a programmer error and throws early.
    public static PartSlot<TWhole, TPart> Declare(string slotId, string jsonName, bool required = true)
    {
        if (!CompositionNames.IsKey(slotId)) throw new ArgumentException("Slot id must be 1-128 of letters, digits, '.', '_', '-', ':'", nameof(slotId));
        if (string.IsNullOrEmpty(jsonName)) throw new ArgumentException("JSON name is required", nameof(jsonName));
        return new(new SlotDescriptor(slotId, jsonName, typeof(TPart), required));
    }
}

public interface ICompositeDescriptor
{
    Type WholeType { get; }
    string Id { get; }
    int Revision { get; }
    string DefinitionHash { get; }
    IReadOnlyList<SlotDescriptor> Slots { get; }
    JsonTypeInfo WholeInfo { get; }
    /// <summary>Runs the descriptor's cross-part check on a boxed whole. Returns a reason or null.</summary>
    string? ValidateObject(object whole);
}

/// <param name="DefinitionHash">Covers Id, Revision and the ordered slot shape. A draft opened under another hash is refused,
/// because mixing parts from two definitions of the same whole is how silent schema drift happens.</param>
/// <param name="Validate">Cross-part invariant on the assembled whole. Returns a reason or null.</param>
public sealed record CompositeDescriptor<TWhole>(string Id, int Revision, string DefinitionHash,
    JsonTypeInfo<TWhole> Whole, IReadOnlyList<SlotDescriptor> Slots,
    Func<TWhole, string?>? Validate = null) : ICompositeDescriptor
{
    public Type WholeType => typeof(TWhole);
    public JsonTypeInfo WholeInfo => Whole;
    public string? ValidateObject(object whole) => Validate?.Invoke((TWhole)whole);
}

/// <summary>
/// Identity of one draft. WHY these four plus tenant: the same input revision may be composed again for a different trial
/// or key without sharing claims, and one tenant can never open another tenant's draft by guessing a RunId.
/// </summary>
public sealed record CompositionRequest(Guid RunId, string InputRevision, string Key, string? TrialId = null);

/// <summary>
/// The sealed aggregate. WHY Output carries ProducerRef.Composition(DraftId): an assembled output has no provider execution, and
/// borrowing the last component's ExecutionId would let a grade or correction look like it targets a model call. The producer is the
/// DRAFT (sealed once), so it is stable across a retry; AggregateId is the stored artifact's id, not part of the producer.
/// </summary>
public sealed record AggregateRef(Guid DraftId, Guid AggregateId, OutputRef Output, string DefinitionId, int Revision, string DefinitionHash);

public enum MemberProvenance { Executed, Reused }

/// <summary>One exact stored version that went into an aggregate. This is the membership record. It is never inferred from traces.</summary>
public sealed record CompositeMember(string SlotId, string JsonName, OutputRef Output, MemberProvenance Provenance,
    Guid? ExecutionId, Guid? StepId);

public enum StepStatus
{
    /// <summary>This handle executed the command and the output is stored.</summary>
    Attached,
    /// <summary>The slot was already attached. The known receipt is returned and no command ran.</summary>
    Replayed,
    /// <summary>A pinned existing output was attached.</summary>
    Reused,
    /// <summary>The command ran (or could not run) and the slot stays unfilled. A new attempt is allowed.</summary>
    Failed,
    Cancelled,
    /// <summary>Claimed but not resolved: model spend is unknown and the command is NOT re-executed.</summary>
    Pending,
    /// <summary>Not started because a dependency did not attach. No spend.</summary>
    Skipped,
    /// <summary>The slot is attached to a different output or the draft is sealed without this slot.</summary>
    Conflict,
}

/// <summary>Per-slot envelope. Callback and dependency failures live here, never as an exception out of ExecuteAsync.</summary>
/// <remarks>
/// ExecutionId is null when no command ran (skipped, callback failed, never claimed). Failure, Snapshot, Delivery and Context are the
/// leaf runtime's own objects, forwarded untouched, so a caller sees the exact ExecutionFailure and ExecutionSnapshot a direct call would give.
/// Operation is the slot's own OperationContext (run, draft, WorkId = StepId). It is set for every step this call created, including
/// callback and pre-claim failures where no CommandExecutionContext exists, so such a step is still correlatable without an invented ExecutionId.
/// Causes lists the dependency steps that blocked a Skipped step, so the original failure stays reachable and is not reduced to a code string.
/// </remarks>
public sealed record StepResult(string SlotId, StepStatus Status, Guid? StepId = null, Guid? ExecutionId = null,
    OutputRef? Output = null, string? Code = null, CallSite? Caller = null,
    ExecutionFailure? Failure = null, ExecutionSnapshot? Snapshot = null, OutputDelivery? Delivery = null,
    CommandExecutionContext? Context = null, Guid? DraftId = null,
    OperationContext? Operation = null, IReadOnlyList<StepResult>? Causes = null);

public closed record CompositeOutcome<TWhole>
{
    public sealed record Published(TWhole Value, Guid DraftId, AggregateRef Aggregate,
        IReadOnlyList<CompositeMember> Members, IReadOnlyList<StepResult> Steps, bool Replayed) : CompositeOutcome<TWhole>;
    /// <summary>Some slots are unfilled. Steps say which and why. Filled slots stay attached for a later attempt.</summary>
    public sealed record Incomplete(Guid DraftId, IReadOnlyList<StepResult> Steps) : CompositeOutcome<TWhole>;
    /// <summary>Stored facts disagree with the request: different pinned output, sealed with other membership, or parts that do not compose.</summary>
    public sealed record Conflict(Guid? DraftId, string Code, string Message, IReadOnlyList<StepResult> Steps) : CompositeOutcome<TWhole>;
    /// <summary>The store or ledger failed. Slots may be Pending. Nothing is assumed about spend.</summary>
    public sealed record Unavailable(Guid? DraftId, string Reason, IReadOnlyList<StepResult> Steps) : CompositeOutcome<TWhole>;
    /// <summary>Wiring is wrong. Raised before any claim, so no provider spend happened.</summary>
    public sealed record ConfigurationFailure(IReadOnlyList<string> Problems, Guid? DraftId = null, CompositionRequest? Request = null) : CompositeOutcome<TWhole>;
    public sealed record Cancelled(Guid? DraftId, IReadOnlyList<StepResult> Steps) : CompositeOutcome<TWhole>;
}

/// <summary>An exact stored version. The runner never trusts Value: it re-reads the artifact from the ledger by Reference.</summary>
public sealed record Pinned<T>(T Value, OutputRef Reference);

static class CompositionNames
{
    internal static bool IsKey(string? value, int max = 128) => !string.IsNullOrEmpty(value) && value.Length <= max
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':');
}
