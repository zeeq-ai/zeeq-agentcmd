using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeeq.LlmCmd;

// Actor comes from the authenticated application, never from trace baggage.
/// <summary>Authenticated application identity used for ledger authorization; never derived from trace baggage.</summary>
/// <param name="Tenant">Tenant boundary.</param>
/// <param name="Subject">Authenticated subject within the tenant.</param>
public sealed record Actor(string Tenant, string Subject);
// An assembled output has no provider execution. Give its actual producer a
// first-class identity instead of borrowing the last component's execution ID.
/// <summary>Identifies the producer of an output without attributing composed or corrected outputs to an unrelated execution.</summary>
/// <param name="Id">Producer identifier within its kind.</param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$producer")]
[JsonDerivedType(typeof(ProducerRef.CommandExecution), "command")]
[JsonDerivedType(typeof(ProducerRef.Composition), "composition")]
[JsonDerivedType(typeof(ProducerRef.HumanCorrection), "correction")]
public closed record ProducerRef(Guid Id)
{
    /// <summary>Output produced by a command execution.</summary>
    /// <param name="Id">Command execution identifier.</param>
    public sealed record CommandExecution(Guid Id) : ProducerRef(Id);
    /// <summary>Output assembled from a composition draft.</summary>
    /// <param name="Id">Composition draft identifier.</param>
    public sealed record Composition(Guid Id) : ProducerRef(Id);
    /// <summary>Output produced by a human correction and linked to its source producer.</summary>
    /// <param name="Id">Correction event identifier.</param>
    /// <param name="Source">Producer of the corrected version.</param>
    public sealed record HumanCorrection(Guid Id, ProducerRef Source) : ProducerRef(Id);
    /// <summary>Stable producer kind used in serialized lineage.</summary>
    public string Kind => this switch { CommandExecution => "command", Composition => "composition", HumanCorrection => "correction" };
    /// <summary>Original command execution in the ancestry, when one exists.</summary>
    [JsonIgnore]
    public Guid? SourceExecutionId => this switch
    {
        CommandExecution command => command.Id,
        Composition => null,
        HumanCorrection correction => correction.Source?.SourceExecutionId
    };
    /// <summary>Original composition draft in the ancestry, when one exists.</summary>
    [JsonIgnore]
    public Guid? SourceDraftId => this switch
    {
        CommandExecution => null,
        Composition composition => composition.Id,
        HumanCorrection correction => correction.Source?.SourceDraftId
    };
}
/// <summary>Exact reference to an immutable output version and its producer lineage.</summary>
/// <param name="RunId">Run that owns the artifact.</param>
/// <param name="Producer">Producer of this specific version.</param>
/// <param name="ArtifactId">Stable artifact identity across versions.</param>
/// <param name="VersionId">Unique identity of this exact version.</param>
/// <param name="Version">Version number within its correction lineage.</param>
[method: JsonConstructor]
public sealed record OutputRef(Guid RunId, ProducerRef Producer, Guid ArtifactId, Guid VersionId, int Version)
{
    // Compatibility for existing command callers. Composite outputs deliberately
    // have no ExecutionId; a correction retains its original execution ancestry.
    /// <summary>Creates a command-produced reference from the legacy execution identifier.</summary>
    /// <param name="runId">Owning run.</param>
    /// <param name="executionId">Original command execution.</param>
    /// <param name="artifactId">Artifact identity shared by its versions.</param>
    /// <param name="versionId">Identity of this exact version.</param>
    /// <param name="version">Version number within its lineage.</param>
    public OutputRef(Guid runId, Guid executionId, Guid artifactId, Guid versionId, int version)
        : this(runId, new ProducerRef.CommandExecution(executionId), artifactId, versionId, version) { }
    /// <summary>Original execution in the producer ancestry, or null for a composition.</summary>
    public Guid? ExecutionId
    {
        get => Producer?.SourceExecutionId;
        init
        {
            // Legacy JSON includes this convenience field. Preserve correction
            // ancestry when it agrees; a changed ID still makes a tampered ref.
            if (value is Guid id && Producer?.SourceExecutionId != id)
            {
                Producer = new ProducerRef.CommandExecution(id);
            }
        }
    }
}
/// <summary>Portable trace identifiers used as a link, not an authorization source.</summary>
/// <param name="TraceId">W3C trace identifier.</param>
/// <param name="SpanId">W3C span identifier.</param>
/// <param name="Recorded">Whether the trace was sampled.</param>
/// <param name="TraceState">Optional W3C trace state.</param>
public sealed record TraceAnchor(string TraceId, string SpanId, bool Recorded, string? TraceState)
{
    /// <summary>Creates an anchor for valid activity identifiers, or null when either is absent.</summary>
    /// <param name="context">Activity context to capture.</param>
    public static TraceAnchor? From(ActivityContext context) => context.TraceId == default || context.SpanId == default
        ? null : new(context.TraceId.ToString(), context.SpanId.ToString(), context.TraceFlags.HasFlag(ActivityTraceFlags.Recorded), context.TraceState);

