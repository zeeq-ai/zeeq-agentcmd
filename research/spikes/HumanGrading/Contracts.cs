using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HumanGrading;

// Actor comes from the authenticated application, never from trace baggage.
public sealed record Actor(string Tenant, string Subject);
// An assembled output has no provider execution. Give its actual producer a
// first-class identity instead of borrowing the last component's execution ID.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$producer")]
[JsonDerivedType(typeof(ProducerRef.CommandExecution), "command")]
[JsonDerivedType(typeof(ProducerRef.Composition), "composition")]
[JsonDerivedType(typeof(ProducerRef.HumanCorrection), "correction")]
public closed record ProducerRef(Guid Id)
{
    public sealed record CommandExecution(Guid Id) : ProducerRef(Id);
    public sealed record Composition(Guid Id) : ProducerRef(Id);
    public sealed record HumanCorrection(Guid Id, ProducerRef Source) : ProducerRef(Id);
    public string Kind => this switch { CommandExecution => "command", Composition => "composition", HumanCorrection => "correction" };
    [JsonIgnore] public Guid? SourceExecutionId => this switch
    {
        CommandExecution command => command.Id,
        Composition => null,
        HumanCorrection correction => correction.Source?.SourceExecutionId
    };
    [JsonIgnore] public Guid? SourceDraftId => this switch
    {
        CommandExecution => null,
        Composition composition => composition.Id,
        HumanCorrection correction => correction.Source?.SourceDraftId
    };
}
[method: JsonConstructor]
public sealed record OutputRef(Guid RunId, ProducerRef Producer, Guid ArtifactId, Guid VersionId, int Version)
{
    // Compatibility for existing command callers. Composite outputs deliberately
    // have no ExecutionId; a correction retains its original execution ancestry.
    public OutputRef(Guid runId, Guid executionId, Guid artifactId, Guid versionId, int version)
        : this(runId, new ProducerRef.CommandExecution(executionId), artifactId, versionId, version) { }
    public Guid? ExecutionId
    {
        get => Producer?.SourceExecutionId;
        init
        {
            // Legacy JSON includes this convenience field. Preserve correction
            // ancestry when it agrees; a changed ID still makes a tampered ref.
            if(value is Guid id && Producer?.SourceExecutionId != id)
                Producer = new ProducerRef.CommandExecution(id);
        }
    }
}
public sealed record TraceAnchor(string TraceId, string SpanId, bool Recorded, string? TraceState)
{
    public static TraceAnchor? From(ActivityContext context) => context.TraceId == default || context.SpanId == default
        ? null : new(context.TraceId.ToString(), context.SpanId.ToString(), context.TraceFlags.HasFlag(ActivityTraceFlags.Recorded), context.TraceState);

    public bool TryGetContext(out ActivityContext context) => ActivityContext.TryParse(
        $"00-{TraceId}-{SpanId}-{(Recorded ? "01" : "00")}", TraceState, out context);
}
public sealed record OutputArtifact(OutputRef Reference, JsonElement Value, string SchemaVersion, TraceAnchor? Origin, Guid? ParentVersionId);
public sealed record GradeRequest(Guid EventId, OutputRef Target, string Pointer, JsonElement ExpectedValue, double Score, string? Rationale = null, Guid? SupersedesEventId = null);
public sealed record CorrectionRequest(Guid EventId, OutputRef Target, string Pointer, JsonElement ExpectedValue, JsonElement Replacement);
public sealed record GradeEvent(Guid EventId, OutputRef Target, string Pointer, JsonElement ObservedValue, double Score, string? Rationale, string Grader, DateTimeOffset RecordedAt, Guid? SupersedesEventId, TraceAnchor? FeedbackTrace);
public sealed record CorrectionEvent(Guid EventId, OutputRef Target, OutputRef Corrected, string Pointer, JsonElement Before, JsonElement After, string Corrector, DateTimeOffset RecordedAt, TraceAnchor? FeedbackTrace);
public sealed record GradeReceipt(GradeEvent Event);
public sealed record CorrectionReceipt(CorrectionEvent Event, OutputArtifact Output);

public closed record LedgerResult<T>
{
    public sealed record Accepted(T Value, bool Replayed = false) : LedgerResult<T>;
    public sealed record Rejected(string Code, string Message) : LedgerResult<T>;
}

public interface IHumanFeedbackLedger
{
    Guid CreateRun(Actor actor);
    bool CanAccessRun(Guid runId, Actor actor) => false;
    LedgerResult<OutputArtifact> RecordOutput(Guid runId, Guid executionId, JsonElement value, string schemaVersion, ActivityContext origin, Actor actor);
    // A host adapter must implement the producer contract explicitly. A default
    // rejection would let all paid parts succeed before discovering that the
    // same ledger can never store their aggregate.
    LedgerResult<OutputArtifact> RecordProducedOutput(Guid runId, ProducerRef producer, JsonElement value, string schemaVersion, ActivityContext origin, Actor actor);
    LedgerResult<OutputArtifact> ReadOutput(OutputRef target, Actor actor);
    LedgerResult<GradeReceipt> RecordGrade(GradeRequest request, Actor actor);
    LedgerResult<CorrectionReceipt> SubmitCorrection(CorrectionRequest request, Actor actor);
}
