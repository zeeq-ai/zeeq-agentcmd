using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.LlmCmd;

namespace LlmCmd.Composition.Tests;

// Correlation: every leaf event carries Draft + Slot + Step, whatever ambient state the caller has.
public class ObservabilityTests
{
    static readonly string[] Lifecycle =
    [
        "execution.started",
        "attempt.started",
        "attempt.finished",
        "output.delivery",
        "execution.finished",
    ];

    [Test]
    public async Task Leaf_events_carry_draft_slot_and_step_even_after_the_callers_scope_is_gone()
    {
        using var rig = new Rig();
        var root = rig.AddAll(await rig.Begin());
        var outer = OperationContext.ForRun(Guid.NewGuid());
        Task<CompositeOutcome<Doc>> running;
        using (LlmCmdContext.BeginScope(outer))
        {
            running = root.ExecuteAsync(); // scope ends while the work is still in flight
        }

        var pub = Check.Published(await running);

        await Assert.That(LlmCmdContext.Current).IsNull();
        var stepBySlot = pub.Steps.ToDictionary(s => s.SlotId, s => s.StepId);
        foreach (var slot in stepBySlot.Keys)
        {
            var mine = rig.Events.Events.Where(e => e.Context.SlotId == slot).ToArray();
            await Assert
                .That(
                    mine.Select(e => e.Kind)
                        .Distinct()
                        .Order(StringComparer.Ordinal)
                        .SequenceEqual(Lifecycle.Order(StringComparer.Ordinal))
                )
                .IsTrue();
            foreach (var e in mine)
            {
                await Assert.That(e.Context.StepId).IsEqualTo(stepBySlot[slot]);
                await Assert.That(e.Context.Operation.DraftId).IsEqualTo(pub.DraftId);
                await Assert.That(e.Context.Operation.WorkId).IsEqualTo(stepBySlot[slot]);
                // The slot's own context wins: the ambient run is never merged in.
                await Assert.That(e.Context.Operation.RunId).IsEqualTo(rig.Run);
                await Assert.That(e.Context.Operation.Source).IsEqualTo(ContextSource.Explicit);
            }
        }
        await Assert
            .That(rig.Events.Events.Any(e => e.Context.Operation.RunId == outer.Reference.RunId))
            .IsFalse();
    }

    [Test]
    public async Task Sibling_drafts_running_in_different_scopes_do_not_cross()
    {
        using var rig = new Rig();
        var a = rig.AddAll(await rig.Begin("a"), "a-doc");
        var b = rig.AddAll(await rig.Begin("b"), "b-doc");
        Task<CompositeOutcome<Doc>> ta,
            tb;
        using (LlmCmdContext.BeginScope(OperationContext.ForRun(Guid.NewGuid())))
        {
            ta = a.ExecuteAsync();
        }

        using (LlmCmdContext.BeginScope(OperationContext.ForRun(Guid.NewGuid())))
        {
            tb = b.ExecuteAsync();
        }

        var pa = Check.Published(await ta);
        var pb = Check.Published(await tb);

        await Assert.That(pa.DraftId).IsNotEqualTo(pb.DraftId);
        var stepsOfDraft = new Dictionary<Guid, HashSet<Guid?>>
        {
            [pa.DraftId] = pa.Steps.Select(s => s.StepId).ToHashSet(),
            [pb.DraftId] = pb.Steps.Select(s => s.StepId).ToHashSet(),
        };
        foreach (var e in rig.Events.Events)
        {
            await Assert
                .That(stepsOfDraft[e.Context.Operation.DraftId!.Value].Contains(e.Context.StepId))
                .IsTrue();
        }
    }

    [Test]
    public async Task Composition_does_not_leak_a_scope_into_the_caller()
    {
        using var rig = new Rig();
        var outer = OperationContext.ForRun(Guid.NewGuid());
        using (LlmCmdContext.BeginScope(outer))
        {
            Check.Published(await rig.AddAll(await rig.Begin()).ExecuteAsync());
            await Assert.That(ReferenceEquals(LlmCmdContext.Current, outer)).IsTrue();
        }
        await Assert.That(LlmCmdContext.Current).IsNull();
    }

    [Test]
    public async Task The_application_call_site_is_kept_through_the_deferred_call()
    {
        using var rig = new Rig();
        var root = rig.AddAll(await rig.Begin(), skipHeader: true);
        int firstLine = Check.Line();
        root.WithPartFrom(DocSlots.Header, rig.Header.WithParameters(new("x")));
        int lastLine = Check.Line();
        var pub = Check.Published(await root.ExecuteAsync());

        var caller = pub.Steps.Single(s => s.SlotId == "header").Caller!;
        await Assert
            .That(caller.Member)
            .IsEqualTo(nameof(The_application_call_site_is_kept_through_the_deferred_call));
        await Assert
            .That(caller.File.EndsWith("ObservabilityTests.cs", StringComparison.Ordinal))
            .IsTrue();
        // A formatter may split the invocation across lines. The recorded line
        // must stay inside that application statement, not a library wrapper.
        await Assert.That(caller.Line > firstLine && caller.Line < lastLine).IsTrue();
    }

    [Test]
    public async Task Reserved_execution_id_is_the_one_the_leaf_and_the_ledger_use()
    {
        using var rig = new Rig();
        var pub = Check.Published(await rig.AddAll(await rig.Begin()).ExecuteAsync());
        var draft = await rig.DraftAsync(pub.DraftId);
        foreach (var step in pub.Steps)
        {
            await Assert.That(draft.Slots[step.SlotId].ExecutionId).IsEqualTo(step.ExecutionId);
            await Assert.That(step.Output!.ExecutionId).IsEqualTo(step.ExecutionId);
            await Assert.That(step.Context!.ExecutionId).IsEqualTo(step.ExecutionId);
        }
    }
}
