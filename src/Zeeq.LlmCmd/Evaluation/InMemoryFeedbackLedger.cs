using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zeeq.LlmCmd;

// Deliberately an in-memory ledger implementation. A database implementation must transact
// the event, output version, dedup key and outbox together.
/// <summary>Thread-safe in-memory implementation of the human feedback ledger.</summary>
/// <remarks>State is process-local and not durable. A durable implementation must atomically persist feedback, new versions, idempotency keys and any outbox.</remarks>
public sealed class InMemoryFeedbackLedger : IHumanFeedbackLedger
{
    /// <summary>Activity source for observational feedback tracing.</summary>
    public static readonly ActivitySource Activity = new("Zeeq.LlmCmd.Evaluation");
    readonly object gate = new();
    readonly Dictionary<Guid, Actor> runs = [];
    readonly Dictionary<Guid, OutputArtifact> outputs = [];
    readonly Dictionary<(Guid Run, Guid Event), (string Fingerprint, object Receipt)> events = [];

    /// <inheritdoc/>
    public Guid CreateRun(Actor actor)
    {
        if (!ValidActor(actor))
        {
            return Guid.Empty;
        }

        var id = Guid.NewGuid();
        lock (gate)
        {
            runs.Add(id, actor);
        }

        return id;
    }

    /// <inheritdoc/>
    public LedgerResult<OutputArtifact> RecordOutput(
        Guid runId,
        Guid executionId,
        JsonElement value,
        string schemaVersion,
        ActivityContext origin,
        Actor actor
    ) =>
        RecordProducedOutput(
            runId,
            new ProducerRef.CommandExecution(executionId),
            value,
            schemaVersion,
            origin,
            actor
        );

    /// <inheritdoc/>
    public bool CanAccessRun(Guid runId, Actor actor)
    {
        lock (gate)
        {
            return Authorized(runId, actor);
        }
    }

    /// <inheritdoc/>
    public LedgerResult<OutputArtifact> RecordProducedOutput(
        Guid runId,
        ProducerRef producer,
        JsonElement value,
        string schemaVersion,
        ActivityContext origin,
        Actor actor
    ) => RecordProducedOutputCore(runId, producer, value, schemaVersion, origin, actor, null);

    /// <inheritdoc/>
    public LedgerResult<OutputArtifact> RecordProducedOutputWithEvidence(
        Guid runId,
        ProducerRef producer,
        JsonElement value,
        string schemaVersion,
        ActivityContext origin,
        Actor actor,
        ExperimentEvidence evidence
    ) =>
        RecordProducedOutputCore(
            runId,
            producer,
            value,
            schemaVersion,
            origin,
            actor,
            evidence,
            requireEvidence: true
        );

    LedgerResult<OutputArtifact> RecordProducedOutputCore(
        Guid runId,
        ProducerRef producer,
        JsonElement value,
        string schemaVersion,
        ActivityContext origin,
        Actor actor,
        ExperimentEvidence? evidence,
        bool requireEvidence = false
    )
    {
        lock (gate)
        {
            if (!Authorized(runId, actor))
            {
                return Reject<OutputArtifact>("not_found", "Run not found");
            }

            if (
                producer is null
                || producer.Id == Guid.Empty
                || producer is ProducerRef.HumanCorrection
                || string.IsNullOrWhiteSpace(schemaVersion)
                || !JsonPointer.HasUniqueKeys(value)
                || requireEvidence && evidence is null
            )
            {
                return Reject<OutputArtifact>(
                    "invalid_output",
                    "Output needs a producer, schema version and unambiguous JSON"
                );
            }

            ExperimentEvidence? snapshot;
            try
            {
                snapshot = evidence is null ? null : SnapshotEvidence(evidence);
            }
            catch (Exception error)
                when (error
                        is ArgumentException
                            or InvalidOperationException
                            or NullReferenceException
                )
            {
                return Reject<OutputArtifact>(
                    "invalid_evidence",
                    "Evidence contains invalid values"
                );
            }

            var existing = outputs.Values.FirstOrDefault(o =>
                o.Reference.RunId == runId
                && o.Reference.Producer == producer
                && o.ParentVersionId is null
            );
            if (existing is not null)
            {
                return
                    existing.SchemaVersion == schemaVersion
                    && JsonElement.DeepEquals(existing.Value, value)
                    && EvidenceEquals(existing.Evidence, snapshot)
                    ? new LedgerResult<OutputArtifact>.Accepted(existing, true)
                    : Reject<OutputArtifact>(
                        "conflict",
                        "Execution already has a different original output"
                    );
            }

            var reference = new OutputRef(runId, producer, Guid.NewGuid(), Guid.NewGuid(), 1);
            var artifact = new OutputArtifact(
                reference,
                value.Clone(),
                schemaVersion,
                TraceAnchor.From(origin),
                null,
                snapshot
            );
            outputs.Add(reference.VersionId, artifact);
            return new LedgerResult<OutputArtifact>.Accepted(artifact);
        }
    }

