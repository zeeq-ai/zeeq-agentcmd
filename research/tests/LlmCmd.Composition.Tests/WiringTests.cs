using CommandRoot;
using HumanGrading;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
namespace LlmCmd.Composition.Tests;

// Wiring mistakes are found before any claim or provider call. Every test here asserts zero spend.
public class WiringTests
{
    static async Task NoSpend(Rig rig, Guid? draftId)
    {
        await Assert.That(rig.Client.Total).IsEqualTo(0);
        await Assert.That(rig.Ledger.RecordedOutputs).IsEqualTo(0);
        if (draftId is { } id)
            await Assert.That((await rig.DraftAsync(id)).Slots.Values.All(s => s.Status == SlotStatus.Empty && s.Attempts == 0)).IsTrue();
    }

    [Test]
    public async Task Duplicate_slot_is_a_deferred_configuration_failure()
    {
        using var rig = new Rig();
        var root = rig.AddAll(await rig.Begin());
        root.WithPartFrom(DocSlots.Header, rig.Header.WithParameters(new("again")));
        var failure = Check.Is<CompositeOutcome<Doc>.ConfigurationFailure>(await root.ExecuteAsync());
        await Assert.That(failure.Problems).Contains("duplicate_slot:header");
        await Assert.That(failure.DraftId).IsEqualTo(root.DraftId);
        await NoSpend(rig, root.DraftId);
    }

    [Test]
    public async Task Missing_required_slot_is_refused_before_any_spend()
    {
        using var rig = new Rig();
        var root = await rig.Begin();
        root.WithPartFrom(DocSlots.Header, rig.Header.WithParameters(new("h")));
        root.WithPartFrom(DocSlots.Parties, rig.Parties.WithParameters(new("p")));
        root.WithPartFrom(DocSlots.Lines, rig.Lines.WithParameters(new("l")));
        var failure = Check.Is<CompositeOutcome<Doc>.ConfigurationFailure>(await root.ExecuteAsync());
        await Assert.That(failure.Problems).Contains("missing_required_slot:totals");
        await NoSpend(rig, root.DraftId);
    }

    [Test]
    public async Task A_slot_that_only_looks_like_a_declared_one_is_refused()
    {
        using var rig = new Rig();
        var root = rig.AddAll(await rig.Begin(), skipHeader: true);
        var lookalike = PartSlot<Doc, Header>.Declare("header", "hdr"); // same id, different wire name
        root.WithPartFrom(lookalike, rig.Header.WithParameters(new("h")));
        var failure = Check.Is<CompositeOutcome<Doc>.ConfigurationFailure>(await root.ExecuteAsync());
        await Assert.That(failure.Problems).Contains("unknown_slot:header");
        await NoSpend(rig, root.DraftId);
    }

    [Test]
    public async Task Dependency_cycles_self_dependencies_and_unknown_dependencies_are_refused()
    {
        using var rig = new Rig();
        var cycle = await rig.Begin("cycle");
        var totals = cycle.Steps.Of(DocSlots.Totals); var lines = cycle.Steps.Of(DocSlots.Lines);
        cycle.WithPartFrom(DocSlots.Header, rig.Header.WithParameters(new("h")));
        cycle.WithPartFrom(DocSlots.Parties, rig.Parties.WithParameters(new("p")));
        cycle.WithPartFrom(DocSlots.Lines, totals.Then<LineItems, DocParams>(t => rig.Lines.WithParameters(new("x"))));
        cycle.WithPartFrom(DocSlots.Totals, lines.Then<Totals, DocParams>(l => rig.Totals.WithParameters(new("x"))));
        var cycleFailure = Check.Is<CompositeOutcome<Doc>.ConfigurationFailure>(await cycle.ExecuteAsync());
        await Assert.That(cycleFailure.Problems.Any(p => p.StartsWith("dependency_cycle:"))).IsTrue();

        var self = rig.AddAll(await rig.Begin("self"), skipHeader: true);
        self.WithPartFrom(DocSlots.Header, self.Steps.Of(DocSlots.Header).Then<Header, DocParams>(h => rig.Header.WithParameters(new("x"))));
        await Assert.That(Check.Is<CompositeOutcome<Doc>.ConfigurationFailure>(await self.ExecuteAsync()).Problems).Contains("self_dependency:header");

        var unknown = rig.AddAll(await rig.Begin("unknown"), skipHeader: true);
        unknown.WithPartFrom(DocSlots.Header, unknown.Steps.Of(DocSlots.Summary).Then<Header, DocParams>(s => rig.Header.WithParameters(new("x"))));
        await Assert.That(Check.Is<CompositeOutcome<Doc>.ConfigurationFailure>(await unknown.ExecuteAsync()).Problems).Contains("unknown_dependency:header->summary");
        await NoSpend(rig, null);
    }

