using LlmCmd.ContextPropagation;

namespace CommandRoot;

// Failure is deliberately not generic. A projection or dependent call can forward
// this exact object, including its original context, without copying N case fields.
public closed record ExecutionFailure(CommandExecutionContext Context, ExecutionSnapshot? Snapshot)
{
    public Guid ExecutionId => Context.ExecutionId;
    public sealed record InvalidInput(CommandExecutionContext Context, string Reason, ExecutionSnapshot? Snapshot = null) : ExecutionFailure(Context, Snapshot);
    public sealed record ProviderFailure(CommandExecutionContext Context, string Reason, ExecutionSnapshot? Snapshot = null) : ExecutionFailure(Context, Snapshot);
    public sealed record HookFailure(CommandExecutionContext Context, string Phase, ExecutionSnapshot? Snapshot = null) : ExecutionFailure(Context, Snapshot);
    public sealed record ConfigurationFailure(CommandExecutionContext Context, string Reason, ExecutionSnapshot? Snapshot = null) : ExecutionFailure(Context, Snapshot);
    public sealed record InvalidOutput(CommandExecutionContext Context, string Reason, ExecutionSnapshot? Snapshot = null) : ExecutionFailure(Context, Snapshot);
    public sealed record Cancelled(CommandExecutionContext Context, ExecutionSnapshot? Snapshot = null) : ExecutionFailure(Context, Snapshot);

    public string Kind => this switch
    {
        InvalidInput => "invalid_input", ProviderFailure => "provider_failure",
        HookFailure => "hook_failure", ConfigurationFailure => "configuration_failure",
        InvalidOutput => "invalid_output", Cancelled => "cancelled"
    };
}

public closed record Outcome<T>(CommandExecutionContext Context)
{
    public Guid ExecutionId => Context.ExecutionId;
    public sealed record Success(T Value, Guid RunId, OutputDelivery Delivery, CallSite Caller,
        ExecutionSnapshot Snapshot, CommandExecutionContext Context) : Outcome<T>(Context);
    public sealed record Failed(ExecutionFailure Failure) : Outcome<T>(Failure.Context);
}

public static class OutcomeExtensions
{
    // Map projects the application value. The receipt still describes the source
    // execution/artifact; this operation does not persist a new output version.
    public static Outcome<U> Map<T, U>(this Outcome<T> outcome, Func<T, U> map) => outcome switch
    {
        Outcome<T>.Success s => new Outcome<U>.Success(map(s.Value), s.RunId, s.Delivery, s.Caller, s.Snapshot, s.Context),
        Outcome<T>.Failed f => new Outcome<U>.Failed(f.Failure)
    };

    // Pure value combinators do not catch programmer exceptions. Deferred plan
    // callbacks have a different boundary: the composition runner records them.
    public static Outcome<U> Bind<T, U>(this Outcome<T> outcome, Func<T, Outcome<U>> next) => outcome switch
    {
        Outcome<T>.Success s => next(s.Value),
        Outcome<T>.Failed f => new Outcome<U>.Failed(f.Failure)
    };

    public static async Task<Outcome<U>> BindAsync<T, U>(this Outcome<T> outcome,
        Func<T, CancellationToken, Task<Outcome<U>>> next, CancellationToken ct = default) => outcome switch
    {
        Outcome<T>.Success s => await next(s.Value, ct).ConfigureAwait(false),
        Outcome<T>.Failed f => new Outcome<U>.Failed(f.Failure)
    };

    public static async Task<Outcome<U>> BindAsync<T, U>(this Task<Outcome<T>> outcome,
        Func<T, CancellationToken, Task<Outcome<U>>> next, CancellationToken ct = default) =>
        await (await outcome.ConfigureAwait(false)).BindAsync(next, ct).ConfigureAwait(false);

    public static R Match<T, R>(this Outcome<T> outcome, Func<T, R> success, Func<ExecutionFailure, R> failure) => outcome switch
    {
        Outcome<T>.Success s => success(s.Value),
        Outcome<T>.Failed f => failure(f.Failure)
    };
}
