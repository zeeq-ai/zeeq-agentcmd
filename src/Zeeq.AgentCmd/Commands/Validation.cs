namespace Zeeq.AgentCmd;

/// <summary>
/// Opts a generated command into compiler-checked validation of its input.
/// </summary>
/// <typeparam name="TParameters">The command's input type.</typeparam>
/// <remarks>
/// The generated command checks its structural contract before calling this hook.
/// Validation can run more than once and on background threads, so implementations
/// should be deterministic, synchronous, and independent of ambient state. A direct
/// generic call to this interface runs only the authored hook, not the structural checks.
/// </remarks>
public interface IInputValidator<TParameters>
{
    /// <summary>Checks the effective input before a provider call.</summary>
    /// <param name="input">The input to validate.</param>
    /// <returns>An error reason, or null when the input is valid.</returns>
    static abstract string? ValidateInput(TParameters input);
}

/// <summary>
/// Opts a generated command into compiler-checked validation of its result.
/// </summary>
/// <typeparam name="TResult">The command's result type.</typeparam>
/// <remarks>
/// The generated command checks its structural contract before calling this hook.
/// Implementations should be deterministic, synchronous, and independent of ambient
/// state. A direct generic call to this interface runs only the authored hook, not the
/// structural checks.
/// </remarks>
public interface IResultValidator<TResult>
{
    /// <summary>Checks a structurally valid provider result.</summary>
    /// <param name="result">The result to validate.</param>
    /// <returns>An error reason, or null when the result is valid.</returns>
    static abstract string? ValidateResult(TResult result);
}