    /// <summary>Parses the anchor as an activity context for a trace link.</summary>
    /// <param name="context">Parsed context on success.</param>
    /// <returns>True when the anchor is a valid W3C context.</returns>
    public bool TryGetContext(out ActivityContext context) => ActivityContext.TryParse(
        $"00-{TraceId}-{SpanId}-{(Recorded ? "01" : "00")}", TraceState, out context);
}
/// <summary>An immutable output version and its schema and lineage metadata.</summary>
/// <param name="Reference">Exact reference for this version.</param>
/// <param name="Value">JSON value of this version.</param>
/// <param name="SchemaVersion">Schema version supplied by the producer.</param>
/// <param name="Origin">Optional trace anchor for the original output.</param>
/// <param name="ParentVersionId">Prior version identifier, or null for an original output.</param>
public sealed record OutputArtifact(OutputRef Reference, JsonElement Value, string SchemaVersion, TraceAnchor? Origin, Guid? ParentVersionId);
/// <summary>A grade against an exact output version and expected field value.</summary>
/// <param name="EventId">Idempotency identifier for this feedback event.</param>
/// <param name="Target">Exact output version to grade.</param>
/// <param name="Pointer">JSON Pointer selecting the field.</param>
/// <param name="ExpectedValue">Expected current field value for conflict detection.</param>
/// <param name="Score">Finite grade in the range zero through one.</param>
/// <param name="Rationale">Optional human explanation.</param>
/// <param name="SupersedesEventId">Optional earlier grade from the same grader for this target and field.</param>
public sealed record GradeRequest(Guid EventId, OutputRef Target, string Pointer, JsonElement ExpectedValue, double Score, string? Rationale = null, Guid? SupersedesEventId = null);
/// <summary>A replacement against an exact output version and expected field value.</summary>
/// <param name="EventId">Idempotency identifier for this correction.</param>
/// <param name="Target">Exact output version to correct.</param>
/// <param name="Pointer">JSON Pointer selecting an existing value.</param>
/// <param name="ExpectedValue">Expected current field value for conflict detection.</param>
/// <param name="Replacement">Replacement JSON value.</param>
public sealed record CorrectionRequest(Guid EventId, OutputRef Target, string Pointer, JsonElement ExpectedValue, JsonElement Replacement);
/// <summary>Recorded evidence of a human grade on a specific field and version.</summary>
/// <param name="EventId">Feedback event identifier.</param>
/// <param name="Target">Exact graded version.</param>
/// <param name="Pointer">JSON Pointer to the graded field.</param>
/// <param name="ObservedValue">Value observed in the target version.</param>
/// <param name="Score">Recorded grade.</param>
/// <param name="Rationale">Optional grader explanation.</param>
/// <param name="Grader">Authenticated subject who graded the field.</param>
/// <param name="RecordedAt">UTC time of recording.</param>
/// <param name="SupersedesEventId">Prior grade replaced by this event, if supplied.</param>
/// <param name="FeedbackTrace">Optional trace anchor for the feedback operation.</param>
public sealed record GradeEvent(Guid EventId, OutputRef Target, string Pointer, JsonElement ObservedValue, double Score, string? Rationale, string Grader, DateTimeOffset RecordedAt, Guid? SupersedesEventId, TraceAnchor? FeedbackTrace);
/// <summary>Recorded correction linking the source and newly created output versions.</summary>
/// <param name="EventId">Correction event identifier.</param>
/// <param name="Target">Exact source version.</param>
/// <param name="Corrected">Exact new version.</param>
/// <param name="Pointer">JSON Pointer to the replaced value.</param>
/// <param name="Before">Observed value in the source version.</param>
/// <param name="After">Replacement value.</param>
/// <param name="Corrector">Authenticated subject who submitted the correction.</param>
/// <param name="RecordedAt">UTC time of recording.</param>
/// <param name="FeedbackTrace">Optional trace anchor for the feedback operation.</param>
public sealed record CorrectionEvent(Guid EventId, OutputRef Target, OutputRef Corrected, string Pointer, JsonElement Before, JsonElement After, string Corrector, DateTimeOffset RecordedAt, TraceAnchor? FeedbackTrace);
/// <summary>Receipt for an accepted or replayed grade.</summary>
/// <param name="Event">The recorded grade event.</param>
public sealed record GradeReceipt(GradeEvent Event);
/// <summary>Receipt containing the correction event and new immutable version.</summary>
/// <param name="Event">The recorded correction event.</param>
/// <param name="Output">The corrected output version.</param>
public sealed record CorrectionReceipt(CorrectionEvent Event, OutputArtifact Output);

