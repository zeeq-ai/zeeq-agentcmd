using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

namespace Zeeq.LlmCmd;

/// <summary>Base for a generated typed command definition bound to a runtime.</summary>
public abstract class PromptCommand<TP, TR>
{
    readonly CommandRuntime runtime;
    readonly PromptDescriptor<TP, TR> descriptor;

    /// <summary>Binds the command definition to the runtime that executes its invocations.</summary>
    protected PromptCommand(CommandRuntime runtime, PromptDescriptor<TP, TR> descriptor)
    {
        this.runtime = runtime;
        this.descriptor = descriptor;
    }

    /// <summary>Creates an immutable invocation with the supplied typed parameters; no provider call is made.</summary>
    public Invocation<TP, TR> WithParameters(TP parameters) =>
        Invocation<TP, TR>.Create(runtime, descriptor, parameters);
}
