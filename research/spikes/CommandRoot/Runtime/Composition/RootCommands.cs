using System.Text.Json.Serialization.Metadata;
using HumanGrading;
using LlmCmd.ContextPropagation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace CommandRoot;

public interface IRootCommands
{
    /// <summary>
    /// Opens (or re-opens) the draft for this request and returns a deferred handle. Idempotent on tenant + run + definition +
    /// input revision + key + trial. Bad requests do not throw: they come back as a handle whose ExecuteAsync reports the failure.
    /// </summary>
    Task<CompositionRoot<TWhole>> BeginAsync<TWhole>(CompositionRequest request, CancellationToken ct = default);
}

/// <summary>Descriptors by whole type. Validated when added, so a bad generated descriptor fails at startup and not mid-run.</summary>
public sealed class CompositionRegistry
{
    readonly Dictionary<Type, ICompositeDescriptor> byType = [];
    public CompositionRegistry(IEnumerable<ICompositeDescriptor>? descriptors = null)
    {
        foreach (var d in descriptors ?? []) Add(d);
    }

    public void Add(ICompositeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var problems = Validate(descriptor);
        if (problems.Count > 0) throw new ArgumentException("Invalid composite descriptor: " + string.Join(", ", problems), nameof(descriptor));
        lock (byType)
        {
            if (byType.TryGetValue(descriptor.WholeType, out var existing) && !ReferenceEquals(existing, descriptor))
                throw new ArgumentException("A descriptor for " + descriptor.WholeType.Name + " is already registered", nameof(descriptor));
            byType[descriptor.WholeType] = descriptor;
        }
    }

    public CompositeDescriptor<TWhole>? Find<TWhole>()
    {
        lock (byType) return byType.TryGetValue(typeof(TWhole), out var d) ? d as CompositeDescriptor<TWhole> : null;
    }

    public static IReadOnlyList<string> Validate(ICompositeDescriptor d)
    {
        var problems = new List<string>();
        // WHY these key rules: Id, hash and slot ids flow into OperationContext/DraftReference, which only accepts this alphabet.
        if (!CompositionNames.IsKey(d.Id)) problems.Add("id_invalid");
        if (d.Revision <= 0) problems.Add("revision_invalid");
        if (!CompositionNames.IsKey(d.DefinitionHash, 256)) problems.Add("definition_hash_invalid");
        if (d.Slots is null || d.Slots.Count == 0) { problems.Add("no_slots"); return problems; }
        if (d.WholeInfo is null) { problems.Add("no_json_type_info"); return problems; }
        foreach (var dup in d.Slots.GroupBy(s => s.SlotId, StringComparer.Ordinal).Where(g => g.Count() > 1)) problems.Add("duplicate_slot_id:" + dup.Key);
        foreach (var dup in d.Slots.GroupBy(s => s.JsonName, StringComparer.Ordinal).Where(g => g.Count() > 1)) problems.Add("duplicate_json_name:" + dup.Key);
        // WHY check against the serializer metadata: the aggregate is validated by deserializing through it, so a slot JsonName
        // the whole does not read would be silently ignored and the part would vanish from the typed value.
        var properties = d.WholeInfo.Kind == JsonTypeInfoKind.Object
            ? d.WholeInfo.Properties.ToDictionary(p => p.Name, p => p.PropertyType, StringComparer.Ordinal) : null;
        foreach (var s in d.Slots)
        {
            if (!CompositionNames.IsKey(s.SlotId)) problems.Add("slot_id_invalid:" + s.SlotId);
            if (string.IsNullOrEmpty(s.JsonName)) { problems.Add("json_name_empty:" + s.SlotId); continue; }
            if (properties is null) problems.Add("whole_not_object");
            else if (!properties.TryGetValue(s.JsonName, out var type)) problems.Add("json_name_not_in_whole:" + s.SlotId);
            else if (!type.IsAssignableFrom(s.PartType)) problems.Add("part_type_mismatch:" + s.SlotId);
        }
        return problems;
    }
}

/// <summary>Host entry point for composition. The caller supplies the authenticated actor provider and the ledger it already uses for commands.</summary>
public sealed class RootCommands : IRootCommands
{
    readonly CompositionEngine engine; readonly CompositionRegistry registry; readonly ICompositionStore store;
    readonly IHumanFeedbackLedger ledger; readonly Func<Actor> actorProvider;

