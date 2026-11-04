using CommandRoot;
using Zeeq.LlmCmd;

namespace LlmCmd.CommandRoot.Tests;

/// <summary>Checks authored interface hooks through actual generated command execution.</summary>
public sealed class ValidationInterfaceTests
{
    [Test]
    public async Task InterfaceHooksValidateInputAndResultThroughGeneratedDescriptor()
    {
        var ledger = new InMemoryFeedbackLedger();
        var actor = new Actor("test", "reviewer");
        var run = ledger.CreateRun(actor);
        var client = new FakeClient("""{"Total":12.5,"Currency":"BAD"}""");
        using var runtime = CommandRuntime.Create(
            () => client,
            new Dictionary<string, Profile> { ["extraction"] = new("fake") },
            ledger,
            () => actor
        );
        var command = new InterfaceValidationCommand(runtime);

        var structurallyInvalid = await command
            .WithParameters(new InvoiceParameters(null!))
            .InRun(run)
            .ExecuteAsync();
        await Assert
            .That(
                structurallyInvalid
                    is Outcome<Invoice>.Failed { Failure: ExecutionFailure.InvalidInput }
            )
            .IsTrue();
        await Assert.That(client.Calls).IsEqualTo(0);

        var rejectedInput = await command.WithParameters(new("blocked")).InRun(run).ExecuteAsync();
        await Assert
            .That(
                rejectedInput
                    is Outcome<Invoice>.Failed
                    {
                        Failure: ExecutionFailure.InvalidInput { Reason: "Input is blocked" },
                    }
            )
            .IsTrue();
        await Assert.That(client.Calls).IsEqualTo(0);

        var thrownInput = await command.WithParameters(new("throws")).InRun(run).ExecuteAsync();
        await Assert
            .That(
                thrownInput
                    is Outcome<Invoice>.Failed
                    {
                        Failure: ExecutionFailure.HookFailure { Phase: "ValidateInput" },
                    }
            )
            .IsTrue();
        await Assert.That(client.Calls).IsEqualTo(0);

        var rejectedOutput = await command.WithParameters(new("allowed")).InRun(run).ExecuteAsync();
        await Assert
            .That(
                rejectedOutput
                    is Outcome<Invoice>.Failed
                    {
                        Failure: ExecutionFailure.InvalidOutput { Reason: "Currency is blocked" },
                    }
            )
            .IsTrue();
        await Assert.That(client.Calls).IsEqualTo(1);

        client.Response = """{"Total":12.5,"Currency":"THROW"}""";
        var thrownOutput = await command.WithParameters(new("allowed")).InRun(run).ExecuteAsync();
        await Assert
            .That(
                thrownOutput
                    is Outcome<Invoice>.Failed
                    {
                        Failure: ExecutionFailure.HookFailure { Phase: "ValidateResult" },
                    }
            )
            .IsTrue();
        await Assert.That(client.Calls).IsEqualTo(2);

        client.Response = """{"Total":12.5,"Currency":"USD"}""";
        var accepted = await command.WithParameters(new("allowed")).InRun(run).ExecuteAsync();
        await Assert.That(accepted is Outcome<Invoice>.Success).IsTrue();
        await Assert.That(client.Calls).IsEqualTo(3);
    }
}
