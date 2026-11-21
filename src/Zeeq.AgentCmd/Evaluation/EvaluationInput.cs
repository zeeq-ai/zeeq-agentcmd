namespace Zeeq.AgentCmd;

/// <summary>
/// The command inputs retained for a later evaluation, paired with the exact stored output. The ledger
/// stores output JSON and feedback but not the prompt or parameters, so a host that grades with inputs
/// in view must keep them itself. This record is the shape; the host chooses storage and retention.
/// </summary>
/// <param name="ExecutionId">Execution that produced the output.</param>
/// <param name="Snapshot">Captured definition, template, rendered prompt and parameters. Treat it as sensitive.</param>
/// <param name="Output">Exact stored output the inputs belong to.</param>
public sealed record EvaluationInput(
    Guid ExecutionId,
    ExecutionSnapshot Snapshot,
    OutputRef Output
);

/// <summary>Host-owned destination for <see cref="EvaluationInput"/>. Choose access control, redaction and retention here.</summary>
public interface IEvaluationInputStore
{
    /// <summary>Saves the retained input.</summary>
    /// <param name="input">Inputs paired with their stored output.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask SaveAsync(EvaluationInput input, CancellationToken cancellationToken);
}

/// <summary>Closed result of retaining a command's inputs.</summary>
public closed record RetainResult
{
    /// <summary>The inputs were handed to the store.</summary>
    /// <param name="Input">What was saved.</param>
    public sealed record Retained(EvaluationInput Input) : RetainResult;

    /// <summary>The output was not stored, so there is no exact output to pair the inputs with.</summary>
    public sealed record NotStored : RetainResult;
}

/// <summary>Retains command inputs next to the stored output they produced.</summary>
public static class EvaluationInputExtensions
{
    /// <summary>
    /// Saves the snapshot with the stored output reference. A valid value without a stored delivery is
    /// not an evaluation artifact, so that case is reported instead of saved.
    /// </summary>
    /// <param name="success">Successful command outcome.</param>
    /// <param name="store">Host-owned destination.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <typeparam name="TResult">Command result type.</typeparam>
    public static async ValueTask<RetainResult> RetainAsync<TResult>(
        this Outcome<TResult>.Success success,
        IEvaluationInputStore store,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(success);
        ArgumentNullException.ThrowIfNull(store);

        if (success.Delivery is not OutputDelivery.Stored stored)
        {
            return new RetainResult.NotStored();
        }

        var input = new EvaluationInput(
            success.ExecutionId,
            success.Snapshot,
            stored.Output.Reference
        );
        await store.SaveAsync(input, cancellationToken).ConfigureAwait(false);

        return new RetainResult.Retained(input);
    }
}
