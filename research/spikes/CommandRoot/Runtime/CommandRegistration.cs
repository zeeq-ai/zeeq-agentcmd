using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using HumanGrading;
using LlmCmd.ContextPropagation;

namespace CommandRoot;

public sealed record CommandRegistration(string Id,int Revision,Type CommandType,Type ParameterType,Type ResultType,string DefaultProfile="");
public sealed record CommandContractError(string CommandId,string Code);
public interface IGeneratedCommandCatalog
{
    static abstract void Register(IServiceCollection services);
    static abstract IReadOnlyList<CommandRegistration> Commands { get; }
    static abstract IReadOnlyList<CommandContractError> ValidateContracts();
}
public static class CommandServices
{
    /// <summary>Compile-time generated registrations. The host supplies its own opaque CommandRuntime.</summary>
    public static IServiceCollection AddLlmCommands<TCatalog>(this IServiceCollection services)
        where TCatalog : IGeneratedCommandCatalog
    {
        ValidateContracts<TCatalog>();
        AddCompositionServices(services);
        TCatalog.Register(services); return services;
    }
    /// <summary>Register one runtime and validate every generated contract and default profile at setup.</summary>
    public static IServiceCollection AddLlmCommands<TCatalog>(this IServiceCollection services,CommandRuntime runtime)
        where TCatalog : IGeneratedCommandCatalog
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ValidateContracts<TCatalog>(); runtime.ValidateProfiles(TCatalog.Commands);
        services.AddSingleton(runtime); AddCompositionServices(services); TCatalog.Register(services); return services;
    }
    static void ValidateContracts<TCatalog>() where TCatalog : IGeneratedCommandCatalog
    {
        var errors=TCatalog.ValidateContracts();
        if(errors.Count>0) throw new InvalidOperationException("Invalid generated command contracts: "+string.Join(", ",errors.Select(e=>e.CommandId)));
    }
    static void AddCompositionServices(IServiceCollection services)
    {
        // A single host registration must bind leaves and roots to the same
        // ledger and actor accessor. Existing host overrides remain explicit.
        services.TryAddSingleton<IHumanFeedbackLedger>(sp=>sp.GetRequiredService<CommandRuntime>().CompositionLedger);
        services.TryAddSingleton<Func<Actor>>(sp=>sp.GetRequiredService<CommandRuntime>().ResolveCompositionActor);
        services.TryAddSingleton<IExecutionEventSink, RuntimeEvents>();
        services.AddCompositionRuntime();
    }

    // Register a forwarding sink rather than discovering one from registered
    // parts: an empty or invalid root still needs to report its captured context.
    // TryAdd keeps an explicit host-wide observer authoritative.
    sealed class RuntimeEvents(CommandRuntime runtime) : IExecutionEventSink
    {
        public ValueTask EmitAsync(LlmEvent record, CancellationToken cancellationToken) =>
            runtime.CompositionEvents?.EmitAsync(record,cancellationToken) ?? ValueTask.CompletedTask;
    }
}