/// <summary>Accepted or rejected result of a ledger operation.</summary>
/// <typeparam name="T">Value returned on acceptance.</typeparam>
public closed record LedgerResult<T>
{
    /// <summary>Accepted value; Replayed indicates an idempotent repeat.</summary>
    /// <param name="Value">Accepted value.</param>
    /// <param name="Replayed">True for a repeated idempotent request.</param>
    public sealed record Accepted(T Value, bool Replayed = false) : LedgerResult<T>;
    /// <summary>Rejection with a stable code and explanatory message.</summary>
    /// <param name="Code">Stable rejection code.</param>
    /// <param name="Message">Human-readable reason.</param>
    public sealed record Rejected(string Code, string Message) : LedgerResult<T>;
}

/// <summary>Authoritative store for run access, exact output versions and human feedback.</summary>
/// <remarks>Implementations must enforce actor authorization and preserve immutable versions and feedback idempotency. Trace metadata is observational and is not authority.</remarks>
public interface IHumanFeedbackLedger
{
    /// <summary>Creates a run owned by the supplied authenticated actor; an invalid actor may be rejected with an empty identifier.</summary>
    /// <param name="actor">Authenticated run owner.</param>
    Guid CreateRun(Actor actor);
    /// <summary>Checks whether the actor can access the run; the default denies access.</summary>
    /// <param name="runId">Run to check.</param>
    /// <param name="actor">Authenticated actor requesting access.</param>
    bool CanAccessRun(Guid runId, Actor actor) => false;
    /// <summary>Records an original command output under an authorized run and exact execution identity.</summary>
    /// <param name="runId">Owning run.</param>
    /// <param name="executionId">Producer command execution.</param>
    /// <param name="value">JSON output value.</param>
    /// <param name="schemaVersion">Schema identity for the output.</param>
    /// <param name="origin">Activity context for optional trace linkage.</param>
    /// <param name="actor">Authenticated actor requesting the write.</param>
    LedgerResult<OutputArtifact> RecordOutput(Guid runId, Guid executionId, JsonElement value, string schemaVersion, ActivityContext origin, Actor actor);
    // A host adapter must implement the producer contract explicitly. A default
    // rejection would let all paid parts succeed before discovering that the
    // same ledger can never store their aggregate.
    /// <summary>Records an original command or composition output using explicit producer lineage.</summary>
    /// <param name="runId">Owning run.</param>
    /// <param name="producer">Command or composition producer identity.</param>
    /// <param name="value">JSON output value.</param>
    /// <param name="schemaVersion">Schema identity for the output.</param>
    /// <param name="origin">Activity context for optional trace linkage.</param>
    /// <param name="actor">Authenticated actor requesting the write.</param>
    LedgerResult<OutputArtifact> RecordProducedOutput(Guid runId, ProducerRef producer, JsonElement value, string schemaVersion, ActivityContext origin, Actor actor);
    /// <summary>Reads an exact output version only when the actor is authorized and the full reference matches.</summary>
    /// <param name="target">Full exact version reference.</param>
    /// <param name="actor">Authenticated actor requesting access.</param>
    LedgerResult<OutputArtifact> ReadOutput(OutputRef target, Actor actor);
    /// <summary>Records or replays an idempotent grade against an exact version and expected field value.</summary>
    /// <param name="request">Grade event and expected value.</param>
    /// <param name="actor">Authenticated grader.</param>
    LedgerResult<GradeReceipt> RecordGrade(GradeRequest request, Actor actor);
    /// <summary>Records or replays a correction as a new version; the source version remains unchanged.</summary>
    /// <param name="request">Correction event, expected value and replacement.</param>
    /// <param name="actor">Authenticated corrector.</param>
    LedgerResult<CorrectionReceipt> SubmitCorrection(CorrectionRequest request, Actor actor);
}
