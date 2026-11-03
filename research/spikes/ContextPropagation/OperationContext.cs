using System.Diagnostics;
using System.Text.Json.Serialization;

namespace LlmCmd.ContextPropagation;

public sealed record DraftReference(Guid DraftId, string DefinitionId, int Revision,
    string DefinitionHash, int Generation, string InputRevision);
public sealed record OperationContextReference(Guid ContextId, Guid RunId,
    DraftReference? Draft = null, string? TrialId = null);

/// <summary>Local correlation only. A receiving resolver and each work claim still authorize the actor.</summary>
public sealed class OperationContext
{
    public OperationContextReference Reference { get; }
    public Guid? WorkId { get; }
    private OperationContext(OperationContextReference reference, Guid? workId)
    { Reference = reference; WorkId = workId; }

    public static OperationContext ForRun(Guid runId) => FromResolvedReference(new(Guid.NewGuid(), runId));

    // Public so a host-owned resolver can construct it. This factory is not evidence of authorization.
    public static OperationContext FromResolvedReference(OperationContextReference reference, Guid? workId = null)
    {
        if (!ContextValidation.Valid(reference) || workId == Guid.Empty)
            throw new ArgumentException("Invalid operation reference");
        return new(reference, workId);
    }
}

public enum ContextSource { None, Ambient, Explicit, Suppressed }
public sealed record CapturedContext(OperationContextReference? Reference, Guid? WorkId,
    ContextSource Source, bool AmbientOverridden)
{
    public Guid? RunId => Reference?.RunId;
    public Guid? DraftId => Reference?.Draft?.DraftId;
}

public sealed record CommandExecutionContext(Guid ExecutionId, string CommandId, int Revision,
    string DefinitionHash, CapturedContext Operation, string? SlotId = null, Guid? StepId = null)
{
    public void Tag(Activity? activity)
    {
        activity?.SetTag("llmcmd.execution.id", ExecutionId.ToString());
        activity?.SetTag("llmcmd.run.id", Operation.RunId?.ToString());
        activity?.SetTag("llmcmd.output.draft_id", Operation.DraftId?.ToString());
        activity?.SetTag("llmcmd.work.id", Operation.WorkId?.ToString());
        activity?.SetTag("llmcmd.context.source", Operation.Source.ToString());
        activity?.SetTag("llmcmd.output.slot_id", SlotId);
        activity?.SetTag("llmcmd.step.id", StepId?.ToString());
    }
}

/// <summary>Immutable flow-local frames. No actor, grants or component slot lives here.</summary>
public static class LlmCmdContext
{
    sealed record Frame(OperationContext? Context, Frame? Parent);
    static readonly AsyncLocal<Frame?> current = new();
    public static OperationContext? Current => current.Value?.Context;

    public static IDisposable BeginScope(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Push(context);
    }
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
            if (ReferenceEquals(current.Value, frame)) current.Value = frame.Parent;
        }
    }

    public static CapturedContext Capture(OperationContext? explicitContext = null, bool suppress = false)
    {
        var ambient = current.Value;
        if (suppress) return new(null, null, ContextSource.Suppressed, ambient is not null);
        if (explicitContext is not null)
            return new(explicitContext.Reference, explicitContext.WorkId, ContextSource.Explicit, ambient is not null);
        return ambient is null ? new(null, null, ContextSource.None, false)
            : new(ambient.Context?.Reference, ambient.Context?.WorkId,
                ambient.Context is null ? ContextSource.Suppressed : ContextSource.Ambient, false);
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$event")]
[JsonDerivedType(typeof(ExecutionEvent), "execution")]
[JsonDerivedType(typeof(CompositionEvent), "composition")]
public closed record LlmEvent
{
    // Emitters receive the captured value. Delayed delivery must not read the
    // scope of the delivery worker or mistake a root for a model execution.
    public abstract CapturedContext Operation { get; }
}

public sealed record ExecutionEvent(string Kind, CommandExecutionContext Context,
    Guid? AttemptId = null, int? AttemptNumber = null, string? Provider = null,
    string? Outcome = null, long? InputTokens = null, long? OutputTokens = null) : LlmEvent
{
    public override CapturedContext Operation => Context.Operation;
}

public sealed record CompositionEvent(string Kind, CompositionEventContext Context,
    string? Outcome = null, string? Code = null) : LlmEvent
{
    public override CapturedContext Operation => Context.Operation;
}

// A skipped step and a root are real operations, but neither is an LLM call.
// Nullable ExecutionId prevents invented call IDs from corrupting provenance.
// OutboxSequence orders composition events only, not events from other journals.
public sealed record CompositionEventContext(CapturedContext Operation, string DefinitionId,
    string? IdempotencyKey = null, string? SlotId = null, Guid? StepId = null,
    Guid? ExecutionId = null, Guid? ArtifactId = null, Guid? VersionId = null,
    long? OutboxSequence = null);

/// <summary>Best-effort observer, not a durable journal. Implementations must not read ambient state.</summary>
public interface IExecutionEventSink
{
    ValueTask EmitAsync(LlmEvent record, CancellationToken cancellationToken);
}

// This awaited port gates provider sends. It is distinct from telemetry, whose
// failure must never decide whether a model request is safe to start or replay.
public interface IExecutionJournal
{
    ValueTask AppendAsync(ExecutionEvent record, CancellationToken cancellationToken);
}
public sealed class ExecutionJournalUnavailableException : Exception
{
    public ExecutionJournalUnavailableException() : base("Execution journal unavailable") { }
}
public static class ExecutionJournal
{
    public static async ValueTask AppendAsync(IExecutionJournal? journal, ExecutionEvent record, CancellationToken ct)
    {
        if(journal is null) return;
        try { await journal.AppendAsync(record,ct).ConfigureAwait(false); }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
        catch { throw new ExecutionJournalUnavailableException(); }
    }
}

public static class ExecutionEvents
{
    public static async ValueTask EmitSafelyAsync(IExecutionEventSink? sink, LlmEvent record)
    {
        if (sink is null) return;
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
            if (delivery is { IsCompleted: false }) _ = delivery.ContinueWith(t => { _ = t.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}

internal static class ContextValidation
{
    internal static bool Key(string? value, int max = 128) => !string.IsNullOrEmpty(value) && value.Length <= max
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':');
    internal static bool Valid(OperationContextReference? r) => r is not null && r.ContextId != Guid.Empty
        && r.RunId != Guid.Empty && (r.TrialId is null || Key(r.TrialId))
        && (r.Draft is null || r.Draft.DraftId != Guid.Empty && Key(r.Draft.DefinitionId)
            && r.Draft.Revision > 0 && Key(r.Draft.DefinitionHash, 256)
            && r.Draft.Generation > 0 && Key(r.Draft.InputRevision, 256));
}
