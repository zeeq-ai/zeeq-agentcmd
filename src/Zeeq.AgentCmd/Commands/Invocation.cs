using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

namespace Zeeq.AgentCmd;

/// <summary>
/// Immutable description of one typed command call. Configuration returns a new
/// invocation so branches can safely share a starting call. Only ExecuteAsync
/// captures ambient context and starts work.
/// </summary>
public sealed partial class Invocation<TP, TR>
{
    sealed record InvocationState(
        string Profile,
        float? Temperature = null,
        OperationContext? Context = null,
        bool Suppressed = false,
        string? Error = null,
        bool ProfileExplicit = false,
        IExecutionJournal? Journal = null,
        ExpectedExperimentCandidate? Candidate = null
    );

    readonly CommandRuntime runtime;
    readonly PromptDescriptor<TP, TR> descriptor;
    readonly TP parameters;
    readonly InvocationState state;

    private Invocation(
        CommandRuntime runtime,
        PromptDescriptor<TP, TR> descriptor,
        TP parameters,
        InvocationState state
    )
    {
        this.runtime = runtime;
        this.descriptor = descriptor;
        this.parameters = parameters;
        this.state = state;
    }

    Invocation<TP, TR> Copy(InvocationState value) =>
        new(runtime, descriptor, parameters, value, prepared);

    // Keep the first configuration error until the terminal. Fluent setup must
    // not bypass the normal failure envelope or lose the caller's context.
    Invocation<TP, TR> Error(string phase) => Copy(state with { Error = state.Error ?? phase });

    internal static Invocation<TP, TR> Create(
        CommandRuntime runtime,
        PromptDescriptor<TP, TR> descriptor,
        TP parameters
    ) => new(runtime, descriptor, parameters, new(descriptor.Profile));

