using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd;
using Zeeq.AgentCmd.Providers;
using Zeeq.AgentCmd.Providers.Platforms;

namespace AgentCmd.ProviderAdapters.Tests;

public class AttemptCorrelationTests
{
    static readonly ChatMessage[] Hello = [new(ChatRole.User, "hello")];

    static AttemptCorrelation Attempt(int number = 1) =>
        new(
            new CommandExecutionContext(
                Guid.NewGuid(),
                "invoice.extract",
                1,
                "hash",
                AgentCmdContext.Capture()
            ),
            Guid.NewGuid(),
            number,
            "extract",
            default
        );

    static (IChatClient Client, CaptureSink Sink) Captured(ScriptedClient inner)
    {
        var sink = new CaptureSink();
        var services = new ServiceCollection().AddAttemptSink(sink).BuildServiceProvider();
        var client = new ChatClientBuilder(inner)
            .UseAttemptCapture(Protocols.AnthropicMessages, "anthropic")
            .Build(services);

        return (client, sink);
    }

    [Test]
    public async Task Nested_scopes_restore_the_outer_attempt()
    {
        var outer = Attempt(1);
        var inner = Attempt(2);

        using (AttemptScope.Begin(outer))
        {
            using (AttemptScope.Begin(inner))
            {
                await Assert.That(AttemptScope.Current).IsEqualTo(inner);
            }

            await Assert.That(AttemptScope.Current).IsEqualTo(outer);
        }

        await Assert.That(AttemptScope.Current).IsNull();
    }

    [Test]
    public async Task Concurrent_flows_do_not_see_each_others_attempt()
    {
        var a = Attempt(1);
        var b = Attempt(2);
        var gate = new TaskCompletionSource();

        async Task<AttemptCorrelation?> Observe(AttemptCorrelation mine)
        {
            using var scope = AttemptScope.Begin(mine);
            await gate.Task;

            return AttemptScope.Current;
        }

        var first = Task.Run(() => Observe(a));
        var second = Task.Run(() => Observe(b));
        gate.SetResult();

        await Assert.That(await first).IsEqualTo(a);
        await Assert.That(await second).IsEqualTo(b);
    }

    [Test]
    public async Task Inner_retries_get_distinct_call_ids_under_one_routed_attempt()
    {
        var (response, _, _) = Fx.Load("anthropic-cache-ttl");
        var (client, sink) = Captured(
            new ScriptedClient { Respond = (_, _, _) => Task.FromResult(response) }
        );
        var routed = Attempt();

        using (AttemptScope.Begin(routed))
        {
            // Two provider calls inside one routed attempt, as an inner retry would make.
            await client.GetResponseAsync(Hello);
            await client.GetResponseAsync(Hello);
        }

        var first = sink.Results[0].Snapshot;
        var second = sink.Results[1].Snapshot;
        await Assert.That(first.Correlation).IsEqualTo(routed);
        await Assert.That(second.Correlation).IsEqualTo(routed);
        await Assert.That(first.AttemptId).IsNotEqualTo(second.AttemptId);
    }

    [Test]
    public async Task A_late_completion_keeps_the_attempt_it_started_under()
    {
        var (response, _, _) = Fx.Load("anthropic-cache-ttl");
        var release = new TaskCompletionSource<ChatResponse>();
        var (client, sink) = Captured(new ScriptedClient { Respond = (_, _, _) => release.Task });
        var original = Attempt(1);

        Task<ChatResponse> pending;
        using (AttemptScope.Begin(original))
        {
            pending = client.GetResponseAsync(Hello);
        }

        // The router has moved on to another attempt before the first call finishes.
        using (AttemptScope.Begin(Attempt(2)))
        {
            release.SetResult(response);
            await pending;
        }

        await Assert.That(sink.Results.Single().Snapshot.Correlation).IsEqualTo(original);
    }

    [Test]
    public async Task A_call_outside_a_command_has_no_correlation()
    {
        var (response, _, _) = Fx.Load("anthropic-cache-ttl");
        var (client, sink) = Captured(
            new ScriptedClient { Respond = (_, _, _) => Task.FromResult(response) }
        );

        await client.GetResponseAsync(Hello);

        await Assert.That(sink.Results.Single().Snapshot.Correlation).IsNull();
    }
}
