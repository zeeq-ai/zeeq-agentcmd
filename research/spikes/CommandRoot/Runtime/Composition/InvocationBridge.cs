using HumanGrading;
using LlmCmd.ContextPropagation;
namespace CommandRoot;

// WHY one seam file: every coupling between composition and the leaf command runtime lives here. The leaf `Outcome<T>`
// shape and root's internal composition entry points (CompositionBridge.cs) can change without touching the engine.

enum ComponentKind { Stored, NotStored, Failed, Cancelled }

/// <summary>Leaf result normalized to what composition needs. Never carries the typed value: the stored wire JSON is authoritative.</summary>
sealed record ComponentRun(ComponentKind Kind, OutputRef? Output, Guid? ExecutionId, string? Code, CallSite? Caller,
    ExecutionFailure? Failure = null, ExecutionSnapshot? Snapshot = null, OutputDelivery? Delivery = null, CommandExecutionContext? Context = null);

abstract class PreparedCall
{
    /// <summary>Hash of command, definition, profile, rendered prompt and parameters. Replay with another fingerprint is a conflict.</summary>
    public abstract string Fingerprint { get; }
    /// <summary>The ledger the leaf command stores into. The engine reads and seals through ITS ledger, so the two must be the same instance.</summary>
    public abstract object Ledger { get; }
    /// <summary>The leaf's result serializer metadata. Compared with the aggregate's metadata for the same part before any claim.</summary>
    public abstract System.Text.Json.Serialization.Metadata.JsonTypeInfo ResultContract { get; }
    public abstract Task<ComponentRun> ExecuteAsync(OperationContext context, Guid executionId, string slotId, Guid stepId,
        CallSite site, IExecutionJournal? journal, CancellationToken ct);
}

sealed class PreparedCall<TP, TR>(Invocation<TP, TR> frozen) : PreparedCall
{
    public override string Fingerprint => frozen.GetCompositionIdentity().Fingerprint;
    public override object Ledger => frozen.RuntimeOwner.CompositionLedger;
    public override System.Text.Json.Serialization.Metadata.JsonTypeInfo ResultContract => frozen.ResultContract;

    public override async Task<ComponentRun> ExecuteAsync(OperationContext context, Guid executionId, string slotId, Guid stepId,
        CallSite site, IExecutionJournal? journal, CancellationToken ct)
    {
        // WHY ExecuteComponentAsync and not InContext(...).ExecuteAsync: the slot supplies one whole context. An invocation that
        // already names another run or draft is refused as a configuration failure instead of being silently re-pointed.
        // WHY an explicit CallSite: the deferred call must report the application line where the slot was registered.
        var outcome = await frozen.ExecuteComponentAsync(context, executionId, slotId, stepId, site, journal, ct).ConfigureAwait(false);
        return Normalize(outcome);
    }

    // WHY keep Failure, Snapshot, Delivery and Context: composition must not flatten the leaf's own typed result into a string.
    static ComponentRun Normalize(Outcome<TR> outcome) => outcome switch
    {
        Outcome<TR>.Success s => s.Delivery switch
        {
            // Only a stored artifact can fill a slot. A value that was parsed but not stored is NOT stored evidence.
            OutputDelivery.Stored stored => new(ComponentKind.Stored, stored.Output.Reference, s.ExecutionId, null, s.Caller, null, s.Snapshot, s.Delivery, s.Context),
            OutputDelivery.Rejected r => new(ComponentKind.NotStored, null, s.ExecutionId, "delivery_rejected:" + r.Code, s.Caller, null, s.Snapshot, s.Delivery, s.Context),
            OutputDelivery.Failed => new(ComponentKind.NotStored, null, s.ExecutionId, "delivery_failed", s.Caller, null, s.Snapshot, s.Delivery, s.Context)
        },
        Outcome<TR>.Failed f => f.Failure is ExecutionFailure.Cancelled
            ? new(ComponentKind.Cancelled, null, f.ExecutionId, "cancelled", null, f.Failure, f.Failure.Snapshot, null, f.Failure.Context)
            : new(ComponentKind.Failed, null, f.ExecutionId, f.Failure.Kind, null, f.Failure, f.Failure.Snapshot, null, f.Failure.Context)
    };
}

static class InvocationBridge
{
    // WHY freeze at preparation: the invocation's parameters may hold mutable collections. Freezing captures the validated,
    // rendered and projected inputs once, so what is fingerprinted is exactly what runs.
    internal static PreparedCall Prepare<TP, TR>(Invocation<TP, TR> invocation) => new PreparedCall<TP, TR>(invocation.FreezeForComposition());
}

/// <summary>
/// Awaited journal for one claimed attempt. The leaf reserves attempt.started here BEFORE any provider send; if this write fails the
/// send does not happen. If the post-send attempt.finished write fails, the result is kept but must not be attached:
/// the attempt's authoritative completion is missing, so the slot stays Pending (indeterminate).
/// </summary>
sealed class SlotJournal(ICompositionStore store, Guid draftId, string slotId, Guid stepId) : IExecutionJournal
{
    volatile bool completionLost, started;
    public bool CompletionLost => completionLost;
    /// <summary>True once attempt.started was stored for this step. After that point a provider request may have been sent.</summary>
    public bool Started => started;

    public async ValueTask AppendAsync(ExecutionEvent record, CancellationToken cancellationToken)
    {
        var finished = record.Kind.EndsWith(".finished", StringComparison.Ordinal);
        try
        {
            var detail = $"attempt={record.AttemptId};n={record.AttemptNumber};provider={record.Provider};outcome={record.Outcome};in={record.InputTokens};out={record.OutputTokens}";
            var result = await store.AppendEventAsync(new(draftId, slotId, stepId, "journal." + record.Kind, record.Context.ExecutionId, detail), cancellationToken).ConfigureAwait(false);
            if (result is not TransitionResult.Applied) throw new InvalidOperationException("journal_rejected");
            if (!finished && record.Kind.EndsWith(".started", StringComparison.Ordinal)) started = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !finished) { throw; }
        catch { if (finished) completionLost = true; throw; }
    }
}