    [Test]
    public async Task Null_arguments_throw()
    {
        using var rig = new Rig();
        var root = await rig.Begin();
        await Assert.That(Check.Throws<ArgumentNullException>(() => root.WithPartFrom(DocSlots.Header, (Invocation<DocParams, Header>)null!))).IsTrue();
        await Assert.That(Check.Throws<ArgumentNullException>(() => root.WithPartFrom(null!, rig.Header.WithParameters(new("x"))))).IsTrue();
        await Assert.That(Check.Throws<ArgumentNullException>(() => root.ReusePart(DocSlots.Header, null!))).IsTrue();
    }

    [Test]
    public async Task Bad_requests_come_back_as_a_handle_that_reports_the_failure()
    {
        using var rig = new Rig();
        var bad = await rig.Root.BeginAsync<Doc>(new(rig.Run, "r 1", "has space"));
        await Assert.That(bad.DraftId).IsNull();
        var failure = Check.Is<CompositeOutcome<Doc>.ConfigurationFailure>(await bad.ExecuteAsync());
        await Assert.That(failure.Problems).Contains("input_revision_invalid");
        await Assert.That(failure.Problems).Contains("key_invalid");
        await Assert.That(failure.Request!.Key).IsEqualTo("has space");

        var empty = Check.Is<CompositeOutcome<Doc>.ConfigurationFailure>(await (await rig.Root.BeginAsync<Doc>(new(Guid.Empty, "r1", "k"))).ExecuteAsync());
        await Assert.That(empty.Problems).Contains("run_invalid");

        var unknown = Check.Is<CompositeOutcome<Header>.ConfigurationFailure>(await (await rig.Root.BeginAsync<Header>(new(rig.Run, "r1", "k"))).ExecuteAsync());
        await Assert.That(unknown.Problems).Contains("unknown_composite:Header");
    }

    [Test]
    public async Task Another_tenant_cannot_open_a_draft_on_the_run()
    {
        using var rig = new Rig(rootActor: new Actor("tenant-b", "mallory"));
        var root = await rig.Begin();
        var failure = Check.Is<CompositeOutcome<Doc>.ConfigurationFailure>(await rig.AddAll(root).ExecuteAsync());
        await Assert.That(failure.Problems).Contains("run_not_accessible");
        await Assert.That(root.DraftId).IsNull();
        await Assert.That(rig.Client.Total).IsEqualTo(0);
    }

    [Test]
    public async Task A_draft_opened_under_another_definition_hash_is_refused()
    {
        using var rig = new Rig();
        await rig.Begin();
        var other = new CompositeDescriptor<Doc>("document.extraction", 1, "hash-v2", DocJson.Default.Doc, DocSlots.All);
        var root = new RootCommands(rig.Ledger, () => rig.Actor, rig.Store, new CompositionRegistry([other]));
        var handle = await root.BeginAsync<Doc>(new(rig.Run, "r1", "k"));
        await Assert.That(Check.Is<CompositeOutcome<Doc>.Conflict>(await handle.ExecuteAsync()).Code).IsEqualTo("definition_mismatch");
    }

    [Test]
    public async Task Descriptors_are_checked_against_the_serializer_metadata()
    {
        var good = DocSlots.All;
        CompositeDescriptor<Doc> With(string hash, params SlotDescriptor[] slots) => new("document.extraction", 1, hash, DocJson.Default.Doc, slots);

        var missingProperty = CompositionRegistry.Validate(With("h", good[0] with { JsonName = "nope" }));
        await Assert.That(missingProperty).Contains("json_name_not_in_whole:header");

        // The CLR name "Lines" is NOT what the serializer reads: the wire name is line_items.
        await Assert.That(CompositionRegistry.Validate(With("h", good[2] with { JsonName = "Lines" }))).Contains("json_name_not_in_whole:lines");
        await Assert.That(CompositionRegistry.Validate(With("h", good[0] with { JsonName = "parties" }))).Contains("part_type_mismatch:header");
        await Assert.That(CompositionRegistry.Validate(With("h", good[0], good[1] with { JsonName = "header" }))).Contains("duplicate_json_name:header");
        await Assert.That(CompositionRegistry.Validate(With("h", good[0], good[0]))).Contains("duplicate_slot_id:header");
        await Assert.That(CompositionRegistry.Validate(With("bad hash"))).Contains("definition_hash_invalid");
        await Assert.That(CompositionRegistry.Validate(With("h"))).Contains("no_slots");
        await Assert.That(CompositionRegistry.Validate(With("hash-v1", good))).IsEmpty();
        await Assert.That(Check.Throws<ArgumentException>(() => _ = new CompositionRegistry([With("h", good[0] with { JsonName = "nope" })]))).IsTrue();
    }

