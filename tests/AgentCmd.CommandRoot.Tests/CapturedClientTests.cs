using CommandRoot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd;
using Zeeq.AgentCmd.Generated;
using Zeeq.AgentCmd.Providers;

namespace AgentCmd.CommandRoot.Tests;

public class CapturedClientTests
{
    [Test]
    public async Task Protocol_client_captures_each_call_inside_host_middleware_and_names_the_command()
    {
        var actor = new Actor("tenant", "reviewer");
        var sink = new RecordingSink();
        var outerCalls = 0;
        var services = new ServiceCollection();
        services.AddAgentCmd(b =>
            b.AddChatClient(
                    "azure",
                    _ => new FakeClient("{\"Total\":12.5,\"Currency\":\"USD\"}"),
                    Protocols.AzureOpenAIChatCompletions,
                    configure: pipeline =>
                        pipeline.Use(
                            (messages, options, next, cancellationToken) =>
                            {
                                outerCalls++;

                                return next(messages, options, cancellationToken);
                            }
                        )
                )
                .AddProfile("extraction", "azure", "deployment-a")
                .UseActor(_ => actor)
        );
        services.AddAttemptSink(sink);
        services.AddLlmCommands<Zeeq_AgentCmd_TestFixturesCommands>();
        using var provider = services.BuildServiceProvider();

        var success = (Outcome<Invoice>.Success)
            await provider
                .GetRequiredService<ExtractInvoice>()
                .WithParameters(new("invoice"))
                .ExecuteAsync();

        // Host middleware ran outside capture, and capture saw exactly the one provider call.
        await Assert.That(outerCalls).IsEqualTo(1);
        await Assert.That(sink.Records.Count).IsEqualTo(1);

        var record = sink.Records[0];
        await Assert.That(record.Correlation!.Command.ExecutionId).IsEqualTo(success.ExecutionId);
        await Assert.That(record.Correlation.Profile).IsEqualTo("extraction");
        await Assert.That(record.Snapshot.Registration.ClientRegistrationId).IsEqualTo("azure");
        await Assert.That(record.Snapshot.Registration.ProviderKey).IsEqualTo("azureopenai");
        await Assert.That(record.Snapshot.RequestedModelId).IsEqualTo("deployment-a");
    }

    sealed class RecordingSink : IAttemptSink
    {
        readonly List<AttemptRecord> records = [];

        public IReadOnlyList<AttemptRecord> Records
        {
            get
            {
                lock (records)
                {
                    return [.. records];
                }
            }
        }

        public ValueTask OnAttemptAsync(AttemptRecord attempt, CancellationToken cancellationToken)
        {
            lock (records)
            {
                records.Add(attempt);
            }

            return ValueTask.CompletedTask;
        }
    }
}
