using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

namespace Zeeq.LlmCmd;

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
        string? Error = null
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
            : Copy(state with { Profile = name });

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

    /// <summary>Captures context and call site, then executes the command and returns its typed outcome.</summary>
    public Task<Outcome<TR>> ExecuteAsync(
        CancellationToken ct = default,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        // Capture here, synchronously. A queued emitter or later await never reads AsyncLocal again.
        var captured = LlmCmdContext.Capture(state.Context, state.Suppressed);
        var context = new CommandExecutionContext(
            Guid.NewGuid(),
            descriptor.Id,
            descriptor.Revision,
            descriptor.DefinitionHash,
            captured
        );
        return runtime.ExecuteAsync(
            descriptor,
            parameters,
            state.Profile,
            state.Temperature,
            context,
            state.Error,
            new(member, file, line),
            ct
        );
    }
}
