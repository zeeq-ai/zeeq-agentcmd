using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Zeeq.LlmCmd;

/// <summary>Generated command metadata used for registration and profile validation.</summary>
/// <param name="Id">Stable command key.</param>
/// <param name="Revision">Definition revision.</param>
/// <param name="CommandType">Generated command CLR type.</param>
/// <param name="ParameterType">Input parameter CLR type.</param>
/// <param name="ResultType">Result CLR type.</param>
/// <param name="DefaultProfile">Command profile override, or empty for the host default.</param>
public sealed record CommandRegistration(
    string Id,
    int Revision,
    Type CommandType,
    Type ParameterType,
    Type ResultType,
    string DefaultProfile = ""
);

/// <summary>A generated command contract that could not be validated.</summary>
/// <param name="CommandId">Command definition key.</param>
/// <param name="Code">Machine-readable validation category.</param>
public sealed record CommandContractError(string CommandId, string Code);

/// <summary>Static generated catalog for command registrations and contract validation.</summary>
public interface IGeneratedCommandCatalog
{
    /// <summary>Adds generated commands to the service collection.</summary>
    static abstract void Register(IServiceCollection services);

    /// <summary>Command metadata used for runtime profile validation.</summary>
    static abstract IReadOnlyList<CommandRegistration> Commands { get; }

    /// <summary>Validates generated serializer contracts before commands run.</summary>
    static abstract IReadOnlyList<CommandContractError> ValidateContracts();
}

/// <summary>Registers generated commands with an application-owned runtime.</summary>
public static class CommandServices
{
    /// <summary>Compile-time generated registrations. The host supplies its own opaque CommandRuntime.</summary>
    public static IServiceCollection AddLlmCommands<TCatalog>(this IServiceCollection services)
        where TCatalog : IGeneratedCommandCatalog
    {
        ArgumentNullException.ThrowIfNull(services);
        ValidateContracts<TCatalog>();
        // Inspect registrations only. Resolving a provider here would invoke client factories
        // and can create a cycle through composition services.
        var runtimeDescriptor = services.LastOrDefault(d =>
            d.ServiceType == typeof(CommandRuntime)
        );
        if (runtimeDescriptor?.ImplementationInstance is CommandRuntime runtime)
        {
            runtime.ValidateProfiles(TCatalog.Commands);
        }
        else if (
            services
                .LastOrDefault(d => d.ServiceType == typeof(LlmCmdProfileConfiguration))
                ?.ImplementationInstance
                is LlmCmdProfileConfiguration configuration
            && ReferenceEquals(
                runtimeDescriptor?.ImplementationFactory,
                configuration.RuntimeFactory
            )
        )
        {
            configuration.Validate(TCatalog.Commands);
        }
        else if (runtimeDescriptor?.ImplementationFactory is { } factory)
        {
            // An opaque factory cannot be inspected at setup. Validate its runtime
            // on resolution, without resolving the service recursively.
            var index = services.IndexOf(runtimeDescriptor);
            services[index] = ServiceDescriptor.Describe(
                typeof(CommandRuntime),
                sp =>
                {
                    var resolved = (CommandRuntime)factory(sp);
                    try
                    {
                        resolved.ValidateProfiles(TCatalog.Commands);
                    }
                    catch
                    {
                        // DI never receives a rejected factory-created singleton to dispose it.
                        try
                        {
                            resolved.Dispose();
                        }
                        catch { }
                        throw;
                    }
                    return resolved;
                },
                runtimeDescriptor.Lifetime
            );
        }
        AddCompositionServices(services);
        TCatalog.Register(services);
        return services;
    }

    /// <summary>Register one runtime and validate every generated contract and default profile at setup.</summary>
    public static IServiceCollection AddLlmCommands<TCatalog>(
        this IServiceCollection services,
        CommandRuntime runtime
    )
        where TCatalog : IGeneratedCommandCatalog
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ValidateContracts<TCatalog>();
        runtime.ValidateProfiles(TCatalog.Commands);
        services.AddSingleton(runtime);
        AddCompositionServices(services);
        TCatalog.Register(services);
        return services;
    }

    static void ValidateContracts<TCatalog>()
        where TCatalog : IGeneratedCommandCatalog
    {
        var errors = TCatalog.ValidateContracts();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Invalid generated command contracts: "
                    + string.Join(", ", errors.Select(e => e.CommandId))
            );
        }
    }

    static void AddCompositionServices(IServiceCollection services)
    {
        // A single host registration must bind leaves and roots to the same
        // ledger and actor accessor. Existing host overrides remain explicit.
        services.TryAddSingleton<IHumanFeedbackLedger>(sp =>
            sp.GetRequiredService<CommandRuntime>().CompositionLedger
        );
        services.TryAddSingleton<Func<Actor>>(sp =>
            sp.GetRequiredService<CommandRuntime>().ResolveCompositionActor
        );
        services.TryAddSingleton<IExecutionEventSink, RuntimeEvents>();
        services.AddCompositionRuntime();
    }

    // Register a forwarding sink rather than discovering one from registered
    // parts: an empty or invalid root still needs to report its captured context.
    // TryAdd keeps an explicit host-wide observer authoritative.
    sealed class RuntimeEvents(CommandRuntime runtime) : IExecutionEventSink
    {
        public ValueTask EmitAsync(LlmEvent record, CancellationToken cancellationToken) =>
            runtime.CompositionEvents?.EmitAsync(record, cancellationToken)
            ?? ValueTask.CompletedTask;
    }
}