    /// <inheritdoc/>
    public LedgerResult<OutputArtifact> ReadOutput(OutputRef target, Actor actor)
    {
        lock (gate)
        {
            return Find(target, actor) is { } output
                ? new LedgerResult<OutputArtifact>.Accepted(output)
                : Reject<OutputArtifact>("not_found", "Output version not found");
        }
    }

    /// <inheritdoc/>
    public LedgerResult<GradeReceipt> RecordGrade(GradeRequest request, Actor actor)
    {
        lock (gate)
        {
            if (request?.Target is null)
            {
                return Reject<GradeReceipt>("invalid_grade", "An exact output target is required");
            }

            var output = Find(request.Target, actor);
            if (output is null)
            {
                return Reject<GradeReceipt>("not_found", "Output version not found");
            }

            if (
                request.EventId == Guid.Empty
                || !double.IsFinite(request.Score)
                || request.Score is < 0 or > 1
            )
            {
                return Reject<GradeReceipt>(
                    "invalid_grade",
                    "Grade needs an event ID and a finite score in [0,1]"
                );
            }

            if (request.ExpectedValue.ValueKind == JsonValueKind.Undefined)
            {
                return Reject<GradeReceipt>("value_conflict", "Expected value is required");
            }

            var fingerprint = Fingerprint(request, actor);
            if (
                Replay<GradeReceipt>(request.Target.RunId, request.EventId, fingerprint) is
                { } replay
            )
            {
                return replay;
            }

            if (!Matches(output, request.Pointer, request.ExpectedValue, out var before))
            {
                return Reject<GradeReceipt>(
                    "value_conflict",
                    "Pointer or expected value does not match the selected version"
                );
            }

            if (
                request.SupersedesEventId is Guid supersedes
                && (
                    !events.TryGetValue((request.Target.RunId, supersedes), out var prior)
                    || prior.Receipt is not GradeReceipt grade
                    || grade.Event.Target != request.Target
                    || grade.Event.Pointer != request.Pointer
                    || grade.Event.Grader != actor.Subject
                )
            )
            {
                return Reject<GradeReceipt>(
                    "invalid_supersession",
                    "Previous grade must target this version and field and belong to this grader"
                );
            }

            using var activity = StartFeedback("grade", request.EventId, output, request.Pointer);
            activity?.AddEvent(
                new ActivityEvent(
                    "gen_ai.evaluation.result",
                    tags: new ActivityTagsCollection
                    {
                        ["gen_ai.evaluation.name"] = "human.field",
                        ["gen_ai.evaluation.score.value"] = request.Score,
                    }
                )
            );
            var item = new GradeEvent(
                request.EventId,
                request.Target,
                request.Pointer,
                before.Clone(),
                request.Score,
                request.Rationale,
                actor.Subject,
                DateTimeOffset.UtcNow,
                request.SupersedesEventId,
                TraceAnchor.From(activity?.Context ?? default)
            );
            var receipt = new GradeReceipt(item);
            events.Add((request.Target.RunId, request.EventId), (fingerprint, receipt));
            return new LedgerResult<GradeReceipt>.Accepted(receipt);
        }
    }

