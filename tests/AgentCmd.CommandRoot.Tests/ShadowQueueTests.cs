using System.Collections.Concurrent;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd;

namespace AgentCmd.CommandRoot.Tests;

public class ShadowQueueTests
{
    static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Test]
    public async Task AdmissionIsBoundedAndPrimaryDoesNotWaitForShadow()
    {
        await using var scheduler = new InMemoryShadowScheduler(1, 1);
        var entered = NewSignal();
        var release = NewSignal();
        var dispositions = new ConcurrentQueue<ShadowDisposition>();
        var work = new ShadowWork(
            async ct =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            },
            TimeSpan.FromSeconds(10),
            dispositions.Enqueue
        );
        await Assert.That(scheduler.TrySchedule(work)).IsTrue();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(scheduler.TrySchedule(work)).IsTrue();
        await Assert.That(scheduler.TrySchedule(work)).IsFalse();
        await Assert.That(dispositions.Contains(ShadowDisposition.Rejected)).IsTrue();
        release.TrySetResult();
        await scheduler.CompleteAsync();
        await Assert.That(dispositions.Count(x => x == ShadowDisposition.Completed)).IsEqualTo(2);
        await Assert.That(scheduler.TrySchedule(work)).IsFalse();
    }

    [Test]
    public async Task ShutdownCancelsRunningAndQueuedWork()
    {
        var scheduler = new InMemoryShadowScheduler(1, 1);
        var entered = NewSignal();
        var dispositions = new ConcurrentQueue<ShadowDisposition>();
        var work = new ShadowWork(
            async ct =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            },
            TimeSpan.FromSeconds(10),
            dispositions.Enqueue
        );
        scheduler.TrySchedule(work);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.TrySchedule(work);
        await Task.WhenAll(scheduler.DisposeAsync().AsTask(), scheduler.DisposeAsync().AsTask());
        await Assert.That(dispositions.Count(x => x == ShadowDisposition.Cancelled)).IsEqualTo(2);
    }

    [Test]
    public async Task FailuresTimeoutsAndObserverExceptionsDoNotStopWorkers()
    {
        await using var scheduler = new InMemoryShadowScheduler(4, 1);
        var dispositions = new ConcurrentQueue<ShadowDisposition>();
        scheduler.TrySchedule(
            new(
                _ => throw new InvalidOperationException(),
                TimeSpan.FromSeconds(2),
                dispositions.Enqueue
            )
        );
        scheduler.TrySchedule(
            new(
                async ct => await Task.Delay(Timeout.InfiniteTimeSpan, ct),
                TimeSpan.FromMilliseconds(30),
                dispositions.Enqueue
            )
        );
        scheduler.TrySchedule(
            new(
                _ => ValueTask.CompletedTask,
                TimeSpan.FromSeconds(2),
                _ => throw new InvalidOperationException()
            )
        );
        scheduler.TrySchedule(
            new(_ => ValueTask.CompletedTask, TimeSpan.FromSeconds(2), dispositions.Enqueue)
        );
        await scheduler.CompleteAsync();
        await Assert
            .That(dispositions.ToArray())
            .IsEquivalentTo(
                new[]
                {
                    ShadowDisposition.Failed,
                    ShadowDisposition.TimedOut,
                    ShadowDisposition.Completed,
                }
            );
    }

    [Test]
    public async Task ConcurrencyIsLimitedAndAmbientContextIsNotInherited()
    {
        var ambient = new AsyncLocal<string?> { Value = "request" };
        await using var scheduler = new InMemoryShadowScheduler(4, 2);
        var release = NewSignal();
        var both = NewSignal();
        var running = 0;
        var max = 0;
        var seen = new ConcurrentQueue<string?>();
        var work = new ShadowWork(
            async ct =>
            {
                seen.Enqueue(ambient.Value);
                var current = Interlocked.Increment(ref running);
                if (current == 2)
                {
                    max = current;
                    both.TrySetResult();
                }
                await release.Task.WaitAsync(ct);
                Interlocked.Decrement(ref running);
            },
            TimeSpan.FromSeconds(5)
        );
        for (var i = 0; i < 4; i++)
        {
            await Assert.That(scheduler.TrySchedule(work)).IsTrue();
        }
        await both.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(running).IsEqualTo(2);
        release.TrySetResult();
        await scheduler.CompleteAsync();
        await Assert.That(max).IsEqualTo(2);
        await Assert.That(seen.Count).IsEqualTo(4);
        await Assert.That(seen.All(value => value is null)).IsTrue();
    }

    [Test]
    public async Task WorkItemsDoNotShareAmbientState()
    {
        var ambient = new AsyncLocal<string?>();
        await using var scheduler = new InMemoryShadowScheduler(2, 1);
        string? seen = "not-run";
        scheduler.TrySchedule(
            new(
                _ =>
                {
                    ambient.Value = "first";
                    return ValueTask.CompletedTask;
                },
                TimeSpan.FromSeconds(2)
            )
        );
        scheduler.TrySchedule(
            new(
                _ =>
                {
                    seen = ambient.Value;
                    return ValueTask.CompletedTask;
                },
                TimeSpan.FromSeconds(2)
            )
        );
        await scheduler.CompleteAsync();
        await Assert.That(seen).IsNull();
    }

    [Test]
    public async Task CancellingDrainCancelsRunningAndQueuedWork()
    {
        await using var scheduler = new InMemoryShadowScheduler(1, 1);
        var entered = NewSignal();
        var results = new ConcurrentQueue<ShadowDisposition>();
        var work = new ShadowWork(
            async ct =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            },
            TimeSpan.FromSeconds(5),
            results.Enqueue
        );
        scheduler.TrySchedule(work);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.TrySchedule(work);
        using var cancellation = new CancellationTokenSource();
        var completion = scheduler.CompleteAsync(cancellation.Token);
        cancellation.Cancel();
        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(results.Count(x => x == ShadowDisposition.Cancelled)).IsEqualTo(2);
    }
}
