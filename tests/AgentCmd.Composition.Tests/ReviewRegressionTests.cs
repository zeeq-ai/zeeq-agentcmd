using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd;

namespace AgentCmd.Composition.Tests;

// Regressions from the source review (B1-B7) and the ambient-context / event requirements. Each test names the failure it prevents.
public class ReviewRegressionTests
{
    static async Task<OutputRef> Seed<TR>(Rig rig, Invocation<DocParams, TR> call)
    {
        var success = (Outcome<TR>.Success)await call.InRun(rig.Run).ExecuteAsync();
        return ((OutputDelivery.Stored)success.Delivery).Output.Reference;
    }

    // ---- B1: a concurrent handle must not adopt another handle's output ----

    [Test]
    public async Task B1_a_concurrent_handle_with_other_input_never_receives_the_leaders_output()
    {
        using var rig = new Rig();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Client.Before = (key, _) => key == "HEADER" ? gate.Task : Task.CompletedTask;

        var a = rig.AddAll(await rig.Begin(), "docA");
        var b = rig.AddAll(await rig.Begin(), "docB"); // same draft key, different inputs for every slot
        var runningA = a.ExecuteAsync();
        await Check.WaitUntil(() => rig.Client.CallsFor("HEADER") == 1);
        var runningB = b.ExecuteAsync();
        await Task.Delay(150);
        gate.SetResult();

        Check.Published(await runningA);
        var conflict = Check.Is<CompositeOutcome<Doc>.Conflict>(await runningB);
        await Assert.That(conflict.Code).IsEqualTo("slot_attached_with_different_input");
        await Assert
            .That(conflict.Steps.Any(s => s.Status == StepStatus.Replayed && s.SlotId == "header"))
            .IsFalse();
        await Assert.That(rig.Client.Total).IsEqualTo(4); // B never ran a command
    }

    [Test]
    public async Task B1_a_concurrent_pin_against_a_running_call_is_a_conflict_not_a_replay()
    {
        using var rig = new Rig();
        var pin = await Seed(rig, rig.Header.WithParameters(new("seed")));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Client.Before = (key, _) => key == "HEADER" ? gate.Task : Task.CompletedTask;

        var a = rig.AddAll(await rig.Begin());
        var b = rig.AddAll(await rig.Begin(), skipHeader: true);
        b.ReusePart(DocSlots.Header, new Pinned<Header>(new Header("x", null), pin));
        var runningA = a.ExecuteAsync();
        await Check.WaitUntil(() => rig.Client.CallsFor("HEADER") == 2); // the seed plus A's call
        var runningB = b.ExecuteAsync();
        await Task.Delay(150);
        gate.SetResult();

        Check.Published(await runningA);
        var conflict = Check.Is<CompositeOutcome<Doc>.Conflict>(await runningB);
        await Assert.That(conflict.Code).IsEqualTo("slot_attached_to_different_output");
    }

    // ---- B2: a real call against a pinned slot ----

    [Test]
    public async Task B2_a_real_call_against_a_reused_slot_is_a_conflict_and_never_runs()
    {
        using var rig = new Rig();
        var pin = await Seed(rig, rig.Header.WithParameters(new("seed")));
        var first = rig.AddAll(await rig.Begin(), skipHeader: true);
        first.ReusePart(DocSlots.Header, new Pinned<Header>(new Header("x", null), pin));
        var pub = Check.Published(await first.ExecuteAsync());
        await Assert
            .That(pub.Members.Single(m => m.SlotId == "header").Provenance)
            .IsEqualTo(MemberProvenance.Reused);

        // Same draft key, so the same stored slots: the header slot now holds a pin, and this handle asks for a real call.
        var second = rig.AddAll(await rig.Begin());
        var outcome = await second.ExecuteAsync();
        // Sealed drafts replay as a whole only for an equal request. A call registered where a pin was stored is not equal.
        var conflict = Check.Is<CompositeOutcome<Doc>.Conflict>(outcome);
        await Assert
            .That(conflict.Steps.Single(s => s.SlotId == "header").Code)
            .IsEqualTo("slot_attached_with_different_input");
        await Assert.That(rig.Client.CallsFor("HEADER")).IsEqualTo(1); // only the seed
    }

    // ---- B5: a pin has no execution ----

