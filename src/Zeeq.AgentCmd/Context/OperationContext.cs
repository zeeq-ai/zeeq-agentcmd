using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Zeeq.AgentCmd;

/// <summary>Identifies the exact composition definition and generation associated with an operation.</summary>
/// <param name="DraftId">Stable composition draft identifier.</param>
/// <param name="DefinitionId">Composition definition key.</param>
/// <param name="Revision">Definition revision.</param>
/// <param name="DefinitionHash">Hash of the definition used by this draft.</param>
/// <param name="Generation">Generation of the draft.</param>
/// <param name="InputRevision">Caller-assigned revision of the input.</param>
public sealed record DraftReference(Guid DraftId, string DefinitionId, int Revision,
    string DefinitionHash, int Generation, string InputRevision);
/// <summary>Portable correlation reference; it is not an authorization grant.</summary>
/// <param name="ContextId">Unique correlation context.</param>
/// <param name="RunId">Run that owns the operation.</param>
/// <param name="Draft">Optional composition draft identity.</param>
/// <param name="TrialId">Optional trial identifier.</param>
public sealed record OperationContextReference(Guid ContextId, Guid RunId,
    DraftReference? Draft = null, string? TrialId = null);

/// <summary>Local correlation only. A receiving resolver and each work claim still authorize the actor.</summary>
public sealed class OperationContext
{
    /// <summary>Immutable run and optional draft correlation.</summary>
    public OperationContextReference Reference
    {
        get;
    }
    /// <summary>Optional identifier of the work item within the operation.</summary>
    public Guid? WorkId
    {
        get;
    }
    private OperationContext(OperationContextReference reference, Guid? workId)
    {
        Reference = reference;
        WorkId = workId;
    }

    /// <summary>Creates a new correlation context for a run; it does not authorize access.</summary>
    public static OperationContext ForRun(Guid runId) => FromResolvedReference(new(Guid.NewGuid(), runId));

    // Public so a host-owned resolver can construct it. This factory is not evidence of authorization.
    /// <summary>Creates local context from a structurally valid resolved reference; the caller must separately authorize it.</summary>
    public static OperationContext FromResolvedReference(OperationContextReference reference, Guid? workId = null)
    {
        if (!OperationContextValidation.IsValid(reference) || workId == Guid.Empty)
        {
            throw new ArgumentException("Invalid operation reference");
        }

        return new(reference, workId);
    }
}

/// <summary>How the current execution selected its operation context.</summary>
public enum ContextSource
{
    /// <summary>No context was supplied or active.</summary>
    None,
    /// <summary>The flow-local scope supplied the context.</summary>
    Ambient,
    /// <summary>The invocation supplied the context explicitly.</summary>
    Explicit,
    /// <summary>Context propagation was deliberately suppressed.</summary>
    Suppressed
}
/// <summary>Frozen context selected at invocation time, safe to pass to delayed observers.</summary>
/// <param name="Reference">Selected operation reference, if any.</param>
/// <param name="WorkId">Selected work identifier, if any.</param>
/// <param name="Source">How the context was selected.</param>
/// <param name="AmbientOverridden">Whether an explicit context or suppression replaced an ambient frame.</param>
public sealed record CapturedContext(OperationContextReference? Reference, Guid? WorkId,
    ContextSource Source, bool AmbientOverridden)
{
    /// <summary>Run identifier from the selected reference, when present.</summary>
    public Guid? RunId => Reference?.RunId;
    /// <summary>Draft identifier from the selected reference, when present.</summary>
    public Guid? DraftId => Reference?.Draft?.DraftId;
}

/// <summary>Identity and captured operation context for one command execution.</summary>
/// <param name="ExecutionId">Unique execution identifier.</param>
/// <param name="CommandId">Stable command definition key.</param>
/// <param name="Revision">Command definition revision.</param>
/// <param name="DefinitionHash">Hash identifying the command definition.</param>
/// <param name="Operation">Context frozen when execution began.</param>
/// <param name="SlotId">Optional composition slot receiving the result.</param>
/// <param name="StepId">Optional composition step identifier.</param>
public sealed record CommandExecutionContext(Guid ExecutionId, string CommandId, int Revision,
    string DefinitionHash, CapturedContext Operation, string? SlotId = null, Guid? StepId = null)
{
    /// <summary>Adds correlation tags to an activity when one is recording.</summary>
    public void Tag(Activity? activity)
    {
        activity?.SetTag("agentcmd.execution.id", ExecutionId.ToString());
        activity?.SetTag("agentcmd.run.id", Operation.RunId?.ToString());
        activity?.SetTag("agentcmd.output.draft_id", Operation.DraftId?.ToString());
        activity?.SetTag("agentcmd.work.id", Operation.WorkId?.ToString());
        activity?.SetTag("agentcmd.context.source", Operation.Source.ToString());
        activity?.SetTag("agentcmd.output.slot_id", SlotId);
        activity?.SetTag("agentcmd.step.id", StepId?.ToString());
    }
}

