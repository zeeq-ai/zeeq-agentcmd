using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.LlmCmd;

namespace LlmCmd.Composition.Tests;

// Core behavior: deferral, publication, memoization, coalescing, replay, dependencies.
public class CompositionTests
{
    [Test]
    public async Task Nothing_runs_or_is_written_until_execute()
    {
        using var rig = new Rig();
        var root = rig.AddAll(await rig.Begin());
        var draft = await rig.DraftAsync(root.DraftId!.Value);
        await Assert.That(rig.Client.Total).IsEqualTo(0);
        await Assert.That(rig.Ledger.RecordedOutputs).IsEqualTo(0);
        await Assert.That(draft.Slots.Values.All(s => s.Status == SlotStatus.Empty)).IsTrue();
        await Assert.That(draft.Events.Single().Kind).IsEqualTo("draft.opened");
    }

    [Test]
    public async Task Published_aggregate_names_the_exact_stored_versions()
    {
        using var rig = new Rig();
        var root = rig.AddAll(await rig.Begin());
        var pub = Check.Published(await root.ExecuteAsync());

        await Assert.That(pub.Value.Header.Title).IsEqualTo("Invoice 7");
        await Assert.That(pub.Value.Lines.Items.Count).IsEqualTo(3);
        await Assert.That(pub.Members.Count).IsEqualTo(4);
        await Assert.That(pub.Replayed).IsFalse();
        await Assert.That(rig.Client.Total).IsEqualTo(4);
        foreach (var member in pub.Members)
        {
            var stored = (LedgerResult<OutputArtifact>.Accepted)
                rig.RealLedger.ReadOutput(member.Output, rig.Actor);
            await Assert.That(stored.Value.Reference).IsEqualTo(member.Output);
            await Assert.That(member.ExecutionId).IsEqualTo(member.Output.ExecutionId);
            await Assert.That(member.Provenance).IsEqualTo(MemberProvenance.Executed);
        }
        // The aggregate has its own producer: it is not a model call and borrows no ExecutionId.
        await Assert.That(pub.Aggregate.Output.Producer).IsTypeOf<ProducerRef.Composition>();
        // Stable per sealed-once draft, so a retry replays the same artifact.
        await Assert
            .That(((ProducerRef.Composition)pub.Aggregate.Output.Producer).Id)
            .IsEqualTo(pub.DraftId);
        await Assert.That(pub.Aggregate.AggregateId).IsEqualTo(pub.Aggregate.Output.ArtifactId);
        await Assert.That(pub.Aggregate.Output.ExecutionId).IsNull();
        var draft = await rig.DraftAsync(pub.DraftId);
        await Assert.That(draft.Status).IsEqualTo(DraftStatus.Sealed);
        await Assert.That(draft.Aggregate!.Members.Count).IsEqualTo(4);
        await Assert
            .That(pub.Steps.All(s => s.Status == StepStatus.Attached && s.DraftId == pub.DraftId))
            .IsTrue();
    }

    [Test]
    public async Task Execute_is_memoized_on_one_handle()
    {
        using var rig = new Rig();
        var root = rig.AddAll(await rig.Begin());
        var first = await root.ExecuteAsync();
        var second = await root.ExecuteAsync();
        await Assert.That(ReferenceEquals(first, second)).IsTrue();
        await Assert.That(rig.Client.Total).IsEqualTo(4);
    }

    [Test]
    public async Task Registration_after_execute_started_throws()
    {
        using var rig = new Rig();
        var root = rig.AddAll(await rig.Begin());
        var running = root.ExecuteAsync();
        await Assert
            .That(
                Check.Throws<InvalidOperationException>(() =>
                    root.WithPartFrom(DocSlots.Summary, rig.Summary.WithParameters(new("late")))
                )
            )
            .IsTrue();
        await Assert
            .That(
                Check.Throws<InvalidOperationException>(() =>
                    root.ReusePart(
                        DocSlots.Summary,
                        new Pinned<Summary>(
                            new("x"),
                            new OutputRef(
                                rig.Run,
                                Guid.NewGuid(),
                                Guid.NewGuid(),
                                Guid.NewGuid(),
                                1
                            )
                        )
                    )
                )
            )
            .IsTrue();
        Check.Published(await running);
    }