    /// <summary>Returns an invocation using the named profile; invalid configuration is reported at execution.</summary>
    public Invocation<TP, TR> WithProfile(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? Error("profile_invalid")
            : Copy(state with { Profile = name, ProfileExplicit = true });

    /// <summary>Returns an invocation using the declared profile key.</summary>
    public Invocation<TP, TR> WithProfile(ProfileKey key) =>
        key is null ? Error("profile_null") : WithProfile(key.Id);

    /// <summary>Selects a profile from a typed catalog; selector failures are reported at execution.</summary>
    public Invocation<TP, TR> WithProfile<TCatalog>(Func<TCatalog, ProfileKey> select)
        where TCatalog : IProfileCatalog<TCatalog>
    {
        try
        {
            return WithProfile(select(TCatalog.Instance));
        }
        catch
        {
            return Error("profile_selector_failed");
        }
    }

    /// <summary>Returns an invocation with a sampling temperature override.</summary>
    public Invocation<TP, TR> WithTemperature(float value) =>
        Copy(state with { Temperature = value });

    /// <summary>Applies settings overrides to a new invocation; callback failures are reported at execution.</summary>
    public Invocation<TP, TR> Configure(Action<ChatSettingsPatch> configure)
    {
        try
        {
            var patch = new ChatSettingsPatch();
            configure(patch);
            return Copy(state with { Temperature = patch.Temperature ?? state.Temperature });
        }
        catch
        {
            return Error("configure_failed");
        }
    }

    /// <summary>Uses the supplied operation context instead of the ambient context.</summary>
    public Invocation<TP, TR> InContext(OperationContext context) =>
        context is null
            ? Error("context_null")
            : Copy(state with { Context = context, Suppressed = false });

    /// <summary>Suppresses ambient operation context for this invocation.</summary>
    public Invocation<TP, TR> WithoutContext() =>
        Copy(state with { Context = null, Suppressed = true });

    /// <summary>Creates an explicit correlation context for the given run; it does not grant access.</summary>
    public Invocation<TP, TR> InRun(Guid runId)
    {
        if (runId == Guid.Empty)
        {
            return Error("run_invalid");
        }

        return InContext(OperationContext.ForRun(runId));
    }

    /// <summary>Returns an invocation whose provider attempts are recorded in the supplied journal.</summary>
    /// <param name="journal">
    /// Job-bound journal, usually tied to one worker claim. A failed start append blocks the provider send;
    /// a failed finish append is not fenced and leaves the attempt for the host to reconcile.
    /// </param>
    /// <returns>A new invocation; the original is unchanged.</returns>
    /// <remarks>
    /// The journal travels with this immutable invocation, not with the runtime, so concurrent jobs cannot
    /// replace each other's journal. Journal events carry the execution context, so an explicit
    /// <see cref="InContext"/> or <see cref="InRun"/> is required; ambient context is rejected at execution.
    /// Composition steps keep their own slot journal and report a bound journal as a configuration error.
    /// </remarks>
    public Invocation<TP, TR> WithJournal(IExecutionJournal journal) =>
        journal is null ? Error("journal_null") : Copy(state with { Journal = journal });

    /// <summary>Returns an invocation that must prepare exactly the expected candidate before any send.</summary>
    /// <param name="candidate">Planned treatment or configured default.</param>
    /// <returns>A new invocation; the original is unchanged.</returns>
    /// <remarks>
    /// A mismatch or failed preparation returns a configuration failure with a reason from
    /// <see cref="ExperimentCandidateFailureReasons"/> and sends nothing. It never falls back to the
    /// configured default. Invocations without a candidate keep the ordinary production fallback.
    /// </remarks>
    public Invocation<TP, TR> RequireExperimentCandidate(ExpectedExperimentCandidate candidate) =>
        candidate switch
        {
            null => Error(ExperimentCandidateFailureReasons.Null),
            ExpectedExperimentCandidate.Treatment treatment
                when treatment.Revision is null
                    || string.IsNullOrWhiteSpace(treatment.ExperimentId)
                    || string.IsNullOrWhiteSpace(treatment.VariantId) => Error(
                ExperimentCandidateFailureReasons.Invalid
            ),
            ExpectedExperimentCandidate.ConfiguredDefault configured
                when string.IsNullOrWhiteSpace(configured.ExpectedFingerprint) => Error(
                ExperimentCandidateFailureReasons.Invalid
            ),
            _ => Copy(state with { Candidate = candidate }),
        };

    /// <summary>Captures context and call site, then executes the command and returns its typed outcome.</summary>
    public Task<Outcome<TR>> ExecuteAsync(
        CancellationToken ct = default,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        // Capture here, synchronously. A queued emitter or later await never reads AsyncLocal again.
        var captured = AgentCmdContext.Capture(state.Context, state.Suppressed);
        var context = new CommandExecutionContext(
            Guid.NewGuid(),
            descriptor.Id,
            descriptor.Revision,
            descriptor.DefinitionHash,
            captured
        );
        FrozenExperimentInput<TP>? frozen = null;
        var error = state.Error;

        // A journal entry is evidence for one worker claim. Ambient context could belong to an
        // unrelated request, so journaled work must name its run explicitly.
        if (state.Journal is not null && state.Context is null)
        {
            error ??= "journal_requires_explicit_context";
        }

        string? hookPhase = null;
        if (runtime.Experiments is not null)
        {
            try
            {
                frozen = runtime.FreezeExperimentInput(descriptor, parameters);
            }
            catch (ValidationHookException)
            {
                hookPhase = "ValidateInput";
            }
            catch
            {
                error ??= "experiment_input_freeze_failed";
            }
        }
        return runtime.ExecuteAsync(
            descriptor,
            parameters,
            state.Profile,
            state.Temperature,
            context,
            error,
            new(member, file, line),
            ct,
            journal: state.Journal,
            frozenExperiment: frozen,
            explicitProfile: state.ProfileExplicit,
            pendingHookPhase: hookPhase,
            expectedCandidate: state.Candidate
        );
    }
}