    [Test]
    public async Task B5_a_reused_member_has_no_execution_id()
    {
        using var rig = new Rig();
        var pin = await Seed(rig, rig.Header.WithParameters(new("seed")));
        var root = rig.AddAll(await rig.Begin(), skipHeader: true);
        root.ReusePart(DocSlots.Header, new Pinned<Header>(new Header("x", null), pin));
        var pub = Check.Published(await root.ExecuteAsync());

        await Assert.That(pub.Members.Single(m => m.SlotId == "header").ExecutionId).IsNull();
        await Assert.That(pub.Steps.Single(s => s.SlotId == "header").ExecutionId).IsNull();
        await Assert.That((await rig.DraftAsync(pub.DraftId)).Slots["header"].ExecutionId).IsNull();
        // Executed members keep the real one.
        await Assert.That(pub.Members.Single(m => m.SlotId == "parties").ExecutionId).IsNotNull();
    }

    // ---- B6: authorization is the current subject's, at execute time ----

    [Test]
    public async Task B6_a_different_subject_without_run_access_gets_nothing_at_execute()
    {
        using var rig = new Rig();
        var root = rig.AddAll(await rig.Begin());
        var again = rig.AddAll(await rig.Begin());
        Check.Published(await root.ExecuteAsync()); // the draft is sealed by the owner

        rig.Acting = new Actor("tenant-a", "intruder"); // same tenant, no access to this run
        var denied = Check.Is<CompositeOutcome<Doc>.ConfigurationFailure>(
            await again.ExecuteAsync()
        );
        await Assert.That(denied.Problems.Contains("run_not_accessible")).IsTrue();
        await Assert.That(rig.Client.Total).IsEqualTo(4);
    }

    [Test]
    public async Task B6_a_handle_begun_by_the_owner_and_executed_by_another_subject_spends_nothing()
    {
        using var rig = new Rig();
        var root = rig.AddAll(await rig.Begin());
        rig.Acting = new Actor("tenant-a", "intruder");
        var denied = Check.Is<CompositeOutcome<Doc>.ConfigurationFailure>(
            await root.ExecuteAsync()
        );
        await Assert.That(denied.Problems.Contains("run_not_accessible")).IsTrue();
        await Assert.That(rig.Client.Total).IsEqualTo(0);
        await Assert
            .That(
                (await rig.DraftAsync(denied.DraftId!.Value))
                    .Registered()
                    .All(s => s.Status == SlotStatus.Empty)
            )
            .IsTrue();
    }

    [Test]
    public async Task B6_a_memoized_handle_rechecks_the_current_subject_before_returning_a_cached_aggregate()
    {
        using var rig = new Rig();
        var root = rig.AddAll(await rig.Begin());
        var original = Check.Published(await root.ExecuteAsync());
        rig.Acting = new Actor("tenant-a", "intruder");
        var denied = Check.Is<CompositeOutcome<Doc>.ConfigurationFailure>(
            await root.ExecuteAsync()
        );
        await Assert.That(denied.Problems.Contains("run_not_accessible")).IsTrue();
        await Assert.That(denied.DraftId).IsEqualTo(original.DraftId);
        await Assert.That(rig.Client.Total).IsEqualTo(4);
        await Assert
            .That(
                rig.Events.Composition.Any(e =>
                    e.Kind == "composition.root.rejected"
                    && e.Context.Operation.DraftId == original.DraftId
                )
            )
            .IsTrue();
        rig.Acting = rig.Actor;
        await Assert
            .That(Check.Published(await root.ExecuteAsync()).Aggregate)
            .IsEqualTo(original.Aggregate);
    }

    // ---- B7: cancel after the send is unknown spend ----
    // (See FailureTests.Cancel_after_the_send_keeps_the_slot_pending_and_is_never_paid_for_again_automatically.)

    // ---- B4: pre-claim failures keep identity, context and cause ----