    [Test]
    public async Task Two_handles_on_one_draft_share_a_single_command_run()
    {
        using var rig = new Rig();
        var gate = new TaskCompletionSource();
        rig.Client.Before = async (_, ct) => await gate.Task.WaitAsync(ct);
        var a = rig.AddAll(await rig.Begin());
        var b = rig.AddAll(await rig.Begin());
        await Assert.That(a.DraftId).IsEqualTo(b.DraftId);

        var ta = a.ExecuteAsync();
        var tb = b.ExecuteAsync();
        await Check.WaitUntil(() => rig.Client.Total == 4);
        gate.SetResult();
        var ra = Check.Published(await ta);
        var rb = Check.Published(await tb);

        foreach (var key in new[] { "HEADER", "PARTIES", "LINES", "TOTALS" })
        {
            await Assert.That(rig.Client.CallsFor(key)).IsEqualTo(1);
        }

        await Assert.That(ra.Aggregate.AggregateId).IsEqualTo(rb.Aggregate.AggregateId);
        await Assert
            .That(ra.Members.Select(m => m.Output).SequenceEqual(rb.Members.Select(m => m.Output)))
            .IsTrue();
    }

    [Test]
    public async Task A_later_handle_replays_a_sealed_draft_without_running_anything()
    {
        using var rig = new Rig();
        var first = Check.Published(await rig.AddAll(await rig.Begin()).ExecuteAsync());
        var again = Check.Published(await rig.AddAll(await rig.Begin()).ExecuteAsync());
        await Assert.That(again.Replayed).IsTrue();
        await Assert.That(again.Aggregate.AggregateId).IsEqualTo(first.Aggregate.AggregateId);
        await Assert.That(again.Steps.All(s => s.Status == StepStatus.Replayed)).IsTrue();
        await Assert.That(rig.Client.Total).IsEqualTo(4);
    }

    [Test]
    public async Task Replaying_with_a_changed_call_is_a_conflict_not_a_silent_reuse()
    {
        using var rig = new Rig();
        Check.Published(await rig.AddAll(await rig.Begin(), "original").ExecuteAsync());
        var outcome = await rig.AddAll(await rig.Begin(), "changed").ExecuteAsync();
        await Assert
            .That(Check.Is<CompositeOutcome<Doc>.Conflict>(outcome).Code)
            .IsEqualTo("slot_attached_with_different_input");
        await Assert.That(rig.Client.Total).IsEqualTo(4);
    }

    [Test]
    public async Task Different_keys_and_revisions_are_different_drafts()
    {
        using var rig = new Rig();
        var a = await rig.Begin("k1");
        var b = await rig.Begin("k2");
        var c = await rig.Begin("k1", "r2");
        var again = await rig.Begin("k1");
        await Assert.That(a.DraftId).IsNotEqualTo(b.DraftId);
        await Assert.That(a.DraftId).IsNotEqualTo(c.DraftId);
        await Assert.That(a.DraftId).IsEqualTo(again.DraftId);
    }