/// <summary>Immutable flow-local frames. No actor, grants or component slot lives here.</summary>
public static class AgentCmdContext
{
    sealed record Frame(OperationContext? Context, Frame? Parent);
    static readonly AsyncLocal<Frame?> current = new();
    /// <summary>Current flow-local context, or null when absent or suppressed.</summary>
    public static OperationContext? Current => current.Value?.Context;

    /// <summary>Pushes a context for the current async flow until the returned scope is disposed.</summary>
    public static IDisposable BeginScope(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Push(context);
    }
    /// <summary>Temporarily suppresses an ambient context in the current async flow.</summary>
    public static IDisposable Suppress() => Push(null);
    static IDisposable Push(OperationContext? context)
    {
        var frame = new Frame(context, current.Value);
        current.Value = frame;
        return new Scope(frame);
    }
    sealed class Scope(Frame frame) : IDisposable
    {
        public void Dispose()
        {
            // No shared disposed flag: a child flow can retain its inherited frame.
            // Out-of-order disposal is a no-op. A later valid LIFO disposal can restore it.
            if (ReferenceEquals(current.Value, frame))
            {
                current.Value = frame.Parent;
            }
        }
    }

    /// <summary>Freezes the explicit or ambient context, or records deliberate suppression.</summary>
    public static CapturedContext Capture(OperationContext? explicitContext = null, bool suppress = false)
    {
        var ambient = current.Value;
        if (suppress)
        {
            return new(null, null, ContextSource.Suppressed, ambient is not null);
        }

        if (explicitContext is not null)
        {
            return new(explicitContext.Reference, explicitContext.WorkId, ContextSource.Explicit, ambient is not null);
        }

        return ambient is null ? new(null, null, ContextSource.None, false)
            : new(ambient.Context?.Reference, ambient.Context?.WorkId,
                ambient.Context is null ? ContextSource.Suppressed : ContextSource.Ambient, false);
    }
}

/// <summary>Base for immutable execution and composition observation events.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$event")]
[JsonDerivedType(typeof(ExecutionEvent), "execution")]
[JsonDerivedType(typeof(CompositionEvent), "composition")]
public closed record LlmEvent
{
    // Emitters receive the captured value. Delayed delivery must not read the
    // scope of the delivery worker or mistake a root for a model execution.
    /// <summary>Operation context captured by the producer, not the observer.</summary>
    public abstract CapturedContext Operation
    {
        get;
    }
}

/// <summary>Observation of a command or provider attempt.</summary>
/// <param name="Kind">Event kind within the command lifecycle.</param>
/// <param name="Context">Captured command execution identity.</param>
/// <param name="AttemptId">Provider attempt identifier, when applicable.</param>
/// <param name="AttemptNumber">One-based provider attempt number, when applicable.</param>
/// <param name="Provider">Provider registration identifier, when known.</param>
/// <param name="Outcome">Outcome category, when known.</param>
/// <param name="InputTokens">Reported input tokens, when known.</param>
/// <param name="OutputTokens">Reported output tokens, when known.</param>
public sealed record ExecutionEvent(string Kind, CommandExecutionContext Context,
    Guid? AttemptId = null, int? AttemptNumber = null, string? Provider = null,
    string? Outcome = null, long? InputTokens = null, long? OutputTokens = null) : LlmEvent
{
    /// <inheritdoc />
    public override CapturedContext Operation => Context.Operation;
}

/// <summary>Observation of a composition root, step, or publication.</summary>
/// <param name="Kind">Event kind within the composition lifecycle.</param>
/// <param name="Context">Captured composition identity.</param>
/// <param name="Outcome">Outcome category, when known.</param>
/// <param name="Code">Optional machine-readable detail code.</param>
public sealed record CompositionEvent(string Kind, CompositionEventContext Context,
    string? Outcome = null, string? Code = null) : LlmEvent
{
    /// <inheritdoc />
    public override CapturedContext Operation => Context.Operation;
}

