namespace Zeeq.AgentCmd;

/// <summary>Typed preparation evidence, not an execution receipt or a guarantee that future host policy will remain unchanged.</summary>
/// <param name="Ready">Whether the exact requested candidate prepared successfully.</param>
/// <param name="FailureCode">Sanitized blocking reason, absent on success.</param>
/// <param name="CommandId">Compiled command identity.</param>
/// <param name="CommandRevision">Compiled definition revision.</param>
/// <param name="DefinitionHash">Compiled definition fingerprint.</param>
/// <param name="ContractSignature">Generated runtime contract identity, when available.</param>
/// <param name="System">Rendered system message, disclosed only to the authorized preview caller.</param>
/// <param name="User">Rendered user message.</param>
/// <param name="Profile">Prepared profile.</param>
/// <param name="Model">Requested model, when configured.</param>
/// <param name="Temperature">Prepared sampling setting.</param>
/// <param name="Evidence">Exact effective candidate evidence; no provider attempt is performed by preflight.</param>
public sealed record CommandPreflight(
    bool Ready,
    string? FailureCode,
    string CommandId,
    int CommandRevision,
    string DefinitionHash,
    string? ContractSignature,
    string? System = null,
    string? User = null,
    string? Profile = null,
    string? Model = null,
    float? Temperature = null,
    ExperimentEvidence? Evidence = null
);

public sealed partial class Invocation<TP, TR>
{
    /// <summary>Validates and renders the exact strict candidate without invoking a provider or writing an output.</summary>
    /// <param name="cancellationToken">Cancels preparation, including source reads; cancellation never selects a default.</param>
    /// <returns>Build-independent typed preparation evidence. The host separately attests its build and manifest.</returns>
    /// <remarks>
    /// Requires an explicit operation context and expected candidate. Authoritative validation runs in the typed host,
    /// not in a browser interpreting a schema. Custom validation hooks and source/policy reads do run. Execution
    /// prepares and checks the candidate again so a later policy change cannot authorize an unintended send.
    /// </remarks>
    public Task<CommandPreflight> PreflightAsync(CancellationToken cancellationToken = default)
    {
        var error = state.Error;
        if (state.Context is null || state.Suppressed)
        {
            error ??= "preflight_requires_explicit_context";
        }
        if (state.Candidate is null)
        {
            error ??= "preflight_requires_strict_candidate";
        }
        if (error is not null)
        {
            return Task.FromResult(
                new CommandPreflight(
                    false,
                    error,
                    descriptor.Id,
                    descriptor.Revision,
                    descriptor.DefinitionHash,
                    descriptor.RuntimeBinding?.ContractSignature
                )
            );
        }
        var captured = AgentCmdContext.Capture(state.Context, state.Suppressed);
        var context = new CommandExecutionContext(
            Guid.NewGuid(),
            descriptor.Id,
            descriptor.Revision,
            descriptor.DefinitionHash,
            captured
        );
        return runtime.PreflightAsync(
            descriptor,
            parameters,
            state.Profile,
            state.Temperature,
            state.ProfileExplicit,
            context,
            state.Candidate,
            error,
            cancellationToken
        );
    }
}

public sealed partial class CommandRuntime
{
    internal async Task<CommandPreflight> PreflightAsync<TParameters, TResult>(
        PromptDescriptor<TParameters, TResult> descriptor,
        TParameters parameters,
        string profile,
        float? temperature,
        bool explicitProfile,
        CommandExecutionContext context,
        ExpectedExperimentCandidate? expected,
        string? pendingError,
        CancellationToken cancellationToken
    )
    {
        CommandPreflight Rejected(string reason) =>
            new(
                false,
                reason,
                descriptor.Id,
                descriptor.Revision,
                descriptor.DefinitionHash,
                descriptor.RuntimeBinding?.ContractSignature
            );
        cancellationToken.ThrowIfCancellationRequested();
        if (pendingError is not null)
        {
            return Rejected(pendingError);
        }
        if (disposed)
        {
            return Rejected("runtime_disposed");
        }
        if (Experiments is null || descriptor.RuntimeBinding is null || expected is null)
        {
            return Rejected(ExperimentCandidateFailureReasons.ExperimentsDisabled);
        }
        try
        {
            var actor = actorProvider();
            if (
                actor is null
                || string.IsNullOrWhiteSpace(actor.Subject)
                || string.IsNullOrWhiteSpace(actor.Tenant)
                || context.Operation.RunId is not { } runId
                || !ledger.CanAccessRun(runId, actor)
            )
            {
                return Rejected("preflight_run_not_accessible");
            }
            var frozen = FreezeExperimentInput(descriptor, parameters);
            if (!frozen.SafeSnapshot)
            {
                return Rejected("preflight_input_snapshot_unavailable");
            }
            var prepared = await PrepareExperimentAsync(
                descriptor,
                frozen,
                profile,
                temperature,
                explicitProfile,
                context,
                actor,
                null,
                cancellationToken,
                expected: expected
            );
            if (prepared.Invalid is not null)
            {
                return Rejected("preflight_input_invalid");
            }
            return new(
                true,
                null,
                descriptor.Id,
                descriptor.Revision,
                descriptor.DefinitionHash,
                descriptor.RuntimeBinding.ContractSignature,
                prepared.System,
                prepared.Prompt,
                prepared.Settings.Profile,
                prepared.Settings.Model,
                prepared.Settings.Temperature,
                prepared.Evidence
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ExperimentCandidateRejectedException error)
        {
            return Rejected(error.Reason);
        }
        catch (ValidationHookException)
        {
            return Rejected("preflight_validation_hook_failed");
        }
        catch
        {
            return Rejected("preflight_preparation_failed");
        }
    }
}