    [Test]
    public async Task A_dependent_call_is_built_once_from_the_stored_version()
    {
        using var rig = new Rig();
        var root = await rig.Begin();
        int callbackRuns = 0;
        Pinned<LineItems>? seen = null;
        var lines = root.Steps.Of(DocSlots.Lines);
        root.WithPartFrom(DocSlots.Header, rig.Header.WithParameters(new("h")));
        root.WithPartFrom(DocSlots.Parties, rig.Parties.WithParameters(new("p")));
        root.WithPartFrom(DocSlots.Lines, rig.Lines.WithParameters(new("l")));
        root.WithPartFrom(
            DocSlots.Totals,
            lines.Then(pinned =>
            {
                callbackRuns++;
                seen = pinned;
                return rig.Totals.WithParameters(new("count=" + pinned.Value.Items.Count));
            })
        );
        var pub = Check.Published(await root.ExecuteAsync());

        await Assert.That(callbackRuns).IsEqualTo(1);
        await Assert.That(rig.Client.CallsFor("LINES")).IsEqualTo(1);
        await Assert.That(rig.Client.Prompts["TOTALS"]).IsEqualTo("TOTALS:count=3");
        // The callback saw the exact version that is now a member, not a copy of the in-memory value.
        await Assert
            .That(seen!.Reference)
            .IsEqualTo(pub.Members.Single(m => m.SlotId == "lines").Output);
    }

    [Test]
    public async Task A_call_can_depend_on_two_stored_parts()
    {
        using var rig = new Rig();
        var root = await rig.Begin();
        var parties = root.Steps.Of(DocSlots.Parties);
        var lines = root.Steps.Of(DocSlots.Lines);
        root.WithPartFrom(DocSlots.Header, rig.Header.WithParameters(new("h")));
        root.WithPartFrom(DocSlots.Parties, rig.Parties.WithParameters(new("p")));
        root.WithPartFrom(DocSlots.Lines, rig.Lines.WithParameters(new("l")));
        root.WithPartFrom(
            DocSlots.Totals,
            parties.Then(
                lines,
                (p, l) => rig.Totals.WithParameters(new(p.Value.Buyer + "/" + l.Value.Items.Count))
            )
        );
        Check.Published(await root.ExecuteAsync());
        await Assert.That(rig.Client.Prompts["TOTALS"]).IsEqualTo("TOTALS:Acme/3");
    }

    [Test]
    public async Task An_optional_part_is_absent_unless_registered()
    {
        using var rig = new Rig();
        var without = Check.Published(await rig.AddAll(await rig.Begin("plain")).ExecuteAsync());
        await Assert.That(without.Value.Summary).IsNull();
        await Assert.That(without.Members.Any(m => m.SlotId == "summary")).IsFalse();
        await Assert
            .That((await rig.DraftAsync(without.DraftId)).Aggregate!.Json.Contains("summary"))
            .IsFalse();

        var root = rig.AddAll(await rig.Begin("with"));
        root.WithPartFrom(DocSlots.Summary, rig.Summary.WithParameters(new("s")));
        var with = Check.Published(await root.ExecuteAsync());
        await Assert.That(with.Value.Summary!.Text).IsEqualTo("ok");
        await Assert.That(with.Members.Count).IsEqualTo(5);
    }

    [Test]
    public async Task A_failed_slot_is_retried_alone_on_a_new_handle()
    {
        using var rig = new Rig();
        rig.Client.Throws.Add("TOTALS");
        var first = Check.Is<CompositeOutcome<Doc>.Incomplete>(
            await rig.AddAll(await rig.Begin()).ExecuteAsync()
        );
        await Assert
            .That(first.Steps.Single(s => s.SlotId == "totals").Status)
            .IsEqualTo(StepStatus.Failed);
        await Assert.That(first.Steps.Count(s => s.Status == StepStatus.Attached)).IsEqualTo(3);

        rig.Client.Throws.Clear();
        var retry = await rig.Begin();
        retry.WithPartFrom(DocSlots.Totals, rig.Totals.WithParameters(new("doc")));
        var pub = Check.Published(await retry.ExecuteAsync());

        await Assert.That(rig.Client.CallsFor("HEADER")).IsEqualTo(1);
        await Assert.That(rig.Client.CallsFor("TOTALS")).IsEqualTo(2);
        await Assert
            .That((await rig.DraftAsync(pub.DraftId)).Slots["totals"].Attempts)
            .IsEqualTo(2);
    }

