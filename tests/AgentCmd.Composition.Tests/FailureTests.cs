using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd;

namespace AgentCmd.Composition.Tests;

// Money and truth: what happens when the leaf, the ledger, the journal or the store fails.
public class FailureTests
{
    [Test]
    public async Task Leaf_failure_is_forwarded_untouched_with_context_and_snapshot()
    {
        using var rig = new Rig();
        rig.Client.Throws.Add("TOTALS");
        var incomplete = Check.Is<CompositeOutcome<Doc>.Incomplete>(
            await rig.AddAll(await rig.Begin()).ExecuteAsync()
        );
        var step = incomplete.Steps.Single(s => s.SlotId == "totals");

        await Assert.That(step.Status).IsEqualTo(StepStatus.Failed);
        await Assert.That(step.Failure).IsTypeOf<ExecutionFailure.ProviderFailure>();
        await Assert.That(step.Snapshot).IsNotNull();
        await Assert.That(step.Snapshot!.PromptId).IsEqualTo("totals.extract");
        await Assert.That(step.Context!.SlotId).IsEqualTo("totals");
        await Assert.That(step.Context.StepId).IsEqualTo(step.StepId);
        await Assert.That(step.Context.Operation.DraftId).IsEqualTo(incomplete.DraftId);
        await Assert.That(step.ExecutionId).IsEqualTo(step.Context.ExecutionId);
        await Assert.That(step.DraftId).IsEqualTo(incomplete.DraftId);
        var slot = (await rig.DraftAsync(incomplete.DraftId)).Slots["totals"];
        await Assert.That(slot.Status).IsEqualTo(SlotStatus.Failed);
        await Assert.That(slot.Output).IsNull();
    }

    [Test]
    public async Task A_stored_delivery_failure_never_fills_the_slot()
    {
        using var rig = new Rig();
        rig.Ledger.OnRecordOutput = _ => new LedgerResult<OutputArtifact>.Rejected(
            "quota",
            "scripted"
        );
        var incomplete = Check.Is<CompositeOutcome<Doc>.Incomplete>(
            await rig.AddAll(await rig.Begin()).ExecuteAsync()
        );

        await Assert
            .That(
                incomplete.Steps.All(s =>
                    s.Status == StepStatus.Failed && s.Code == "delivery_rejected:quota"
                )
            )
            .IsTrue();
        await Assert
            .That(incomplete.Steps.All(s => s.Delivery is OutputDelivery.Rejected))
            .IsTrue();
        var draft = await rig.DraftAsync(incomplete.DraftId);
        await Assert
            .That(draft.Registered().All(s => s.Status == SlotStatus.Failed && s.Output is null))
            .IsTrue();
        await Assert.That(draft.Status).IsEqualTo(DraftStatus.Open);

        // The ledger recovers; a new handle fills every slot and counts a second attempt.
        rig.Ledger.OnRecordOutput = null;
        var pub = Check.Published(await rig.AddAll(await rig.Begin()).ExecuteAsync());
        await Assert
            .That(
                (await rig.DraftAsync(pub.DraftId)).Slots.Values.Count(s =>
                    s.Attempts == 2 && s.Status == SlotStatus.Attached
                )
            )
            .IsEqualTo(4);
    }

    [Test]
    public async Task A_claim_that_never_resolved_is_pending_and_is_not_run_again()
    {
        using var rig = new Rig();
        var root = rig.AddAll(await rig.Begin());
        // Simulate a crash after claim: the store says Claimed, nobody will ever resolve it.
        var crashed = Guid.NewGuid();
        await rig.RealStore.ClaimSlotAsync(
            new(root.DraftId!.Value, "header", Guid.NewGuid(), MemberProvenance.Executed, crashed),
            default
        );

        var incomplete = Check.Is<CompositeOutcome<Doc>.Incomplete>(await root.ExecuteAsync());
        var header = incomplete.Steps.Single(s => s.SlotId == "header");
        await Assert.That(header.Status).IsEqualTo(StepStatus.Pending);
        await Assert.That(header.Code).IsEqualTo("claimed_unresolved");
        // The reserved execution id survives, so an operator can look up what the provider charged.
        await Assert.That(header.ExecutionId).IsEqualTo(crashed);
        await Assert.That(rig.Client.CallsFor("HEADER")).IsEqualTo(0);
        await Assert
            .That(incomplete.Steps.Count(s => s.Status == StepStatus.Attached))
            .IsEqualTo(3);
    }

