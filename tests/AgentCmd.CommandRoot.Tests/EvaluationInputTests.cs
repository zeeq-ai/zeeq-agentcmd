using CommandRoot;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd;

namespace AgentCmd.CommandRoot.Tests;

public class EvaluationInputTests
{
    sealed class MemoryStore : IEvaluationInputStore
    {
        public List<EvaluationInput> Items { get; } = [];

        public ValueTask SaveAsync(EvaluationInput input, CancellationToken cancellationToken)
        {
            Items.Add(input);

            return ValueTask.CompletedTask;
        }
    }

    [Test]
    public async Task Retain_pairs_the_snapshot_with_the_exact_stored_output()
    {
        var ledger = new InMemoryFeedbackLedger();
        var actor = new Actor("test", "reviewer");
        using var runtime = CommandRuntime.Create(
            () => new FakeClient("{\"Total\":12.5,\"Currency\":\"USD\"}"),
            new Dictionary<string, Profile> { { "extraction", new("m") } },
            ledger,
            () => actor
        );
        var success = (Outcome<Invoice>.Success)
            await new ExtractInvoice(runtime).WithParameters(new("doc")).ExecuteAsync();
        var store = new MemoryStore();

        var result = await success.RetainAsync(store);

        var retained = (RetainResult.Retained)result;
        var stored = ((OutputDelivery.Stored)success.Delivery).Output.Reference;
        await Assert.That(store.Items.Single()).IsEqualTo(retained.Input);
        await Assert.That(retained.Input.Output).IsEqualTo(stored);
        await Assert.That(retained.Input.ExecutionId).IsEqualTo(success.ExecutionId);
        await Assert.That(retained.Input.Snapshot.PromptId).IsEqualTo(success.Snapshot.PromptId);
    }

    [Test]
    public async Task Retain_reports_a_value_that_was_not_stored_instead_of_saving_it()
    {
        var ledger = new InMemoryFeedbackLedger();
        var actor = new Actor("test", "reviewer");
        using var runtime = CommandRuntime.Create(
            () => new FakeClient("{\"Total\":12.5,\"Currency\":\"USD\"}"),
            new Dictionary<string, Profile> { { "extraction", new("m") } },
            ledger,
            () => actor
        );
        var success = (Outcome<Invoice>.Success)
            await new ExtractInvoice(runtime).WithParameters(new("doc")).ExecuteAsync();
        var unstored = success with { Delivery = new OutputDelivery.Failed() };
        var store = new MemoryStore();

        var result = await unstored.RetainAsync(store);

        await Assert.That(result).IsTypeOf<RetainResult.NotStored>();
        await Assert.That(store.Items.Count).IsEqualTo(0);
    }
}
