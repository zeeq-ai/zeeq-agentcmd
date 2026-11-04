using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd;

namespace AgentCmd.Composition.Tests;

public class ReuseAndProjectionTests
{
    static async Task<OutputRef> Seed<TR>(Rig rig, Invocation<DocParams, TR> call, Guid? run = null)
    {
        var success = (Outcome<TR>.Success)await call.InRun(run ?? rig.Run).ExecuteAsync();
        return ((OutputDelivery.Stored)success.Delivery).Output.Reference;
    }

    // ---- Reusing a pinned, already stored output ----

    [Test]
    public async Task A_pinned_output_is_attached_from_the_ledger_and_the_callers_value_is_ignored()
    {
        using var rig = new Rig();
        var pinned = await Seed(rig, rig.Header.WithParameters(new("seed")));
        var root = rig.AddAll(await rig.Begin(), skipHeader: true);
        root.ReusePart(DocSlots.Header, new Pinned<Header>(new Header("FORGED", null), pinned));
        var pub = Check.Published(await root.ExecuteAsync());

        await Assert.That(pub.Value.Header.Title).IsEqualTo("Invoice 7");
        var member = pub.Members.Single(m => m.SlotId == "header");
        await Assert.That(member.Provenance).IsEqualTo(MemberProvenance.Reused);
        await Assert.That(member.Output).IsEqualTo(pinned);
        await Assert
            .That(pub.Steps.Single(s => s.SlotId == "header").Status)
            .IsEqualTo(StepStatus.Reused);
        await Assert.That(rig.Client.CallsFor("HEADER")).IsEqualTo(1); // only the seed
    }

    [Test]
    public async Task Reuse_from_another_run_is_a_conflict()
    {
        using var rig = new Rig();
        var otherRun = rig.RealLedger.CreateRun(rig.Actor);
        var foreign = await Seed(rig, rig.Header.WithParameters(new("seed")), otherRun);
        var root = rig.AddAll(await rig.Begin(), skipHeader: true);
        root.ReusePart(DocSlots.Header, new Pinned<Header>(new Header("x", null), foreign));
        var conflict = Check.Is<CompositeOutcome<Doc>.Conflict>(await root.ExecuteAsync());
        await Assert.That(conflict.Code).IsEqualTo("pinned_other_run");
        await Assert
            .That((await rig.DraftAsync(conflict.DraftId!.Value)).Slots["header"].Status)
            .IsEqualTo(SlotStatus.Failed);
    }