    [Test]
    public async Task Attach_failure_after_spend_leaves_the_slot_pending_and_does_not_rerun()
    {
        using var rig = new Rig();
        rig.Store.FailAttach = true;
        var unavailable = Check.Is<CompositeOutcome<Doc>.Unavailable>(
            await rig.AddAll(await rig.Begin()).ExecuteAsync()
        );
        await Assert
            .That(
                unavailable.Steps.All(s =>
                    s.Status == StepStatus.Pending
                    && s.Code == "attach_failed"
                    && s.Output is not null
                )
            )
            .IsTrue();
        await Assert.That(rig.Client.Total).IsEqualTo(4);

        rig.Store.FailAttach = false;
        var again = Check.Is<CompositeOutcome<Doc>.Incomplete>(
            await rig.AddAll(await rig.Begin()).ExecuteAsync()
        );
        await Assert
            .That(
                again.Steps.All(s =>
                    s.Status == StepStatus.Pending && s.Code == "claimed_unresolved"
                )
            )
            .IsTrue();
        await Assert.That(rig.Client.Total).IsEqualTo(4);
    }

    [Test]
    public async Task Journal_reservation_failure_stops_the_provider_send()
    {
        using var rig = new Rig();
        rig.Store.FailAppend = e =>
            e.SlotId == "header" && e.Kind.EndsWith("attempt.started", StringComparison.Ordinal);
        var incomplete = Check.Is<CompositeOutcome<Doc>.Incomplete>(
            await rig.AddAll(await rig.Begin()).ExecuteAsync()
        );
        var header = incomplete.Steps.Single(s => s.SlotId == "header");
        await Assert.That(header.Status).IsEqualTo(StepStatus.Failed);
        await Assert.That(header.Failure).IsTypeOf<ExecutionFailure.ConfigurationFailure>();
        await Assert.That(rig.Client.CallsFor("HEADER")).IsEqualTo(0);
        await Assert
            .That(incomplete.Steps.Count(s => s.Status == StepStatus.Attached))
            .IsEqualTo(3);
    }

    [Test]
    public async Task Lost_completion_record_keeps_the_output_but_does_not_attach_it()
    {
        using var rig = new Rig();
        rig.Store.FailAppend = e =>
            e.SlotId == "totals" && e.Kind.EndsWith("attempt.finished", StringComparison.Ordinal);
        var unavailable = Check.Is<CompositeOutcome<Doc>.Unavailable>(
            await rig.AddAll(await rig.Begin()).ExecuteAsync()
        );
        var totals = unavailable.Steps.Single(s => s.SlotId == "totals");

        await Assert.That(totals.Status).IsEqualTo(StepStatus.Pending);
        await Assert.That(totals.Code).IsEqualTo("journal_completion_unavailable");
        await Assert.That(totals.Output).IsNotNull();
        await Assert.That(rig.Client.CallsFor("TOTALS")).IsEqualTo(1);
        await Assert
            .That((await rig.DraftAsync(unavailable.DraftId!.Value)).Slots["totals"].Status)
            .IsEqualTo(SlotStatus.Claimed);

        rig.Store.FailAppend = null;
        var next = Check.Is<CompositeOutcome<Doc>.Incomplete>(
            await rig.AddAll(await rig.Begin()).ExecuteAsync()
        );
        await Assert
            .That(next.Steps.Single(s => s.SlotId == "totals").Status)
            .IsEqualTo(StepStatus.Pending);
        await Assert.That(rig.Client.CallsFor("TOTALS")).IsEqualTo(1);
    }