    [Test]
    public async Task B4_a_callback_failure_has_its_own_step_identity_and_context_and_no_execution_id()
    {
        using var rig = new Rig();
        var root = await rig.Begin();
        root.WithPartFrom(DocSlots.Header, rig.Header.WithParameters(new("doc")));
        root.WithPartFrom(DocSlots.Parties, rig.Parties.WithParameters(new("doc")));
        root.WithPartFrom(DocSlots.Lines, rig.Lines.WithParameters(new("doc")));
        root.WithPartFrom(
            DocSlots.Totals,
            root.Steps.Of(DocSlots.Lines)
                .Then<Totals, DocParams>(_ =>
                    throw new InvalidOperationException("secret document text")
                )
        );
        var incomplete = Check.Is<CompositeOutcome<Doc>.Incomplete>(await root.ExecuteAsync());

        var step = incomplete.Steps.Single(s => s.SlotId == "totals");
        await Assert.That(step.Status).IsEqualTo(StepStatus.Failed);
        await Assert.That(step.Code).IsEqualTo("callback_failed:InvalidOperationException");
        await Assert.That(step.StepId).IsNotNull();
        await Assert.That(step.ExecutionId).IsNull(); // nothing ran: no invented id
        await Assert.That(step.Context).IsNull(); // no CommandExecutionContext exists for a call that never started
        await Assert.That(step.Operation!.WorkId).IsEqualTo(step.StepId);
        await Assert.That(step.Operation.Reference.Draft!.DraftId).IsEqualTo(incomplete.DraftId);
        await Assert.That(step.DraftId).IsEqualTo(incomplete.DraftId);
        await Assert.That(step.Code!.Contains("secret")).IsFalse();
        // No claim was written for it.
        await Assert
            .That((await rig.DraftAsync(incomplete.DraftId)).Slots["totals"].Status)
            .IsEqualTo(SlotStatus.Empty);
    }

    [Test]
    public async Task B4_a_skipped_step_keeps_the_original_failed_step_as_its_cause()
    {
        using var rig = new Rig();
        rig.Client.Throws.Add("LINES");
        var root = await rig.Begin();
        root.WithPartFrom(DocSlots.Header, rig.Header.WithParameters(new("doc")));
        root.WithPartFrom(DocSlots.Parties, rig.Parties.WithParameters(new("doc")));
        root.WithPartFrom(DocSlots.Lines, rig.Lines.WithParameters(new("doc")));
        root.WithPartFrom(
            DocSlots.Totals,
            root.Steps.Of(DocSlots.Lines)
                .Then(l => rig.Totals.WithParameters(new("n" + l.Value.Items.Count)))
        );
        var incomplete = Check.Is<CompositeOutcome<Doc>.Incomplete>(await root.ExecuteAsync());

        var skipped = incomplete.Steps.Single(s => s.SlotId == "totals");
        await Assert.That(skipped.Status).IsEqualTo(StepStatus.Skipped);
        await Assert.That(skipped.StepId).IsNotNull();
        await Assert.That(skipped.Operation!.WorkId).IsEqualTo(skipped.StepId);
        var cause = skipped.Causes!.Single();
        await Assert.That(cause.SlotId).IsEqualTo("lines");
        await Assert.That(cause.Status).IsEqualTo(StepStatus.Failed);
        await Assert.That(cause.Failure).IsNotNull(); // the leaf's own ExecutionFailure, not a string
        await Assert.That(cause.ExecutionId).IsNotNull();
        await Assert.That(skipped.ExecutionId).IsNull();
    }

    [Test]
    public async Task B4_a_store_failure_after_the_work_keeps_every_completed_step_receipt()
    {
        using var rig = new Rig();
        rig.Store.FailGetFrom = 2; // the first read (start) works; the read after the steps settle fails
        var unavailable = Check.Is<CompositeOutcome<Doc>.Unavailable>(
            await rig.AddAll(await rig.Begin()).ExecuteAsync()
        );
        await Assert.That(unavailable.Steps.Count).IsEqualTo(4);
        await Assert
            .That(
                unavailable.Steps.All(s =>
                    s.Status == StepStatus.Attached && s.Output is not null && s.Context is not null
                )
            )
            .IsTrue();
    }

    // ---- Ambient context: the callback runs inside the draft's context ----

