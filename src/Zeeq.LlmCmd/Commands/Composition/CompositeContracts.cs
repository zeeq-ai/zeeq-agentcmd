using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
namespace Zeeq.LlmCmd;

// WHY this file exists: it holds only declarations (no behavior) so a source generator can emit against a
// small, stable surface. Everything else in Runtime/Composition is replaceable behind these types.

/// <summary>Marks a whole output type that is assembled from independently produced parts.</summary>
/// <param name="id">Stable composition definition key.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public sealed class CompositeOutputAttribute(string id) : Attribute
{
    /// <summary>Stable composition definition key.</summary>
    public string Id { get; } = id;
    /// <summary>Definition revision, defaulting to one.</summary>
    public int Revision { get; init; } = 1;
    /// <summary>Optional explicit serializer context. The generator uses it for JsonTypeInfo of the whole when set.</summary>
    public Type? JsonContext
    {
        get; init;
    }
}

/// <summary>Untyped metadata for a required or optional composition slot.</summary>
/// <param name="SlotId">Stable responsibility id. WHY separate from JsonName: renaming a wire property must not
/// orphan stored claims, and a wire name may be changed by naming policy or [JsonPropertyName].</param>
/// <param name="JsonName">The serialized property name that the whole's JsonTypeInfo reads. Never a CLR name.</param>
/// <param name="PartType">CLR type accepted by the slot.</param>
/// <param name="Required">Whether sealing requires this slot.</param>
public sealed record SlotDescriptor(string SlotId, string JsonName, Type PartType, bool Required);

/// <summary>
/// A typed slot. Sealed and invariant on purpose: a PartSlot&lt;W, Header&gt; can never be passed where a
/// PartSlot&lt;W, Parties&gt; is expected, so a wrong-part wiring is a compile error, not a runtime surprise.
/// </summary>
public sealed class PartSlot<TWhole, TPart>
{
    PartSlot(SlotDescriptor descriptor)
    {
        Descriptor = descriptor;
    }
    /// <summary>Untyped metadata used by the composition engine.</summary>
    public SlotDescriptor Descriptor
    {
        get;
    }
    /// <summary>Stable slot key independent of its JSON property name.</summary>
    public string SlotId => Descriptor.SlotId;

    // Declaration happens in static initializers, so a bad name is a programmer error and throws early.
    /// <summary>Declares a typed slot and validates its stable key and JSON name.</summary>
    public static PartSlot<TWhole, TPart> Declare(string slotId, string jsonName, bool required = true)
    {
        if (!CompositionNames.IsKey(slotId))
        {
            throw new ArgumentException("Slot id must be 1-128 of letters, digits, '.', '_', '-', ':'", nameof(slotId));
        }

        if (string.IsNullOrEmpty(jsonName))
        {
            throw new ArgumentException("JSON name is required", nameof(jsonName));
        }

        return new(new SlotDescriptor(slotId, jsonName, typeof(TPart), required));
    }
}

/// <summary>Untyped composition metadata used by the runtime registry.</summary>
public interface ICompositeDescriptor
{
    /// <summary>CLR type of the assembled result.</summary>
    Type WholeType
    {
        get;
    }
    /// <summary>Stable composition definition key.</summary>
    string Id
    {
        get;
    }
    /// <summary>Definition revision.</summary>
    int Revision
    {
        get;
    }
    /// <summary>Hash of the definition and slot shape.</summary>
    string DefinitionHash
    {
        get;
    }
    /// <summary>Declared slots in definition order.</summary>
    IReadOnlyList<SlotDescriptor> Slots
    {
        get;
    }
    /// <summary>Serializer metadata used to read the assembled whole.</summary>
    JsonTypeInfo WholeInfo
    {
        get;
    }
    /// <summary>Runs the descriptor's cross-part check on a boxed whole. Returns a reason or null.</summary>
    string? ValidateObject(object whole);
}

