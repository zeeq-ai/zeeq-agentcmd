using CommandRoot;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd;

namespace AgentCmd.CommandRoot.Tests;

public sealed class PromptTemplateTests
{
    [Test]
    public async Task RenderedUserPromptMustContainNonWhitespaceText()
    {
        var client = new FakeClient("{\"Total\":12.5,\"Currency\":\"USD\"}");
        using var runtime = CommandRuntime.Create(
            () => client,
            new Dictionary<string, Profile> { ["extraction"] = new("model") },
            new InMemoryFeedbackLedger(),
            () => new Actor("tenant", "user")
        );
        var command = new PromptTemplateCommand(runtime);

        foreach (var text in new[] { "", " \t", "\u2003" })
        {
            var result = await command.WithParameters(new(text, "fr")).ExecuteAsync();
            await Assert
                .That(result is Outcome<Invoice>.Failed { Failure: ExecutionFailure.InvalidInput })
                .IsTrue();
        }

        await Assert.That(client.Calls).IsEqualTo(0);
        var success = await command.WithParameters(new(" invoice ", "fr")).ExecuteAsync();
        await Assert.That(success is Outcome<Invoice>.Success).IsTrue();
        await Assert.That(client.Prompt).IsEqualTo(" invoice ");
        await Assert.That(client.System).IsEqualTo("Use fr");
        await Assert.That(client.Calls).IsEqualTo(1);

        var noSystem = await new InterfaceValidationCommand(runtime)
            .WithParameters(new("invoice"))
            .ExecuteAsync();
        await Assert.That(noSystem is Outcome<Invoice>.Success).IsTrue();
        await Assert.That(client.System).IsNull();
    }
}