    public RootCommands(IHumanFeedbackLedger ledger, Func<Actor> actorProvider, ICompositionStore store,
        CompositionRegistry registry, TimeProvider? clock = null, IExecutionEventSink? events = null)
    {
        this.ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        this.actorProvider = actorProvider ?? throw new ArgumentNullException(nameof(actorProvider));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        engine = new(ledger, actorProvider, store, clock ?? TimeProvider.System, events);
    }

    public async Task<CompositionRoot<TWhole>> BeginAsync<TWhole>(CompositionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var descriptor = registry.Find<TWhole>();
        var problems = new List<string>();
        if (descriptor is null) problems.Add("unknown_composite:" + typeof(TWhole).Name);
        if (request.RunId == Guid.Empty) problems.Add("run_invalid");
        if (!CompositionNames.IsKey(request.InputRevision, 256)) problems.Add("input_revision_invalid");
        if (!CompositionNames.IsKey(request.Key)) problems.Add("key_invalid");
        if (request.TrialId is not null && !CompositionNames.IsKey(request.TrialId)) problems.Add("trial_invalid");
        Actor? actor = null;
        try { actor = actorProvider(); } catch (Exception) { /* reported below; never leak the exception text */ }
        if (actor is null || string.IsNullOrWhiteSpace(actor.Tenant) || string.IsNullOrWhiteSpace(actor.Subject)) problems.Add("actor_invalid");
        else if (request.RunId != Guid.Empty)
        {
            // WHY check the run here: the ledger is the authority on who may touch a run, and it fails closed by default.
            bool allowed; try { allowed = ledger.CanAccessRun(request.RunId, actor); } catch (Exception) { allowed = false; }
            if (!allowed) problems.Add("run_not_accessible");
        }
        if (problems.Count > 0 || descriptor is null || actor is null)
            return new(engine, descriptor, request, null, new CompositeOutcome<TWhole>.ConfigurationFailure(problems, null, request));

        var key = new DraftKey(actor.Tenant, request.RunId, descriptor.Id, request.InputRevision, request.Key, request.TrialId);
        try
        {
            var opened = await store.OpenDraftAsync(new(key, descriptor.Revision, descriptor.DefinitionHash,
                descriptor.Slots.Select(s => s.SlotId).ToArray()), ct).ConfigureAwait(false);
            return opened switch
            {
                OpenDraftResult.Opened o => new(engine, descriptor, request, o.Draft, null),
                OpenDraftResult.DefinitionMismatch m => new(engine, descriptor, request, null, new CompositeOutcome<TWhole>.Conflict(
                    m.Existing.DraftId, "definition_mismatch", "The draft was opened under another definition revision or hash", []))
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return new(engine, descriptor, request, null, new CompositeOutcome<TWhole>.Unavailable(null, "store_unavailable", []));
        }
    }
}

public static class CompositionServiceCollectionExtensions
{
    /// <summary>
    /// Registers IRootCommands, the in-memory store and the descriptor registry. The host must already register
    /// IHumanFeedbackLedger and Func&lt;Actor&gt; (the same ones the command runtime uses). Replace ICompositionStore before this call to use a durable store.
    /// </summary>
    public static IServiceCollection AddCompositionRuntime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ICompositionStore>(_ => new InMemoryCompositionStore());
        services.TryAddSingleton(sp => new CompositionRegistry(sp.GetServices<ICompositeDescriptor>()));
        services.TryAddSingleton<IRootCommands>(sp => new RootCommands(sp.GetRequiredService<IHumanFeedbackLedger>(),
            sp.GetRequiredService<Func<Actor>>(), sp.GetRequiredService<ICompositionStore>(),
            sp.GetRequiredService<CompositionRegistry>(), sp.GetService<TimeProvider>(), sp.GetService<IExecutionEventSink>()));
        return services;
    }

    public static IServiceCollection AddCompositeDescriptor<TWhole>(this IServiceCollection services, CompositeDescriptor<TWhole> descriptor) =>
        services.AddCompositeDescriptor((ICompositeDescriptor)descriptor);

    public static IServiceCollection AddCompositeDescriptor(this IServiceCollection services, ICompositeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(descriptor);
        // Fail at host setup, like AddLlmCommands does for command contracts.
        var problems = CompositionRegistry.Validate(descriptor);
        if (problems.Count > 0) throw new InvalidOperationException("Invalid composite descriptor " + descriptor.Id + ": " + string.Join(", ", problems));
        services.AddSingleton(descriptor);
        return services;
    }
}