    [Test]
    public async Task Slot_declaration_rejects_bad_ids()
    {
        await Assert.That(Check.Throws<ArgumentException>(() => PartSlot<Doc, Header>.Declare("bad id", "header"))).IsTrue();
        await Assert.That(Check.Throws<ArgumentException>(() => PartSlot<Doc, Header>.Declare("header", ""))).IsTrue();
    }

    [Test]
    public async Task Dependency_injection_wires_the_runtime_and_validates_descriptors_at_setup()
    {
        using var rig = new Rig();
        var services = new ServiceCollection()
            .AddSingleton<IHumanFeedbackLedger>(rig.Ledger).AddSingleton<Func<Actor>>(() => rig.Actor)
            .AddCompositionRuntime().AddCompositeDescriptor(rig.Descriptor);
        using var provider = services.BuildServiceProvider();
        var roots = provider.GetRequiredService<IRootCommands>();
        var root = rig.AddAll(await roots.BeginAsync<Doc>(new(rig.Run, "r1", "di")));
        Check.Published(await root.ExecuteAsync());

        var bad = new CompositeDescriptor<Doc>("document.extraction", 1, "h", DocJson.Default.Doc, [DocSlots.Header.Descriptor with { JsonName = "nope" }]);
        await Assert.That(Check.Throws<InvalidOperationException>(() => new ServiceCollection().AddCompositionRuntime().AddCompositeDescriptor(bad))).IsTrue();
    }
}

public class ContractCompatibilityTests
{
    [Test]
    public async Task A_leaf_that_writes_other_property_names_than_the_aggregate_reads_is_refused_before_spend()
    {
        using var rig = new Rig();
        var root = rig.AddAll(await rig.Begin(), skipHeader: true);
        root.WithPartFrom(DocSlots.Header, rig.PascalHeader.WithParameters(new("x")));
        var failure = Check.Is<CompositeOutcome<Doc>.ConfigurationFailure>(await root.ExecuteAsync());
        await Assert.That(failure.Problems).Contains("incompatible_contract:header");
        await Assert.That(rig.Client.Total).IsEqualTo(0);
    }

    [Test]
    public async Task A_dependent_call_with_an_incompatible_contract_fails_its_step_before_the_claim()
    {
        using var rig = new Rig();
        var root = await rig.Begin();
        var lines = root.Steps.Of(DocSlots.Lines);
        root.WithPartFrom(DocSlots.Header, rig.Header.WithParameters(new("h")));
        root.WithPartFrom(DocSlots.Parties, rig.Parties.WithParameters(new("p")));
        root.WithPartFrom(DocSlots.Lines, rig.Lines.WithParameters(new("l")));
        root.WithPartFrom(DocSlots.Totals, lines.Then(p => rig.PascalTotals.WithParameters(new("n"))));
        var incomplete = Check.Is<CompositeOutcome<Doc>.Incomplete>(await root.ExecuteAsync());
        var totals = incomplete.Steps.Single(s => s.SlotId == "totals");
        await Assert.That(totals.Code).IsEqualTo("incompatible_contract:totals");
        // Its callback can only construct the call after the prerequisite is stored.
        // The prerequisite may have spent; this specific incompatible call cannot.
        await Assert.That(rig.Client.CallsFor("LINES")).IsEqualTo(1);
        await Assert.That(rig.Client.CallsFor("TOTALS")).IsEqualTo(0);
        await Assert.That((await rig.DraftAsync(incomplete.DraftId)).Slots["totals"].Status).IsEqualTo(SlotStatus.Empty);
    }
}