    /// <inheritdoc/>
    public LedgerResult<CorrectionReceipt> SubmitCorrection(CorrectionRequest request, Actor actor)
    {
        lock (gate)
        {
            if (request?.Target is null)
            {
                return Reject<CorrectionReceipt>(
                    "invalid_correction",
                    "An exact output target is required"
                );
            }

            var output = Find(request.Target, actor);
            if (output is null)
            {
                return Reject<CorrectionReceipt>("not_found", "Output version not found");
            }

            if (request.EventId == Guid.Empty || !JsonPointer.HasUniqueKeys(request.Replacement))
            {
                return Reject<CorrectionReceipt>(
                    "invalid_correction",
                    "Correction needs an event ID and unambiguous JSON replacement"
                );
            }

            if (request.ExpectedValue.ValueKind == JsonValueKind.Undefined)
            {
                return Reject<CorrectionReceipt>("value_conflict", "Expected value is required");
            }

            var fingerprint = Fingerprint(request, actor);
            if (
                Replay<CorrectionReceipt>(request.Target.RunId, request.EventId, fingerprint) is
                { } replay
            )
            {
                return replay;
            }

            if (
                !Matches(output, request.Pointer, request.ExpectedValue, out var before)
                || !JsonPointer.TryReplace(
                    output.Value,
                    request.Pointer,
                    request.Replacement,
                    out var corrected
                )
            )
            {
                return Reject<CorrectionReceipt>(
                    "value_conflict",
                    "Pointer or expected value does not match the selected version"
                );
            }
            // Concurrent corrections branch from the exact version, never overwrite 'latest'.
            var reference = output.Reference with
            {
                VersionId = Guid.NewGuid(),
                Version = output.Reference.Version + 1,
                Producer = new ProducerRef.HumanCorrection(
                    request.EventId,
                    output.Reference.Producer
                ),
            };
            var artifact = new OutputArtifact(
                reference,
                corrected,
                output.SchemaVersion,
                output.Origin,
                output.Reference.VersionId,
                output.Evidence
            );
            using var activity = StartFeedback(
                "correction",
                request.EventId,
                output,
                request.Pointer
            );
            activity?.SetTag("llmcmd.output.corrected_version_id", reference.VersionId.ToString());
            var item = new CorrectionEvent(
                request.EventId,
                request.Target,
                reference,
                request.Pointer,
                before.Clone(),
                request.Replacement.Clone(),
                actor.Subject,
                DateTimeOffset.UtcNow,
                TraceAnchor.From(activity?.Context ?? default)
            );
            var receipt = new CorrectionReceipt(item, artifact);
            outputs.Add(reference.VersionId, artifact);
            events.Add((request.Target.RunId, request.EventId), (fingerprint, receipt));
            return new LedgerResult<CorrectionReceipt>.Accepted(receipt);
        }
    }

    // Lists and JSON values must not remain owned by the caller after the atomic write.
    static ExperimentEvidence SnapshotEvidence(ExperimentEvidence evidence)
    {
        var parameters = evidence
            .Parameters?.Select(parameter =>
            {
                ArgumentNullException.ThrowIfNull(parameter);
                if (
                    parameter.Value.ValueKind == JsonValueKind.Undefined
                    || !JsonPointer.HasUniqueKeys(parameter.Value)
                )
                {
                    throw new ArgumentException("Invalid parameter JSON");
                }
                return parameter with { Value = parameter.Value.Clone() };
            })
            .ToArray();
        var attempts = evidence.Attempts?.ToArray();
        var members = evidence.Members?.ToArray();
        var compositionMembers = evidence.CompositionMembers?.ToArray();
        if (
            attempts?.Any(item => item is null) == true
            || members?.Any(item => item is null) == true
            || compositionMembers?.Any(item => item is null || item.Output is null) == true
        )
        {
            throw new ArgumentException("Invalid evidence collection");
        }
        return evidence with
        {
            Parameters = parameters is null ? null : Array.AsReadOnly(parameters),
            Attempts = attempts is null ? null : Array.AsReadOnly(attempts),
            Members = members is null ? null : Array.AsReadOnly(members),
            CompositionMembers = compositionMembers is null
                ? null
                : Array.AsReadOnly(compositionMembers),
        };
    }

