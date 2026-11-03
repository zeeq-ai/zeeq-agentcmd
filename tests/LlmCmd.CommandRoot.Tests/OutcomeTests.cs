using CommandRoot;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.LlmCmd;

namespace LlmCmd.CommandRoot.Tests;

public class OutcomeTests
{
    static CommandExecutionContext Context() =>
        new(
            Guid.NewGuid(),
            "test.outcome",
            1,
            "definition",
            LlmCmdContext.Capture(OperationContext.ForRun(Guid.NewGuid()))
        );

    static ExecutionSnapshot Snapshot() =>
        new(
            "test.outcome",
            1,
            "definition",
            "template",
            "rendered",
            Array.Empty<ParameterRecord>(),
            "test",
            "model",
            .2f
        );

    static ExecutionFailure Failure(
        string kind,
        CommandExecutionContext context,
        ExecutionSnapshot snapshot
    ) =>
        kind switch
        {
            "input" => new ExecutionFailure.InvalidInput(context, "bad input", snapshot),
            "provider" => new ExecutionFailure.ProviderFailure(
                context,
                "provider failed",
                snapshot
            ),
            "hook" => new ExecutionFailure.HookFailure(context, "Project", snapshot),
            "configuration" => new ExecutionFailure.ConfigurationFailure(
                context,
                "bad config",
                snapshot
            ),
            "output" => new ExecutionFailure.InvalidOutput(context, "bad output", snapshot),
            "cancelled" => new ExecutionFailure.Cancelled(context, snapshot),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    [Test]
    [Arguments("input")]
    [Arguments("provider")]
    [Arguments("hook")]
    [Arguments("configuration")]
    [Arguments("output")]
    [Arguments("cancelled")]
    public async Task Failure_is_forwarded_by_reference_without_callbacks_or_metadata_copy(
        string kind
    )
    {
        var context = Context();
        var snapshot = Snapshot();
        var failure = Failure(kind, context, snapshot);
        Outcome<int> source = new Outcome<int>.Failed(failure);
        int calls = 0;
        var mapped = source.Map(value =>
        {
            calls++;
            return value.ToString();
        });
        var bound = source.Bind<int, string>(value =>
        {
            calls++;
            return new Outcome<string>.Failed(failure);
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var asynchronous = await Task.FromResult(source)
            .BindAsync<int, string>(
                (value, ct) =>
                {
                    calls++;
                    return Task.FromResult<Outcome<string>>(new Outcome<string>.Failed(failure));
                },
                cancellation.Token
            );
        foreach (var result in new[] { mapped, bound, asynchronous })
        {
            var failed = (Outcome<string>.Failed)result;
            await Assert.That(ReferenceEquals(failed.Failure, failure)).IsTrue();
            await Assert.That(ReferenceEquals(failed.Context, context)).IsTrue();
            await Assert.That(ReferenceEquals(failed.Failure.Snapshot, snapshot)).IsTrue();
            await Assert.That(failed.ExecutionId).IsEqualTo(context.ExecutionId);
        }
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task Map_projects_value_without_claiming_to_store_a_new_artifact()
    {
        var context = Context();
        var snapshot = Snapshot();
        var caller = new CallSite("caller", "caller.cs", 7);
        var delivery = new OutputDelivery.Failed();
        Outcome<int> source = new Outcome<int>.Success(
            21,
            Guid.NewGuid(),
            delivery,
            caller,
            snapshot,
            context
        );
        var mapped = (Outcome<string>.Success)source.Map(value => (value * 2).ToString());
        await Assert.That(mapped.Value).IsEqualTo("42");
        await Assert.That(ReferenceEquals(mapped.Delivery, delivery)).IsTrue();
        await Assert.That(ReferenceEquals(mapped.Context, context)).IsTrue();
        await Assert.That(ReferenceEquals(mapped.Snapshot, snapshot)).IsTrue();
        await Assert.That(mapped.Caller).IsEqualTo(caller);
    }

    [Test]
    public async Task Pure_callback_exceptions_propagate_and_async_bind_receives_the_token()
    {
        var context = Context();
        var snapshot = Snapshot();
        Outcome<int> source = new Outcome<int>.Success(
            21,
            Guid.NewGuid(),
            new OutputDelivery.Failed(),
            new("caller", "caller.cs", 7),
            snapshot,
            context
        );
        bool threw = false;
        try
        {
            source.Map<int, string>(_ => throw new InvalidOperationException("programmer error"));
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
        using var cancellation = new CancellationTokenSource();
        CancellationToken observed = default;
        var bound = await source.BindAsync<int, int>(
            (value, ct) =>
            {
                observed = ct;
                return Task.FromResult<Outcome<int>>(
                    new Outcome<int>.Success(
                        value * 2,
                        Guid.NewGuid(),
                        new OutputDelivery.Failed(),
                        new("next", "next.cs", 1),
                        snapshot,
                        context
                    )
                );
            },
            cancellation.Token
        );
        await Assert.That(observed == cancellation.Token).IsTrue();
        await Assert.That(bound.Match(value => value, _ => -1)).IsEqualTo(42);
    }
}