    [Test]
    public async Task A_dependent_callback_sees_its_own_slot_context_and_the_caller_sees_nothing_afterwards()
    {
        using var rig = new Rig();
        OperationContext? seen = null;
        var root = await rig.Begin();
        root.WithPartFrom(DocSlots.Header, rig.Header.WithParameters(new("doc")));
        root.WithPartFrom(DocSlots.Parties, rig.Parties.WithParameters(new("doc")));
        root.WithPartFrom(DocSlots.Lines, rig.Lines.WithParameters(new("doc")));
        root.WithPartFrom(
            DocSlots.Totals,
            root.Steps.Of(DocSlots.Lines)
                .Then(_ =>
                {
                    seen = AgentCmdContext.Current;
                    return rig.Totals.WithParameters(new("n"));
                })
        );
        await Assert.That(AgentCmdContext.Current).IsNull();
        var pub = Check.Published(await root.ExecuteAsync());

        await Assert.That(seen).IsNotNull();
        await Assert.That(seen!.Reference.RunId).IsEqualTo(rig.Run);
        await Assert.That(seen.Reference.Draft!.DraftId).IsEqualTo(pub.DraftId);
        await Assert
            .That(seen.WorkId)
            .IsEqualTo(pub.Steps.Single(s => s.SlotId == "totals").StepId);
        await Assert.That(AgentCmdContext.Current).IsNull();
    }

    [Test]
    public async Task Concurrent_siblings_each_see_only_their_own_draft_in_callbacks()
    {
        using var rig = new Rig();
        var seenBy = new System.Collections.Concurrent.ConcurrentDictionary<string, Guid?>();
        CompositionRoot<Doc> Build(CompositionRoot<Doc> root, string name)
        {
            root.WithPartFrom(DocSlots.Header, rig.Header.WithParameters(new(name)));
            root.WithPartFrom(DocSlots.Parties, rig.Parties.WithParameters(new(name)));
            root.WithPartFrom(DocSlots.Lines, rig.Lines.WithParameters(new(name)));
            root.WithPartFrom(
                DocSlots.Totals,
                root.Steps.Of(DocSlots.Lines)
                    .Then(_ =>
                    {
                        seenBy[name] = AgentCmdContext.Current?.Reference.Draft?.DraftId;
                        return rig.Totals.WithParameters(new(name));
                    })
            );
            return root;
        }
        var a = Build(await rig.Begin("a"), "a");
        var b = Build(await rig.Begin("b"), "b");
        var pa = Check.Published(await a.ExecuteAsync());
        var pb = Check.Published(await b.ExecuteAsync());
        // Run them truly concurrently as well.
        var c = Build(await rig.Begin("c"), "c");
        var d = Build(await rig.Begin("d"), "d");
        var results = await Task.WhenAll(c.ExecuteAsync(), d.ExecuteAsync());

        await Assert.That(seenBy["a"]).IsEqualTo(pa.DraftId);
        await Assert.That(seenBy["b"]).IsEqualTo(pb.DraftId);
        await Assert.That(seenBy["c"]).IsEqualTo(Check.Published(results[0]).DraftId);
        await Assert.That(seenBy["d"]).IsEqualTo(Check.Published(results[1]).DraftId);
    }

    // ---- Composition events: one closed sink, no invented execution ids ----

    [Test]
    public async Task Root_step_and_stored_events_reach_the_runtime_sink_with_captured_context()
    {
        using var rig = new Rig();
        var pub = Check.Published(await rig.AddAll(await rig.Begin()).ExecuteAsync());
        var events = rig.Events.Composition.ToArray();

        var started = events.Single(e => e.Kind == "composition.root.started");
        var finished = events.Single(e => e.Kind == "composition.root.finished");
        await Assert.That(finished.Outcome).IsEqualTo("published");
        await Assert.That(started.Operation.DraftId).IsEqualTo(pub.DraftId);
        await Assert.That(started.Context.ExecutionId).IsNull(); // a root is not a model call
        await Assert.That(events.Count(e => e.Kind == "composition.step.attached")).IsEqualTo(4);
        foreach (var e in events.Where(e => e.Kind == "composition.step.attached"))
        {
            await Assert.That(e.Context.ExecutionId).IsNotNull();
            await Assert.That(e.Operation.WorkId).IsEqualTo(e.Context.StepId);
        }
        // The stored outbox is projected once, in stored order, and journal entries are not duplicated.
        var stored = events
            .Where(e => e.Kind.StartsWith("composition.store.", StringComparison.Ordinal))
            .ToArray();
        await Assert.That(stored.Any(e => e.Kind == "composition.store.draft.sealed")).IsTrue();
        await Assert.That(stored.Any(e => e.Kind.Contains("journal"))).IsFalse();
        await Assert
            .That(
                stored
                    .Select(e => e.Context.OutboxSequence!.Value)
                    .SequenceEqual(stored.Select(e => e.Context.OutboxSequence!.Value).Order())
            )
            .IsTrue();
    }