    [Test]
    public async Task Lost_completion_record_after_provider_failure_also_prevents_automatic_retry()
    {
        using var rig = new Rig();
        rig.Client.Throws.Add("TOTALS");
        rig.Store.FailAppend = e =>
            e.SlotId == "totals" && e.Kind.EndsWith("attempt.finished", StringComparison.Ordinal);
        var unavailable = Check.Is<CompositeOutcome<Doc>.Unavailable>(
            await rig.AddAll(await rig.Begin()).ExecuteAsync()
        );
        var totals = unavailable.Steps.Single(s => s.SlotId == "totals");
        await Assert.That(totals.Status).IsEqualTo(StepStatus.Pending);
        await Assert.That(totals.Code).IsEqualTo("journal_completion_unavailable");
        await Assert.That(totals.Failure).IsTypeOf<ExecutionFailure.ProviderFailure>();
        await Assert
            .That((await rig.DraftAsync(unavailable.DraftId!.Value)).Slots["totals"].Status)
            .IsEqualTo(SlotStatus.Claimed);
        rig.Store.FailAppend = null;
        var retry = Check.Is<CompositeOutcome<Doc>.Incomplete>(
            await rig.AddAll(await rig.Begin()).ExecuteAsync()
        );
        await Assert
            .That(retry.Steps.Single(s => s.SlotId == "totals").Status)
            .IsEqualTo(StepStatus.Pending);
        await Assert.That(rig.Client.CallsFor("TOTALS")).IsEqualTo(1);
    }

    [Test]
    public async Task Journal_events_record_each_attempt_in_the_store_outbox()
    {
        using var rig = new Rig();
        var pub = Check.Published(await rig.AddAll(await rig.Begin()).ExecuteAsync());
        var events = (await rig.DraftAsync(pub.DraftId)).Events;
        foreach (var slot in new[] { "header", "parties", "lines", "totals" })
        {
            var kinds = events.Where(e => e.SlotId == slot).Select(e => e.Kind).ToArray();
            await Assert.That(kinds).Contains("slot.claimed");
            await Assert.That(kinds).Contains("journal.attempt.started");
            await Assert.That(kinds).Contains("journal.attempt.finished");
            await Assert.That(kinds).Contains("slot.attached");
            await Assert
                .That(Array.IndexOf(kinds, "journal.attempt.started"))
                .IsLessThan(Array.IndexOf(kinds, "journal.attempt.finished"));
        }
        await Assert.That(events.Last().Kind).IsEqualTo("draft.sealed");
        await Assert
            .That(
                events
                    .Select(e => e.Sequence)
                    .SequenceEqual(Enumerable.Range(1, events.Count).Select(i => (long)i))
            )
            .IsTrue();
    }

    [Test]
    public async Task Seal_failure_is_unavailable_and_a_retry_publishes_without_new_spend()
    {
        using var rig = new Rig();
        rig.Store.FailSeal = true;
        var unavailable = Check.Is<CompositeOutcome<Doc>.Unavailable>(
            await rig.AddAll(await rig.Begin()).ExecuteAsync()
        );
        await Assert.That(unavailable.Reason).IsEqualTo("seal_failed");

        rig.Store.FailSeal = false;
        var pub = Check.Published(await rig.AddAll(await rig.Begin()).ExecuteAsync());
        await Assert.That(rig.Client.Total).IsEqualTo(4);
        await Assert.That(pub.Replayed).IsFalse();
        // Both attempts recorded the aggregate under the same producer and the ledger replayed one artifact, not an orphan per attempt.
        await Assert.That(rig.Ledger.Produced.Distinct().Count()).IsEqualTo(1);
        await Assert.That(rig.Ledger.Produced.Last()).IsEqualTo(pub.Aggregate.Output);
    }

    [Test]
    public async Task Aggregate_that_does_not_compose_is_a_conflict_and_the_draft_stays_open()
    {
        using var rig = new Rig(validate: doc =>
            doc.Totals.Currency == "EUR" ? null : "currency_must_be_EUR"
        );
        var conflict = Check.Is<CompositeOutcome<Doc>.Conflict>(
            await rig.AddAll(await rig.Begin()).ExecuteAsync()
        );
        await Assert.That(conflict.Code).IsEqualTo("aggregate_invalid");
        await Assert.That(conflict.Message).IsEqualTo("currency_must_be_EUR");
        var draft = await rig.DraftAsync(conflict.DraftId!.Value);
        await Assert.That(draft.Status).IsEqualTo(DraftStatus.Open);
        await Assert.That(draft.Aggregate).IsNull();
        await Assert.That(draft.Registered().All(s => s.Status == SlotStatus.Attached)).IsTrue();
    }

