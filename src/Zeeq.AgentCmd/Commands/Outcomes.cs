
namespace Zeeq.AgentCmd;

// Failure is deliberately not generic. A projection or dependent call can forward
// this exact object, including its original context, without copying N case fields.
/// <summary>Failure preserved across value projections and dependent calls.</summary>
/// <param name="Context">Original command execution context.</param>
/// <param name="Snapshot">Captured definition and settings, when available.</param>
public closed record ExecutionFailure(CommandExecutionContext Context, ExecutionSnapshot? Snapshot)
{
    /// <summary>Identifier of the execution that failed.</summary>
    public Guid ExecutionId => Context.ExecutionId;
    /// <summary>Input validation failed.</summary>
    /// <param name="Context">Original execution context.</param>
    /// <param name="Reason">Sanitized reason or phase.</param>
    /// <param name="Snapshot">Execution snapshot, when available.</param>
    public sealed record InvalidInput(CommandExecutionContext Context, string Reason, ExecutionSnapshot? Snapshot = null) : ExecutionFailure(Context, Snapshot);
    /// <summary>The provider call failed.</summary>
    /// <param name="Context">Original execution context.</param>
    /// <param name="Reason">Sanitized reason or phase.</param>
    /// <param name="Snapshot">Execution snapshot, when available.</param>
    public sealed record ProviderFailure(CommandExecutionContext Context, string Reason, ExecutionSnapshot? Snapshot = null) : ExecutionFailure(Context, Snapshot);
    /// <summary>A command hook failed.</summary>
    /// <param name="Context">Original execution context.</param>
    /// <param name="Phase">Sanitized reason or phase.</param>
    /// <param name="Snapshot">Execution snapshot, when available.</param>
    public sealed record HookFailure(CommandExecutionContext Context, string Phase, ExecutionSnapshot? Snapshot = null) : ExecutionFailure(Context, Snapshot);
    /// <summary>Execution configuration was invalid.</summary>
    /// <param name="Context">Original execution context.</param>
    /// <param name="Reason">Sanitized reason or phase.</param>
    /// <param name="Snapshot">Execution snapshot, when available.</param>
    public sealed record ConfigurationFailure(CommandExecutionContext Context, string Reason, ExecutionSnapshot? Snapshot = null) : ExecutionFailure(Context, Snapshot);
    /// <summary>Provider output did not satisfy the result contract.</summary>
    /// <param name="Context">Original execution context.</param>
    /// <param name="Reason">Sanitized reason or phase.</param>
    /// <param name="Snapshot">Execution snapshot, when available.</param>
    public sealed record InvalidOutput(CommandExecutionContext Context, string Reason, ExecutionSnapshot? Snapshot = null) : ExecutionFailure(Context, Snapshot);
    /// <summary>The caller cancelled execution.</summary>
    /// <param name="Context">Original command execution context.</param>
    /// <param name="Snapshot">Captured definition and settings, when available.</param>
    public sealed record Cancelled(CommandExecutionContext Context, ExecutionSnapshot? Snapshot = null) : ExecutionFailure(Context, Snapshot);

    /// <summary>Stable machine-readable failure category.</summary>
    public string Kind => this switch
    {
        InvalidInput => "invalid_input",
        ProviderFailure => "provider_failure",
        HookFailure => "hook_failure",
        ConfigurationFailure => "configuration_failure",
        InvalidOutput => "invalid_output",
        Cancelled => "cancelled"
    };
}

/// <summary>Typed command result retaining its original execution context.</summary>
/// <param name="Context">Execution context that produced the outcome.</param>
public closed record Outcome<T>(CommandExecutionContext Context)
{
    /// <summary>Identifier of the originating execution.</summary>
    public Guid ExecutionId => Context.ExecutionId;
    /// <summary>A valid typed result and its output delivery receipt.</summary>
    /// <param name="Value">Validated typed result.</param>
    /// <param name="RunId">Owning run identifier.</param>
    /// <param name="Delivery">Ledger delivery outcome.</param>
    /// <param name="Caller">Invocation call site.</param>
    /// <param name="Snapshot">Captured execution definition and settings.</param>
    /// <param name="Context">Original execution context.</param>
    public sealed record Success(T Value, Guid RunId, OutputDelivery Delivery, CallSite Caller,
        ExecutionSnapshot Snapshot, CommandExecutionContext Context) : Outcome<T>(Context);
    /// <summary>An execution failure, without a typed value.</summary>
    /// <param name="Failure">Original failure and context.</param>
    public sealed record Failed(ExecutionFailure Failure) : Outcome<T>(Failure.Context);
}

/// <summary>Value combinators that preserve failure provenance.</summary>
public static class OutcomeExtensions
{
    // Map projects the application value. The receipt still describes the source
    // execution/artifact; this operation does not persist a new output version.
    /// <summary>Projects a successful value without creating or persisting another output.</summary>
    public static Outcome<U> Map<T, U>(this Outcome<T> outcome, Func<T, U> map) => outcome switch
    {
        Outcome<T>.Success s => new Outcome<U>.Success(map(s.Value), s.RunId, s.Delivery, s.Caller, s.Snapshot, s.Context),
        Outcome<T>.Failed f => new Outcome<U>.Failed(f.Failure)
    };

    // Pure value combinators do not catch programmer exceptions. Deferred plan
    // callbacks have a different boundary: the composition runner records them.
    /// <summary>Runs a dependent function on success and forwards failures unchanged.</summary>
    public static Outcome<U> Bind<T, U>(this Outcome<T> outcome, Func<T, Outcome<U>> next) => outcome switch
    {
        Outcome<T>.Success s => next(s.Value),
        Outcome<T>.Failed f => new Outcome<U>.Failed(f.Failure)
    };

    /// <summary>Runs an asynchronous dependent function on success and forwards failures unchanged.</summary>
    public static async Task<Outcome<U>> BindAsync<T, U>(this Outcome<T> outcome,
        Func<T, CancellationToken, Task<Outcome<U>>> next, CancellationToken ct = default) => outcome switch
        {
            Outcome<T>.Success s => await next(s.Value, ct).ConfigureAwait(false),
            Outcome<T>.Failed f => new Outcome<U>.Failed(f.Failure)
        };

    /// <summary>Awaits an outcome and binds its successful value to an asynchronous function.</summary>
    public static async Task<Outcome<U>> BindAsync<T, U>(this Task<Outcome<T>> outcome,
        Func<T, CancellationToken, Task<Outcome<U>>> next, CancellationToken ct = default) =>
        await (await outcome.ConfigureAwait(false)).BindAsync(next, ct).ConfigureAwait(false);

    /// <summary>Selects the success or failure handler for the outcome.</summary>
    public static R Match<T, R>(this Outcome<T> outcome, Func<T, R> success, Func<ExecutionFailure, R> failure) => outcome switch
    {
        Outcome<T>.Success s => success(s.Value),
        Outcome<T>.Failed f => failure(f.Failure)
    };
}