/// <summary>Typed composition definition and cross-part validation contract.</summary>
/// <param name="Id">Stable definition key.</param>
/// <param name="Revision">Definition revision.</param>
/// <param name="Whole">Serializer metadata for the whole.</param>
/// <param name="Slots">Declared composition slots.</param>
/// <param name="DefinitionHash">Covers Id, Revision and the ordered slot shape. A draft opened under another hash is refused,
/// because mixing parts from two definitions of the same whole is how silent schema drift happens.</param>
/// <param name="Validate">Cross-part invariant on the assembled whole. Returns a reason or null.</param>
public sealed record CompositeDescriptor<TWhole>(string Id, int Revision, string DefinitionHash,
    JsonTypeInfo<TWhole> Whole, IReadOnlyList<SlotDescriptor> Slots,
    Func<TWhole, string?>? Validate = null) : ICompositeDescriptor
{
    /// <inheritdoc />
    public Type WholeType => typeof(TWhole);
    /// <inheritdoc />
    public JsonTypeInfo WholeInfo => Whole;
    /// <inheritdoc />
    public string? ValidateObject(object whole) => Validate?.Invoke((TWhole)whole);
}

/// <summary>
/// Identity of one draft. WHY these four plus tenant: the same input revision may be composed again for a different trial
/// or key without sharing claims, and one tenant can never open another tenant's draft by guessing a RunId.
/// </summary>
/// <param name="RunId">Owning run.</param>
/// <param name="InputRevision">Caller-assigned input version.</param>
/// <param name="Key">Idempotency key within the run.</param>
/// <param name="TrialId">Optional trial identifier.</param>
public sealed record CompositionRequest(Guid RunId, string InputRevision, string Key, string? TrialId = null);

/// <summary>
/// The sealed aggregate. WHY Output carries ProducerRef.Composition(DraftId): an assembled output has no provider execution, and
/// borrowing the last component's ExecutionId would let a grade or correction look like it targets a model call. The producer is the
/// DRAFT (sealed once), so it is stable across a retry; AggregateId is the stored artifact's id, not part of the producer.
/// </summary>
/// <param name="DraftId">Producing draft.</param>
/// <param name="AggregateId">Aggregate artifact identifier.</param>
/// <param name="Output">Exact aggregate output version.</param>
/// <param name="DefinitionId">Composition definition key.</param>
/// <param name="Revision">Definition revision.</param>
/// <param name="DefinitionHash">Hash of the definition used to assemble the aggregate.</param>
public sealed record AggregateRef(Guid DraftId, Guid AggregateId, OutputRef Output, string DefinitionId, int Revision, string DefinitionHash);

/// <summary>How a stored component entered the composition.</summary>
public enum MemberProvenance
{
    /// <summary>A command produced the component for this draft.</summary>
    Executed,
    /// <summary>An existing exact output was pinned and reused.</summary>
    Reused
}

/// <summary>One exact stored version that went into an aggregate. This is the membership record. It is never inferred from traces.</summary>
/// <param name="SlotId">Stable slot key.</param>
/// <param name="JsonName">Aggregate JSON property name.</param>
/// <param name="Output">Exact stored component version.</param>
/// <param name="Provenance">How the component was attached.</param>
/// <param name="ExecutionId">Producing command execution, if applicable.</param>
/// <param name="StepId">Attaching step identifier, if applicable.</param>
public sealed record CompositeMember(string SlotId, string JsonName, OutputRef Output, MemberProvenance Provenance,
    Guid? ExecutionId, Guid? StepId);

/// <summary>Outcome of one composition slot step.</summary>
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
    /// <summary>The step was cancelled before it attached an output.</summary>
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
/// <param name="SlotId">Slot associated with the step.</param>
/// <param name="Status">Step outcome.</param>
/// <param name="StepId">Step identifier, if created.</param>
/// <param name="ExecutionId">Actual command execution, if one ran.</param>
/// <param name="Output">Exact attached output, if any.</param>
/// <param name="Code">Machine-readable outcome detail.</param>
/// <param name="Caller">Registration call site, if captured.</param>
/// <param name="Failure">Original command failure, if any.</param>
/// <param name="Snapshot">Original execution snapshot, if any.</param>
/// <param name="Delivery">Original output delivery result, if any.</param>
/// <param name="Context">Command execution context, if any.</param>
/// <param name="DraftId">Owning draft, if opened.</param>
/// <param name="Operation">Captured operation for this step, if created.</param>
/// <param name="Causes">Dependency failures that prevented the step from running.</param>
public sealed record StepResult(string SlotId, StepStatus Status, Guid? StepId = null, Guid? ExecutionId = null,
    OutputRef? Output = null, string? Code = null, CallSite? Caller = null,
    ExecutionFailure? Failure = null, ExecutionSnapshot? Snapshot = null, OutputDelivery? Delivery = null,
    CommandExecutionContext? Context = null, Guid? DraftId = null,
    OperationContext? Operation = null, IReadOnlyList<StepResult>? Causes = null);