// A skipped step and a root are real operations, but neither is an LLM call.
// Nullable ExecutionId prevents invented call IDs from corrupting provenance.
// OutboxSequence orders composition events only, not events from other journals.
/// <summary>Captured composition provenance; roots and skipped steps need not have execution IDs.</summary>
/// <param name="Operation">Captured run context.</param>
/// <param name="DefinitionId">Composition definition key.</param>
/// <param name="IdempotencyKey">Optional key for deduplicating the request.</param>
/// <param name="SlotId">Slot involved in the event, if any.</param>
/// <param name="StepId">Step involved in the event, if any.</param>
/// <param name="ExecutionId">Actual command execution, if one occurred.</param>
/// <param name="ArtifactId">Output artifact identifier, if published.</param>
/// <param name="VersionId">Exact output version identifier, if published.</param>
/// <param name="OutboxSequence">Sequence within the composition outbox, when available.</param>
public sealed record CompositionEventContext(CapturedContext Operation, string DefinitionId,
    string? IdempotencyKey = null, string? SlotId = null, Guid? StepId = null,
    Guid? ExecutionId = null, Guid? ArtifactId = null, Guid? VersionId = null,
    long? OutboxSequence = null);

/// <summary>Best-effort observer, not a durable journal. Implementations must not read ambient state.</summary>
public interface IExecutionEventSink
{
    /// <summary>Observes a captured event. Observer failure must not control execution.</summary>
    ValueTask EmitAsync(LlmEvent record, CancellationToken cancellationToken);
}

// This awaited port gates provider sends. It is distinct from telemetry, whose
// failure must never decide whether a model request is safe to start or replay.
/// <summary>Durable execution journal whose availability gates provider sends.</summary>
public interface IExecutionJournal
{
    /// <summary>Appends a lifecycle event before or after the corresponding provider attempt.</summary>
    ValueTask AppendAsync(ExecutionEvent record, CancellationToken cancellationToken);
}
/// <summary>The execution journal could not persist a required event.</summary>
public sealed class ExecutionJournalUnavailableException : Exception
{
    /// <summary>Creates a sanitized journal failure without exposing the underlying exception.</summary>
    public ExecutionJournalUnavailableException() : base("Execution journal unavailable") { }
}
/// <summary>Applies the execution journal failure and cancellation policy.</summary>
public static class ExecutionJournal
{
    /// <summary>Appends when configured, propagating caller cancellation and converting other failures to a sanitized journal error.</summary>
    public static async ValueTask AppendAsync(IExecutionJournal? journal, ExecutionEvent record, CancellationToken ct)
    {
        if (journal is null)
        {
            return;
        }

        try
        {
            await journal.AppendAsync(record, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { throw new ExecutionJournalUnavailableException(); }
    }
}

/// <summary>Delivers best-effort execution observations without affecting results.</summary>
public static class ExecutionEvents
{
    /// <summary>Delivers an event with a bounded wait; observer failures do not affect the command.</summary>
    public static async ValueTask EmitSafelyAsync(IExecutionEventSink? sink, LlmEvent record)
    {
        if (sink is null)
        {
            return;
        }

        Task? delivery = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            delivery = sink.EmitAsync(record, deadline.Token).AsTask();
            await delivery.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch { /* Observer failure cannot replace a provider result. Durable storage is a separate port. */ }
        finally
        {
            if (delivery is { IsCompleted: false })
            {
                _ = delivery.ContinueWith(t => { _ = t.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }
}

/// <summary>Structural validation for operation references and wire identifiers. This does not authorize work.</summary>
public static class OperationContextValidation
{
    /// <summary>Checks the supported bounded ASCII identifier alphabet.</summary>
    public static bool IsValidKey(string? value, int max = 128) => !string.IsNullOrEmpty(value) && value.Length <= max
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':');
    /// <summary>Checks identifiers and draft fields structurally; it does not authorize access.</summary>
    public static bool IsValid(OperationContextReference? r) => r is not null && r.ContextId != Guid.Empty
        && r.RunId != Guid.Empty && (r.TrialId is null || IsValidKey(r.TrialId))
        && (r.Draft is null || r.Draft.DraftId != Guid.Empty && IsValidKey(r.Draft.DefinitionId)
            && r.Draft.Revision > 0 && IsValidKey(r.Draft.DefinitionHash, 256)
            && r.Draft.Generation > 0 && IsValidKey(r.Draft.InputRevision, 256));
}