    [Test]
    public async Task A_second_execute_projects_only_new_stored_events()
    {
        using var rig = new Rig();
        var root = rig.AddAll(await rig.Begin());
        Check.Published(await root.ExecuteAsync());
        var before = rig.Events.Composition.Count(e =>
            e.Kind.StartsWith("composition.store.", StringComparison.Ordinal)
        );
        Check.Published(await rig.AddAll(await rig.Begin()).ExecuteAsync()); // sealed replay: nothing new in the store
        var after = rig.Events.Composition.Count(e =>
            e.Kind.StartsWith("composition.store.", StringComparison.Ordinal)
        );
        await Assert.That(after).IsEqualTo(before);
    }

    [Test]
    public async Task Skipped_and_callback_failed_steps_emit_events_with_a_code_and_no_execution_id()
    {
        using var rig = new Rig();
        var root = await rig.Begin();
        root.WithPartFrom(DocSlots.Header, rig.Header.WithParameters(new("doc")));
        root.WithPartFrom(DocSlots.Parties, rig.Parties.WithParameters(new("doc")));
        root.WithPartFrom(
            DocSlots.Lines,
            root.Steps.Of(DocSlots.Header)
                .Then<LineItems, DocParams>(_ => throw new InvalidOperationException("boom"))
        );
        root.WithPartFrom(
            DocSlots.Totals,
            root.Steps.Of(DocSlots.Lines).Then(_ => rig.Totals.WithParameters(new("n")))
        );
        var incomplete = Check.Is<CompositeOutcome<Doc>.Incomplete>(await root.ExecuteAsync());

        var failed = rig.Events.Composition.Single(e => e.Kind == "composition.step.failed");
        await Assert.That(failed.Code).IsEqualTo("callback_failed:InvalidOperationException");
        await Assert.That(failed.Context.ExecutionId).IsNull();
        await Assert
            .That(failed.Context.StepId)
            .IsEqualTo(incomplete.Steps.Single(s => s.SlotId == "lines").StepId);
        var skipped = rig.Events.Composition.Single(e => e.Kind == "composition.step.skipped");
        await Assert.That(skipped.Code).IsEqualTo("dependency_not_attached:lines");
        await Assert.That(skipped.Context.ExecutionId).IsNull();
        await Assert
            .That(rig.Events.Composition.Single(e => e.Kind == "composition.root.finished").Outcome)
            .IsEqualTo("incomplete");
    }

    [Test]
    public async Task A_handle_that_never_reached_execution_still_emits_a_root_event()
    {
        using var rig = new Rig();
        var handle = await rig.Root.BeginAsync<Doc>(new(rig.Run, "r1", "bad key!"));
        var failure = Check.Is<CompositeOutcome<Doc>.ConfigurationFailure>(
            await handle.ExecuteAsync()
        );
        await Assert.That(failure.Problems.Contains("key_invalid")).IsTrue();
        var rejected = rig.Events.Composition.Single(e => e.Kind == "composition.root.rejected");
        await Assert.That(rejected.Code).IsEqualTo("key_invalid");
        await Assert.That(rejected.Context.ExecutionId).IsNull();
    }

    [Test]
    public async Task A_failing_event_sink_never_changes_the_result()
    {
        using var rig = new Rig();
        var throwing = new RootCommands(
            rig.Ledger,
            () => rig.Actor,
            rig.Store,
            rig.Registry,
            null,
            new ThrowingSink()
        );
        var root = await throwing.BeginAsync<Doc>(new(rig.Run, "r1", "throwing"));
        Check.Published(await rig.AddAll(root).ExecuteAsync());
    }

    sealed class ThrowingSink : IExecutionEventSink
    {
        public ValueTask EmitAsync(LlmEvent record, CancellationToken cancellationToken) =>
            throw new IOException("sink down");
    }
}