    static bool EvidenceEquals(ExperimentEvidence? left, ExperimentEvidence? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }
        if (
            left with
            {
                Parameters = null,
                Attempts = null,
                Members = null,
                CompositionMembers = null,
            }
            != right with
            {
                Parameters = null,
                Attempts = null,
                Members = null,
                CompositionMembers = null,
            }
        )
        {
            return false;
        }
        if (
            !SequenceEquals(left.Attempts, right.Attempts)
            || !SequenceEquals(left.Members, right.Members)
            || !SequenceEquals(left.CompositionMembers, right.CompositionMembers)
        )
        {
            return false;
        }
        if (left.Parameters is null || right.Parameters is null)
        {
            return left.Parameters is null && right.Parameters is null;
        }
        return left.Parameters.Count == right.Parameters.Count
            && left.Parameters.Zip(right.Parameters)
                .All(pair =>
                    pair.First with { Value = default } == pair.Second with { Value = default }
                    && JsonElement.DeepEquals(pair.First.Value, pair.Second.Value)
                );
    }

    static bool SequenceEquals<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right) =>
        left is null || right is null ? left is null && right is null : left.SequenceEqual(right);

    static bool ValidActor(Actor? actor) =>
        actor is not null
        && !string.IsNullOrWhiteSpace(actor.Tenant)
        && !string.IsNullOrWhiteSpace(actor.Subject);

    bool Authorized(Guid runId, Actor actor) =>
        ValidActor(actor) && runs.TryGetValue(runId, out var owner) && owner == actor;

    OutputArtifact? Find(OutputRef? target, Actor actor) =>
        target is not null
        && Authorized(target.RunId, actor)
        && outputs.TryGetValue(target.VersionId, out var output)
        && output.Reference == target
            ? output
            : null;

    static LedgerResult<T> Reject<T>(string code, string message) =>
        new LedgerResult<T>.Rejected(code, message);

    LedgerResult<T>? Replay<T>(Guid runId, Guid eventId, string fingerprint)
    {
        if (!events.TryGetValue((runId, eventId), out var prior))
        {
            return null;
        }

        return prior.Fingerprint == fingerprint && prior.Receipt is T receipt
            ? new LedgerResult<T>.Accepted(receipt, true)
            : Reject<T>("idempotency_conflict", "Event ID already used for a different request");
    }

    static bool Matches(
        OutputArtifact output,
        string pointer,
        JsonElement expected,
        out JsonElement before
    )
    {
        before = default;
        return expected.ValueKind != JsonValueKind.Undefined
            && JsonPointer.TryGet(output.Value, pointer, out before)
            && JsonElement.DeepEquals(expected, before);
    }

    static string Fingerprint<T>(T request, Actor actor)
    {
        // Explicit wire fields keep feedback idempotency available when the
        // application disables reflection serialization for generated commands.
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", typeof(T).Name);
            writer.WriteString("tenant", actor.Tenant);
            writer.WriteString("subject", actor.Subject);
            switch (request)
            {
                case GradeRequest grade:
                    Target(grade.Target);
                    writer.WriteString("pointer", grade.Pointer);
                    writer.WritePropertyName("expected");
                    grade.ExpectedValue.WriteTo(writer);
                    writer.WriteNumber("score", grade.Score);
                    writer.WriteString("rationale", grade.Rationale);
                    if (grade.SupersedesEventId is Guid supersedes)
                    {
                        writer.WriteString("supersedes", supersedes);
                    }

                    break;
                case CorrectionRequest correction:
                    Target(correction.Target);
                    writer.WriteString("pointer", correction.Pointer);
                    writer.WritePropertyName("expected");
                    correction.ExpectedValue.WriteTo(writer);
                    writer.WritePropertyName("replacement");
                    correction.Replacement.WriteTo(writer);
                    break;
                default:
                    throw new InvalidOperationException("Unsupported feedback request");
            }
            writer.WriteEndObject();
            void Target(OutputRef target)
            {
                writer.WriteString("run", target.RunId);
                writer.WriteString("artifact", target.ArtifactId);
                writer.WriteString("version", target.VersionId);
                writer.WriteNumber("revision", target.Version);
                Producer(target.Producer);
            }
            void Producer(ProducerRef producer)
            {
                writer.WriteStartArray("producer");
                writer.WriteStringValue(producer.Kind);
                writer.WriteStringValue(producer.Id);
                if (producer is ProducerRef.HumanCorrection correction)
                {
                    writer.WriteStartObject();
                    Producer(correction.Source);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
        }
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }

    static Activity? StartFeedback(string kind, Guid eventId, OutputArtifact output, string pointer)
    {
        var links =
            output.Origin is { } origin && origin.TryGetContext(out var context)
                ? new[] { new ActivityLink(context) }
                : [];
        return Activity.StartActivity(
            "llmcmd.human." + kind,
            ActivityKind.Internal,
            default(ActivityContext),
            tags: new ActivityTagsCollection
            {
                ["llmcmd.run.id"] = output.Reference.RunId.ToString(),
                ["llmcmd.execution.id"] = output.Reference.ExecutionId?.ToString(),
                ["llmcmd.output.producer.kind"] = output.Reference.Producer.Kind,
                ["llmcmd.output.producer.id"] = output.Reference.Producer.Id.ToString(),
                ["llmcmd.output.draft_id"] = output.Reference.Producer.SourceDraftId?.ToString(),
                ["llmcmd.output.version_id"] = output.Reference.VersionId.ToString(),
                ["llmcmd.feedback.id"] = eventId.ToString(),
                ["llmcmd.feedback.kind"] = kind,
                ["llmcmd.feedback.pointer"] = pointer,
            },
            links: links
        );
    }
}
