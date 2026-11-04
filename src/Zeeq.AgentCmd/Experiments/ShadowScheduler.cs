using System.Threading.Channels;

namespace Zeeq.AgentCmd;

/// <summary>Terminal disposition of one offered shadow item.</summary>
public enum ShadowDisposition
{
    /// <summary>The work completed successfully.</summary>
    Completed,

    /// <summary>The work threw an exception.</summary>
    Failed,

    /// <summary>The work observed its time limit.</summary>
    TimedOut,

    /// <summary>Shutdown cancelled or abandoned the work.</summary>
    Cancelled,

    /// <summary>The scheduler did not admit the work.</summary>
    Rejected,
}

/// <summary>One process-local shadow execution and its independent outcome observer.</summary>
/// <param name="Execute">Work that owns its own scope and cooperates with cancellation.</param>
/// <param name="Timeout">Positive time limit beginning when a worker starts the item.</param>
/// <param name="Observe">Optional best-effort, nonblocking terminal disposition observer.</param>
/// <remarks>The delegate cannot be persisted or recovered after a process restart. Durable work needs a separate serializable envelope and handler. It must capture frozen values, not a request scope or ambient authority.</remarks>
public sealed record ShadowWork(
    Func<CancellationToken, ValueTask> Execute,
    TimeSpan Timeout,
    Action<ShadowDisposition>? Observe = null
);

/// <summary>Host-replaceable nonblocking admission port for independent shadow work.</summary>
public interface IShadowScheduler
{
    /// <summary>Offers work without waiting for capacity or execution.</summary>
    /// <param name="work">Frozen process-local work with its own lifetime.</param>
    /// <returns>True only if the scheduler accepted the item.</returns>
    bool TrySchedule(ShadowWork work);
}

/// <summary>
/// A bounded, process-local scheduler that supervises shadow work independently of a primary result.
/// Accepted work is not durable and is never retried. A host requiring recovery must replace this port.
/// </summary>
public sealed class InMemoryShadowScheduler : IShadowScheduler, IAsyncDisposable
{
    readonly Channel<ShadowWork> queue;
    readonly CancellationTokenSource shutdown = new();
    readonly Task workers;
    readonly object gate = new();
    bool stopped;
    Task? disposal;

    /// <summary>Creates a bounded scheduler with a fixed number of supervised workers.</summary>
    /// <param name="capacity">Maximum number of items waiting for a worker.</param>
    /// <param name="concurrency">Maximum number of concurrently executing items.</param>
    public InMemoryShadowScheduler(int capacity, int concurrency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency);
        queue = Channel.CreateBounded<ShadowWork>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = concurrency == 1,
                AllowSynchronousContinuations = false,
            }
        );
        // Ambient request context is neither a scope nor authority for background work.
        using (ExecutionContext.SuppressFlow())
        {
            workers = Task.WhenAll(
                Enumerable.Range(0, concurrency).Select(_ => Task.Run(WorkerAsync))
            );
        }
    }

    /// <inheritdoc />
    public bool TrySchedule(ShadowWork work)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(work.Execute);
        if (
            work.Timeout <= TimeSpan.Zero
            || work.Timeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1)
        )
        {
            throw new ArgumentOutOfRangeException(nameof(work));
        }
        bool accepted;
        lock (gate)
        {
            accepted = !stopped && queue.Writer.TryWrite(work);
        }
        if (!accepted)
        {
            Notify(work, ShadowDisposition.Rejected);
        }
        return accepted;
    }

    /// <summary>Stops admission and waits for accepted work to drain.</summary>
    /// <param name="cancellationToken">Cancellation requests shutdown and cancels queued and running work.</param>
    /// <returns>A task that completes after all workers have stopped.</returns>
    /// <remarks>Cancellation and timeouts are cooperative; a delegate that ignores its token can delay completion.</remarks>
    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            stopped = true;
            queue.Writer.TryComplete();
        }
        using var registration = cancellationToken.Register(CancelWork);
        await workers.ConfigureAwait(false);
    }

    /// <summary>Cancels accepted work and waits for all workers to stop.</summary>
    /// <returns>A task that completes when supervised work has ended.</returns>
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            stopped = true;
            queue.Writer.TryComplete();
            disposal ??= Task.Run(DisposeCoreAsync);
            return new(disposal);
        }
    }

    async Task DisposeCoreAsync()
    {
        CancelWork();
        await workers.ConfigureAwait(false);
        // Do not dispose the token source: a concurrent CompleteAsync may still register cancellation.
    }

    void CancelWork()
    {
        try
        {
            shutdown.Cancel();
        }
        catch (AggregateException)
        {
            // A work item can register a failing cancellation callback.
        }
    }

    async Task WorkerAsync()
    {
        await foreach (var work in queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (shutdown.IsCancellationRequested)
            {
                Notify(work, ShadowDisposition.Cancelled);
                continue;
            }
            using var timeout = new CancellationTokenSource(work.Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                shutdown.Token,
                timeout.Token
            );
            ShadowDisposition disposition;
            try
            {
                Task execution;
                // Isolate ambient state between items as well as from the submitting request.
                using (ExecutionContext.SuppressFlow())
                {
                    execution = Task.Run(async () =>
                        await work.Execute(linked.Token).ConfigureAwait(false)
                    );
                }
                await execution.ConfigureAwait(false);
                disposition =
                    shutdown.IsCancellationRequested ? ShadowDisposition.Cancelled
                    : timeout.IsCancellationRequested ? ShadowDisposition.TimedOut
                    : ShadowDisposition.Completed;
            }
            catch (Exception)
            {
                disposition =
                    shutdown.IsCancellationRequested ? ShadowDisposition.Cancelled
                    : timeout.IsCancellationRequested ? ShadowDisposition.TimedOut
                    : ShadowDisposition.Failed;
            }
            Notify(work, disposition);
        }
    }

    static void Notify(ShadowWork work, ShadowDisposition disposition)
    {
        try
        {
            work.Observe?.Invoke(disposition);
        }
        catch
        {
            // Observers cannot change the primary outcome or stop a worker.
        }
    }
}