/// <summary>Publication or failure outcome for a composition attempt.</summary>
public closed record CompositeOutcome<TWhole>
{
    /// <summary>The aggregate was published or replayed.</summary>
    /// <param name="Value">Typed whole deserialized from aggregate JSON.</param>
    /// <param name="DraftId">Producing draft.</param>
    /// <param name="Aggregate">Exact stored aggregate reference.</param>
    /// <param name="Members">Exact component references and provenance.</param>
    /// <param name="Steps">Per-slot outcomes.</param>
    /// <param name="Replayed">Whether a prior publication was returned.</param>
    public sealed record Published(TWhole Value, Guid DraftId, AggregateRef Aggregate,
        IReadOnlyList<CompositeMember> Members, IReadOnlyList<StepResult> Steps, bool Replayed) : CompositeOutcome<TWhole>;
    /// <summary>Some slots are unfilled. Steps say which and why. Filled slots stay attached for a later attempt.</summary>
    /// <param name="DraftId">Open draft identifier.</param>
    /// <param name="Steps">Per-slot outcomes.</param>
    public sealed record Incomplete(Guid DraftId, IReadOnlyList<StepResult> Steps) : CompositeOutcome<TWhole>;
    /// <summary>Stored facts disagree with the request: different pinned output, sealed with other membership, or parts that do not compose.</summary>
    /// <param name="DraftId">Draft identifier, if opened.</param>
    /// <param name="Code">Conflict category.</param>
    /// <param name="Message">Safe conflict explanation.</param>
    /// <param name="Steps">Per-slot outcomes.</param>
    public sealed record Conflict(Guid? DraftId, string Code, string Message, IReadOnlyList<StepResult> Steps) : CompositeOutcome<TWhole>;
    /// <summary>The store or ledger failed. Slots may be Pending. Nothing is assumed about spend.</summary>
    /// <param name="DraftId">Draft identifier, if opened.</param>
    /// <param name="Reason">Safe unavailability explanation.</param>
    /// <param name="Steps">Per-slot outcomes.</param>
    public sealed record Unavailable(Guid? DraftId, string Reason, IReadOnlyList<StepResult> Steps) : CompositeOutcome<TWhole>;
    /// <summary>Wiring is wrong. Raised before any claim, so no provider spend happened.</summary>
    /// <param name="Problems">Preflight configuration problems.</param>
    /// <param name="DraftId">Draft identifier, if opened.</param>
    /// <param name="Request">Original request, if available.</param>
    public sealed record ConfigurationFailure(IReadOnlyList<string> Problems, Guid? DraftId = null, CompositionRequest? Request = null) : CompositeOutcome<TWhole>;
    /// <summary>Composition was cancelled before publication.</summary>
    /// <param name="DraftId">Draft identifier, if opened.</param>
    /// <param name="Steps">Per-slot outcomes known at cancellation.</param>
    public sealed record Cancelled(Guid? DraftId, IReadOnlyList<StepResult> Steps) : CompositeOutcome<TWhole>;
}

/// <summary>An exact stored version. The runner never trusts Value: it re-reads the artifact from the ledger by Reference.</summary>
/// <param name="Value">Convenience typed value; the runner re-reads the reference.</param>
/// <param name="Reference">Exact stored output version.</param>
public sealed record Pinned<T>(T Value, OutputRef Reference);

static class CompositionNames
{
    internal static bool IsKey(string? value, int max = 128) => !string.IsNullOrEmpty(value) && value.Length <= max
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':');
}