    [Test]
    public async Task A_throwing_whole_validator_is_a_conflict_not_an_exception()
    {
        using var rig = new Rig(validate: _ => throw new InvalidOperationException("boom"));
        var conflict = Check.Is<CompositeOutcome<Doc>.Conflict>(
            await rig.AddAll(await rig.Begin()).ExecuteAsync()
        );
        await Assert.That(conflict.Code).IsEqualTo("aggregate_validator_failed");
    }

    [Test]
    public async Task A_composition_artifact_the_ledger_refuses_is_unavailable()
    {
        using var rig = new Rig();
        rig.Ledger.RejectProduced = true;
        var unavailable = Check.Is<CompositeOutcome<Doc>.Unavailable>(
            await rig.AddAll(await rig.Begin()).ExecuteAsync()
        );
        await Assert.That(unavailable.Reason).IsEqualTo("aggregate_not_stored");
        await Assert
            .That((await rig.DraftAsync(unavailable.DraftId!.Value)).Status)
            .IsEqualTo(DraftStatus.Open);
    }

    [Test]
    public async Task A_command_from_another_runtime_ledger_is_refused_without_spend()
    {
        using var rig = new Rig();
        using var stranger = new Rig();
        var root = rig.AddAll(await rig.Begin(), skipHeader: true);
        root.WithPartFrom(DocSlots.Header, stranger.Header.WithParameters(new("x")));
        var incomplete = Check.Is<CompositeOutcome<Doc>.Incomplete>(await root.ExecuteAsync());
        await Assert
            .That(incomplete.Steps.Single(s => s.SlotId == "header").Code)
            .IsEqualTo("runtime_ledger_mismatch");
        await Assert.That(stranger.Client.Total).IsEqualTo(0);
    }

    [Test]
    public async Task An_invocation_bound_to_another_run_is_a_leaf_configuration_failure_and_never_re_pointed()
    {
        using var rig = new Rig();
        var root = rig.AddAll(await rig.Begin(), skipHeader: true);
        root.WithPartFrom(
            DocSlots.Header,
            rig.Header.WithParameters(new("x")).InRun(Guid.NewGuid())
        );
        var incomplete = Check.Is<CompositeOutcome<Doc>.Incomplete>(await root.ExecuteAsync());
        var header = incomplete.Steps.Single(s => s.SlotId == "header");
        var failure = Check.Is<ExecutionFailure.ConfigurationFailure>(header.Failure!);
        await Assert.That(failure.Reason).IsEqualTo("composition_context_conflict");
        await Assert.That(rig.Client.CallsFor("HEADER")).IsEqualTo(0);
    }

    [Test]
    public async Task Cancel_after_the_send_keeps_the_slot_pending_and_is_never_paid_for_again_automatically()
    {
        using var rig = new Rig();
        rig.Client.Before = (_, ct) => Task.Delay(Timeout.Infinite, ct);
        using var cts = new CancellationTokenSource();
        var running = rig.AddAll(await rig.Begin()).ExecuteAsync(cts.Token);
        await Check.WaitUntil(() => rig.Client.Total == 4);
        cts.Cancel();
        var cancelled = Check.Is<CompositeOutcome<Doc>.Cancelled>(await running);
        // attempt.started is stored, so a request may have been billed: the slots stay Claimed, they are NOT released for a silent repeat.
        await Assert
            .That(
                cancelled.Steps.All(s =>
                    s.Status == StepStatus.Pending && s.Code == "cancelled_after_send"
                )
            )
            .IsTrue();
        var draft = await rig.DraftAsync(cancelled.DraftId!.Value);
        await Assert.That(draft.Registered().All(s => s.Status == SlotStatus.Claimed)).IsTrue();

        rig.Client.Before = null;
        var blocked = Check.Is<CompositeOutcome<Doc>.Incomplete>(
            await rig.AddAll(await rig.Begin()).ExecuteAsync()
        );
        await Assert
            .That(
                blocked.Steps.All(s =>
                    s.Status == StepStatus.Pending && s.Code == "claimed_unresolved"
                )
            )
            .IsTrue();
        await Assert.That(rig.Client.Total).IsEqualTo(4);

        // An operator who has checked the provider resolves each Pending slot through the store, naming the exact attempt (StepId).
        foreach (var step in cancelled.Steps)
        {
            var released = await rig.RealStore.ReleaseSlotAsync(
                new(
                    cancelled.DraftId!.Value,
                    step.SlotId,
                    step.StepId!.Value,
                    SlotStatus.Failed,
                    "operator_released",
                    null
                ),
                default
            );
            await Assert.That(released).IsTypeOf<TransitionResult.Applied>();
        }
        var pub = Check.Published(await rig.AddAll(await rig.Begin()).ExecuteAsync());
        await Assert
            .That((await rig.DraftAsync(pub.DraftId)).Registered().All(s => s.Attempts == 2))
            .IsTrue();
    }

