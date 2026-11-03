# Composition runtime: public contract (draft v1)

Namespace: `CommandRoot`. All types live in `spikes/CommandRoot/Runtime/Composition/**`.
Status: draft. Names change only with a note here.

`CompositionRoot<TWhole>` is the deferred handle. It is a mutable, one-use builder. `ExecuteAsync` freezes it into an immutable plan.

## Attribute

```csharp
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public sealed class CompositeOutputAttribute(string id) : Attribute
{
    public string Id { get; } = id;
    public int Revision { get; init; } = 1;
    // Optional explicit serializer context. The generator uses it for JsonTypeInfo<TWhole> when set.
    public Type? JsonContext { get; init; }
}
```

## Slots and descriptor

```csharp
public sealed record SlotDescriptor(string SlotId, string JsonName, Type PartType, bool Required);

public sealed class PartSlot<TWhole, TPart>
{
    public SlotDescriptor Descriptor { get; }
    public static PartSlot<TWhole, TPart> Declare(string slotId, string jsonName, bool required = true);
}

public interface ICompositeDescriptor
{
    Type WholeType { get; }
    string Id { get; }
    int Revision { get; }
    string DefinitionHash { get; }
    IReadOnlyList<SlotDescriptor> Slots { get; }
}

public sealed record CompositeDescriptor<TWhole>(
    string Id, int Revision, string DefinitionHash,
    JsonTypeInfo<TWhole> Whole, IReadOnlyList<SlotDescriptor> Slots,
    Func<TWhole, string?>? Validate = null) : ICompositeDescriptor;
```

- `SlotId` is the stable responsibility id. `JsonName` is the serialized name. They are separate on purpose.
- `PartSlot` is sealed and invariant.
- `DefinitionHash` covers Id, Revision, and the ordered SlotId/JsonName/PartType/Required values. The runtime stores it in the manifest and rejects a draft opened with another hash.

## Root handle

```csharp
public sealed class CompositionRoot<TWhole>
{
    public StepCatalog<TWhole> Steps { get; }

    public CompositionRoot<TWhole> WithPartFrom<TP, TPart>(PartSlot<TWhole, TPart> slot, Invocation<TP, TPart> call,
        [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0);

    public CompositionRoot<TWhole> WithPartFrom<TPart>(PartSlot<TWhole, TPart> slot, PartSource<TPart> source,
        [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0);

    public CompositionRoot<TWhole> ReusePart<TPart>(PartSlot<TWhole, TPart> slot, Pinned<TPart> pinned);

    // Memoized: repeat calls on one handle return the same Task.
    public Task<CompositeOutcome<TWhole>> ExecuteAsync(CancellationToken ct = default);
}
```

Caller info: registration records member/file/line as the slot's `CallSite`. The bridge passes it into the leaf execution, so the leaf `Outcome.Caller` is the application site. Generated wrappers must declare the three caller attributes and forward them explicitly:

```csharp
public static CompositionRoot<DocumentExtraction> WithHeaderFrom<TP>(
    this CompositionRoot<DocumentExtraction> root, Invocation<TP, Header> call,
    [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    => root.WithPartFrom(Slots.Header, call, member, file, line);
```

Misuse (wrong slot, duplicate slot, registration after execute) does not throw. It becomes `CompositeOutcome.ConfigurationFailure` at `ExecuteAsync` with zero provider spend. Null arguments throw `ArgumentNullException`.

## Steps (dependencies)

```csharp
public sealed class StepCatalog<TWhole> { public StepRef<TPart> Of<TPart>(PartSlot<TWhole, TPart> slot); }

public sealed class StepRef<TPart>
{
    public PartSource<TTarget> Then<TTarget, TP>(Func<Pinned<TPart>, Invocation<TP, TTarget>> next);
    public PartSource<TTarget> Then<TTarget, TP, TOther>(StepRef<TOther> other,
        Func<Pinned<TPart>, Pinned<TOther>, Invocation<TP, TTarget>> next);
}

public sealed class PartSource<TPart> { /* opaque; created by StepRef.Then */ }

public sealed record Pinned<T>(T Value, OutputRef Reference);
```

The runner re-reads the pinned value from the ledger before it calls the callback. A callback exception becomes a step failure on the step envelope. It never escapes `ExecuteAsync`.

## Entry and DI

```csharp
public sealed record CompositionRequest(Guid RunId, string InputRevision, string Key, string? TrialId = null);

public interface IRootCommands
{
    Task<CompositionRoot<TWhole>> BeginAsync<TWhole>(CompositionRequest request, CancellationToken ct = default);
}

public static class CompositionServiceCollectionExtensions
{
    public static IServiceCollection AddCompositionRuntime(this IServiceCollection services);
    public static IServiceCollection AddCompositeDescriptor<TWhole>(this IServiceCollection services, CompositeDescriptor<TWhole> descriptor);
    public static IServiceCollection AddCompositeDescriptor(this IServiceCollection services, ICompositeDescriptor descriptor);
}
```

`BeginAsync` is idempotent on RunId + InputRevision + Key. An unknown `TWhole` becomes `ConfigurationFailure` at execute.

## Outcome

`CompositeOutcome<TWhole>` is a closed record: `Published | Incomplete | Conflict | Unavailable | ConfigurationFailure | Cancelled`.

## Aggregate projection policy

The stored aggregate JSON is not re-serialized from `TWhole`. The runtime builds a `JsonObject` from the exact component wire JSON at each slot's `JsonName`. Explicit `null` stays `null`. It then validates by deserializing through `descriptor.Whole`. So `WhenWritingNull` on the whole type cannot drop a required nullable member from the stored aggregate. Each slot `JsonName` must match a property name that `JsonTypeInfo<TWhole>` reads. The runtime checks this and fails with `ConfigurationFailure` otherwise.

## Needed from root (all use is isolated in `InvocationBridge.cs`)

1. Internal `Invocation` identity snapshot (command id, revision, profile, parameter records, context).
2. Internal `ExecuteComponentAsync(OperationContext, Guid executionId, string slotId, Guid stepId, CallSite, CancellationToken)`. Until it exists the bridge uses the public `InContext(...).ExecuteAsync(ct, member, file, line)` and trusts `Outcome.ExecutionId`.
3. `ProducerRef.Composition` and `RecordProducedOutput(..., ProducerRef, ...)`. Until then the runtime uses its own `AggregateRef`.
4. Optional: `SlotId` on `CommandExecutionContext`. Until then the runtime uses WorkId = StepId and composition outbox events.

## Known limits (v1)

- In-memory store only. No durability, leases, or fencing.
- `Pending` means unknown spend. There is no exactly-once model-spend claim.
- No slot replace or revision API.