    [Test]
    public async Task Reuse_of_an_unreadable_reference_is_a_conflict()
    {
        using var rig = new Rig();
        var root = rig.AddAll(await rig.Begin(), skipHeader: true);
        var invented = new OutputRef(rig.Run, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        root.ReusePart(DocSlots.Header, new Pinned<Header>(new Header("x", null), invented));
        var conflict = Check.Is<CompositeOutcome<Doc>.Conflict>(await root.ExecuteAsync());
        await Assert.That(conflict.Code.StartsWith("pinned_unreadable:")).IsTrue();
    }

    [Test]
    public async Task Reuse_of_a_wrongly_shaped_or_null_artifact_is_a_conflict()
    {
        using var rig = new Rig();
        var totals = await Seed(rig, rig.Totals.WithParameters(new("seed")));
        var wrong = rig.AddAll(await rig.Begin("wrong"), skipHeader: true);
        wrong.ReusePart(DocSlots.Header, new Pinned<Header>(new Header("x", null), totals));
        await Assert
            .That(Check.Is<CompositeOutcome<Doc>.Conflict>(await wrong.ExecuteAsync()).Code)
            .IsEqualTo("pinned_wrong_shape");

        // Optional means "may be absent". A stored JSON null is never a valid part.
        var nullRecorded = rig.RealLedger.RecordOutput(
            rig.Run,
            Guid.NewGuid(),
            JsonDocument.Parse("null").RootElement.Clone(),
            "1",
            default,
            rig.Actor
        );
        if (nullRecorded is LedgerResult<OutputArtifact>.Accepted nullArtifact)
        {
            var root = rig.AddAll(await rig.Begin("null"));
            root.ReusePart(
                DocSlots.Summary,
                new Pinned<Summary>(new Summary("x"), nullArtifact.Value.Reference)
            );
            await Assert
                .That(Check.Is<CompositeOutcome<Doc>.Conflict>(await root.ExecuteAsync()).Code)
                .IsEqualTo("pinned_null");
        }
    }

    [Test]
    public async Task Reuse_naming_a_different_output_than_the_attached_one_is_a_conflict()
    {
        using var rig = new Rig();
        Check.Published(await rig.AddAll(await rig.Begin()).ExecuteAsync());
        var other = await Seed(rig, rig.Header.WithParameters(new("other")));
        var root = await rig.Begin();
        root.ReusePart(DocSlots.Header, new Pinned<Header>(new Header("x", null), other));
        root.WithPartFrom(DocSlots.Parties, rig.Parties.WithParameters(new("doc")));
        root.WithPartFrom(DocSlots.Lines, rig.Lines.WithParameters(new("doc")));
        root.WithPartFrom(DocSlots.Totals, rig.Totals.WithParameters(new("doc")));
        await Assert
            .That(Check.Is<CompositeOutcome<Doc>.Conflict>(await root.ExecuteAsync()).Code)
            .IsEqualTo("slot_attached_to_different_output");
    }

    [Test]
    public async Task Reusing_the_attached_output_again_is_an_idempotent_replay()
    {
        using var rig = new Rig();
        var first = Check.Published(await rig.AddAll(await rig.Begin()).ExecuteAsync());
        var same = first.Members.Single(m => m.SlotId == "header").Output;
        var root = rig.AddAll(await rig.Begin(), skipHeader: true);
        root.ReusePart(DocSlots.Header, new Pinned<Header>(first.Value.Header, same));
        var again = Check.Published(await root.ExecuteAsync());
        await Assert.That(again.Replayed).IsTrue();
    }

    // ---- What the stored aggregate contains ----

    [Test]
    public async Task The_aggregate_is_the_exact_component_wire_json_and_keeps_explicit_nulls()
    {
        using var rig = new Rig();
        rig.Client.Answers["HEADER"] = """{"title":"T","reference":null}""";
        var pub = Check.Published(await rig.AddAll(await rig.Begin()).ExecuteAsync());
        var draft = await rig.DraftAsync(pub.DraftId);
        var json = draft.Aggregate!.Json;

        await Assert
            .That(draft.Aggregate.ProjectionPolicy)
            .IsEqualTo(AggregateProjection.ExactComponentWire);
        await Assert.That(json.Contains("\"reference\":null")).IsTrue();
        // Wire names, not CLR names.
        await Assert.That(json.Contains("\"line_items\"")).IsTrue();
        await Assert.That(json.Contains("\"Lines\"")).IsFalse();
        await Assert.That(pub.Value.Header.Reference).IsNull();
        // Proof the policy matters: re-serializing the typed whole WOULD drop the required nullable member.
        await Assert
            .That(JsonSerializer.Serialize(pub.Value, DocJson.Default.Doc).Contains("reference"))
            .IsFalse();

        var aggregate = JsonNode.Parse(json)!;
        foreach (var member in pub.Members)
        {
            var stored = (LedgerResult<OutputArtifact>.Accepted)
                rig.RealLedger.ReadOutput(member.Output, rig.Actor);
            await Assert
                .That(
                    JsonNode.DeepEquals(
                        aggregate[member.JsonName],
                        JsonNode.Parse(stored.Value.Value.GetRawText())
                    )
                )
                .IsTrue();
        }
        var recorded = (LedgerResult<OutputArtifact>.Accepted)
            rig.RealLedger.ReadOutput(pub.Aggregate.Output, rig.Actor);
        await Assert
            .That(JsonNode.DeepEquals(JsonNode.Parse(recorded.Value.Value.GetRawText()), aggregate))
            .IsTrue();
    }

    [Test]
    public async Task Component_artifacts_are_not_modified_by_composition()
    {
        using var rig = new Rig();
        var pub = Check.Published(await rig.AddAll(await rig.Begin()).ExecuteAsync());
        var header = pub.Members.Single(m => m.SlotId == "header");
        var stored = (LedgerResult<OutputArtifact>.Accepted)
            rig.RealLedger.ReadOutput(header.Output, rig.Actor);
        await Assert
            .That(stored.Value.Value.GetRawText())
            .IsEqualTo("""{"title":"Invoice 7","reference":"R-1"}""");
        await Assert.That(stored.Value.Reference.Version).IsEqualTo(1);
    }
}