    [Test]
    public async Task A_token_cancelled_before_execute_claims_nothing()
    {
        using var rig = new Rig();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var root = rig.AddAll(await rig.Begin());
        Check.Is<CompositeOutcome<Doc>.Cancelled>(await root.ExecuteAsync(cts.Token));
        await Assert.That(rig.Client.Total).IsEqualTo(0);
        await Assert
            .That(
                (await rig.DraftAsync(root.DraftId!.Value)).Slots.Values.All(s =>
                    s.Status == SlotStatus.Empty
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task A_waiter_that_cancels_does_not_cancel_the_shared_work()
    {
        using var rig = new Rig();
        var gate = new TaskCompletionSource();
        rig.Client.Before = async (_, ct) => await gate.Task.WaitAsync(ct);
        var root = rig.AddAll(await rig.Begin());
        var first = root.ExecuteAsync();
        await Check.WaitUntil(() => rig.Client.Total == 4);
        using var cts = new CancellationTokenSource();
        var waiter = root.ExecuteAsync(cts.Token);
        cts.Cancel();
        Check.Is<CompositeOutcome<Doc>.Cancelled>(await waiter);
        gate.SetResult();
        Check.Published(await first);
    }

    [Test]
    public async Task A_store_that_throws_on_open_gives_an_unavailable_handle_and_no_spend()
    {
        using var rig = new Rig();
        var root = new RootCommands(rig.Ledger, () => rig.Actor, new ThrowingStore(), rig.Registry);
        var handle = rig.AddAll(await root.BeginAsync<Doc>(new(rig.Run, "r1", "k")));
        var unavailable = Check.Is<CompositeOutcome<Doc>.Unavailable>(await handle.ExecuteAsync());
        await Assert.That(unavailable.Reason).IsEqualTo("store_unavailable");
        await Assert.That(rig.Client.Total).IsEqualTo(0);
    }

    sealed class ThrowingStore : ICompositionStore
    {
        public ValueTask<OpenDraftResult> OpenDraftAsync(OpenDraft request, CancellationToken ct) =>
            throw new IOException("down");

        public ValueTask<DraftRecord?> GetDraftAsync(Guid draftId, CancellationToken ct) =>
            throw new IOException("down");

        public ValueTask<ClaimResult> ClaimSlotAsync(ClaimSlot request, CancellationToken ct) =>
            throw new IOException("down");

        public ValueTask<TransitionResult> AttachSlotAsync(
            AttachSlot request,
            CancellationToken ct
        ) => throw new IOException("down");

        public ValueTask<TransitionResult> ReleaseSlotAsync(
            ReleaseSlot request,
            CancellationToken ct
        ) => throw new IOException("down");

        public ValueTask<TransitionResult> AppendEventAsync(
            AppendEvent request,
            CancellationToken ct
        ) => throw new IOException("down");

        public ValueTask<SealResult> SealAsync(SealDraft request, CancellationToken ct) =>
            throw new IOException("down");
    }
}