    [Test]
    public async Task Dependency_failure_skips_the_dependent_without_a_claim()
    {
        using var rig = new Rig();
        rig.Client.Throws.Add("LINES");
        var root = await rig.Begin();
        var lines = root.Steps.Of(DocSlots.Lines);
        root.WithPartFrom(DocSlots.Header, rig.Header.WithParameters(new("h")));
        root.WithPartFrom(DocSlots.Parties, rig.Parties.WithParameters(new("p")));
        root.WithPartFrom(DocSlots.Lines, rig.Lines.WithParameters(new("l")));
        root.WithPartFrom(DocSlots.Totals, lines.Then(p => rig.Totals.WithParameters(new("n"))));
        var incomplete = Check.Is<CompositeOutcome<Doc>.Incomplete>(await root.ExecuteAsync());

        var totals = incomplete.Steps.Single(s => s.SlotId == "totals");
        await Assert.That(totals.Status).IsEqualTo(StepStatus.Skipped);
        await Assert.That(totals.Code).IsEqualTo("dependency_not_attached:lines");
        await Assert.That(totals.ExecutionId).IsNull();
        await Assert.That(rig.Client.CallsFor("TOTALS")).IsEqualTo(0);
        await Assert
            .That((await rig.DraftAsync(incomplete.DraftId)).Slots["totals"].Status)
            .IsEqualTo(SlotStatus.Empty);
    }

    [Test]
    public async Task A_throwing_callback_stays_on_the_step_envelope()
    {
        using var rig = new Rig();
        var root = await rig.Begin();
        var lines = root.Steps.Of(DocSlots.Lines);
        root.WithPartFrom(DocSlots.Header, rig.Header.WithParameters(new("h")));
        root.WithPartFrom(DocSlots.Parties, rig.Parties.WithParameters(new("p")));
        root.WithPartFrom(DocSlots.Lines, rig.Lines.WithParameters(new("l")));
        root.WithPartFrom(
            DocSlots.Totals,
            lines.Then<Totals, DocParams>(p =>
                throw new InvalidOperationException("secret-document-text")
            )
        );
        var incomplete = Check.Is<CompositeOutcome<Doc>.Incomplete>(await root.ExecuteAsync());

        var totals = incomplete.Steps.Single(s => s.SlotId == "totals");
        await Assert.That(totals.Status).IsEqualTo(StepStatus.Failed);
        await Assert.That(totals.Code).IsEqualTo("callback_failed:InvalidOperationException");
        // Only the exception TYPE is kept. The message may hold document text.
        await Assert.That(incomplete.Steps.Any(s => (s.Code ?? "").Contains("secret"))).IsFalse();
        await Assert.That(rig.Client.CallsFor("TOTALS")).IsEqualTo(0);
        await Assert
            .That(incomplete.Steps.Count(s => s.Status == StepStatus.Attached))
            .IsEqualTo(3);
    }

    [Test]
    public async Task A_later_handle_can_depend_on_a_slot_attached_by_an_earlier_one()
    {
        using var rig = new Rig();
        rig.Client.Throws.Add("TOTALS");
        Check.Is<CompositeOutcome<Doc>.Incomplete>(
            await rig.AddAll(await rig.Begin()).ExecuteAsync()
        );
        rig.Client.Throws.Clear();

        var retry = await rig.Begin();
        retry.WithPartFrom(
            DocSlots.Totals,
            retry
                .Steps.Of(DocSlots.Lines)
                .Then(p => rig.Totals.WithParameters(new("n=" + p.Value.Items.Count)))
        );
        Check.Published(await retry.ExecuteAsync());
        await Assert.That(rig.Client.Prompts["TOTALS"]).IsEqualTo("TOTALS:n=3");
        await Assert.That(rig.Client.CallsFor("LINES")).IsEqualTo(1);
    }
}
