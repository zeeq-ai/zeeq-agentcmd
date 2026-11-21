using System.Diagnostics;

namespace Zeeq.AgentCmd;

/// <summary>
/// The routed attempt a provider call belongs to. Correlation runs command → routed attempt →
/// provider call: one routed attempt can make several provider calls when inner middleware retries
/// or loops over tools, so capture assigns each call its own ID and keeps this record as its parent.
/// Correlation links records; it is not authority and does not prove that anything was stored.
/// </summary>
/// <param name="Command">Execution identity captured when the command started.</param>
/// <param name="RouteAttemptId">Router attempt identifier; matches <see cref="ExecutionEvent.AttemptId"/>.</param>
/// <param name="AttemptNumber">One-based routed attempt number, counting retries and fallback.</param>
/// <param name="Profile">Profile selected for this attempt, when routing is profile-based.</param>
/// <param name="Parent">Trace context of the attempt span, so an exporter can parent a later observation correctly.</param>
public sealed record AttemptCorrelation(
    CommandExecutionContext Command,
    Guid RouteAttemptId,
    int AttemptNumber,
    string? Profile,
    ActivityContext Parent
);

/// <summary>
/// Flow-local carrier for <see cref="AttemptCorrelation"/>. The runtime sets it only around each
/// provider send, and provider capture reads it once when a call starts. Reading once matters: a
/// queued sink or a late, timed-out completion must keep the attempt it started under, never the
/// scope of whatever runs later. <c>ChatOptions.AdditionalProperties</c> is not used because some
/// providers serialize it into the request body.
/// </summary>
public static class AttemptScope
{
    sealed record Frame(AttemptCorrelation Value, Frame? Parent);

    static readonly AsyncLocal<Frame?> current = new();

    /// <summary>The routed attempt for the current flow, or null outside a command's provider send.</summary>
    public static AttemptCorrelation? Current => current.Value?.Value;

    /// <summary>
    /// Sets the attempt for the current flow until the returned scope is disposed. Disposal restores
    /// the previous value, so nested and concurrent sends do not see each other's attempts. Hosts with
    /// a custom router use this to give provider capture the same correlation.
    /// </summary>
    /// <param name="correlation">Attempt that the enclosed provider calls belong to.</param>
    public static IDisposable Begin(AttemptCorrelation correlation)
    {
        ArgumentNullException.ThrowIfNull(correlation);

        var frame = new Frame(correlation, current.Value);
        current.Value = frame;

        return new Scope(frame);
    }

    sealed class Scope(Frame frame) : IDisposable
    {
        public void Dispose()
        {
            // Out-of-order disposal is a no-op, as in AgentCmdContext: a child flow keeps what it inherited.
            if (ReferenceEquals(current.Value, frame))
            {
                current.Value = frame.Parent;
            }
        }
    }
}
