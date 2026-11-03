using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Zeeq.LlmCmd;

// WHY a deferred, mutable, one-use builder: registering a part must never spend money. Nothing runs, claims or reads
// until ExecuteAsync, so wiring mistakes (wrong slot, duplicate, missing, dependency cycle) surface before any provider
// call. The leaf Invocation stays immutable; the builder only collects them.

/// <summary>What the engine needs to build a call once its dependencies are stored. Non-generic so the engine needs no type parameters.</summary>
sealed record DependencyInput(OutputRef Ref, JsonElement Value);

sealed record SourceCore(
    IReadOnlyList<string> Dependencies,
    Func<IReadOnlyDictionary<string, DependencyInput>, PreparedCall> Build,
    string? Problem
);

/// <summary>Where a part comes from: a command invocation, possibly built from earlier stored parts. Opaque on purpose.</summary>
public sealed class PartSource<TPart>
{
    internal PartSource(SourceCore core)
    {
        Core = core;
    }

    internal SourceCore Core { get; }

    // WHY freeze now: parameters may hold mutable collections. What is fingerprinted and run must be what the caller registered.
    internal static PartSource<TPart> From<TP>(Invocation<TP, TPart> call)
    {
        var prepared = InvocationBridge.Prepare(call);
        return new(new SourceCore([], _ => prepared, null));
    }
}

/// <summary>
/// A reference to another slot's stored output, usable as a dependency. WHY Then takes a callback and not a value:
/// the value does not exist until the other slot's command ran and its output was STORED. The callback receives the
/// exact stored version (Pinned) and returns the next Invocation, so the dependent call is built from stored evidence.
/// </summary>
public sealed class StepRef<TPart>
{
    readonly string slotId;
    readonly JsonTypeInfo<TPart>? info;
    readonly string? problem;

    internal StepRef(string slotId, JsonTypeInfo<TPart>? info, string? problem)
    {
        this.slotId = slotId;
        this.info = info;
        this.problem = problem;
    }

    internal string SlotId => slotId;

    /// <summary>Defers a dependent invocation until this step has an exact stored output.</summary>
    public PartSource<TTarget> Then<TTarget, TP>(Func<Pinned<TPart>, Invocation<TP, TTarget>> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return new(new SourceCore([slotId], inputs => Build(next(Read(inputs))), problem));
    }

    /// <summary>Defers a dependent invocation until both prerequisite outputs are pinned.</summary>
    public PartSource<TTarget> Then<TTarget, TP, TOther>(
        StepRef<TOther> other,
        Func<Pinned<TPart>, Pinned<TOther>, Invocation<TP, TTarget>> next
    )
    {
        ArgumentNullException.ThrowIfNull(other);
        ArgumentNullException.ThrowIfNull(next);
        return new(
            new SourceCore(
                [slotId, other.slotId],
                inputs => Build(next(Read(inputs), other.Read(inputs))),
                problem ?? other.problem
            )
        );
    }

    static PreparedCall Build<TP, TTarget>(Invocation<TP, TTarget>? call) =>
        InvocationBridge.Prepare(
            call ?? throw new InvalidOperationException("Dependent callback returned null")
        );

    internal Pinned<TPart> Read(IReadOnlyDictionary<string, DependencyInput> inputs)
    {
        // WHY deserialize here from the re-read ledger JSON and not hand back the earlier typed value: the stored version is
        // the fact. A callback sees exactly what a later reader of the ledger would see.
        var input = inputs[slotId];
        var value =
            JsonSerializer.Deserialize(
                input.Value,
                info ?? throw new InvalidOperationException("No JSON type info")
            ) ?? throw new InvalidOperationException("Stored dependency is null");
        return new(value, input.Ref);
    }
}

/// <summary>Typed access to prerequisite steps of a composition.</summary>
public sealed class StepCatalog<TWhole>
{
    readonly CompositeDescriptor<TWhole>? descriptor;

    internal StepCatalog(CompositeDescriptor<TWhole>? descriptor)
    {
        this.descriptor = descriptor;
    }

    /// <summary>References the step responsible for a typed slot.</summary>
    public StepRef<TPart> Of<TPart>(PartSlot<TWhole, TPart> slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        // WHY resolve TypeInfo from the whole's options: it is the same source-generated metadata the aggregate is validated
        // with, so no reflection and no second serializer configuration to drift.
        if (
            descriptor is not null
            && descriptor.Whole.Options.TryGetTypeInfo(typeof(TPart), out var found)
            && found is JsonTypeInfo<TPart> typed
        )
        {
            return new(slot.SlotId, typed, null);
        }

        return new(slot.SlotId, null, "no_json_typeinfo:" + slot.SlotId);
    }
}

enum RegistrationKind
{
    Execute,
    Reuse,
}

sealed record SlotRegistration(
    SlotDescriptor Slot,
    RegistrationKind Kind,
    CallSite Site,
    SourceCore? Source,
    OutputRef? Pinned,
    Func<JsonElement, string?>? Shape = null
);

/// <summary>The frozen input of one execution.</summary>
sealed record CompositionPlan<TWhole>(
    CompositeDescriptor<TWhole>? Descriptor,
    CompositionRequest Request,
    DraftRecord? Draft,
    CompositeOutcome<TWhole>? Early,
    IReadOnlyList<SlotRegistration> Registrations,
    IReadOnlyList<string> Problems
);

/// <summary>Deferred composition plan for assembling and publishing a typed whole.</summary>
public sealed class CompositionRoot<TWhole>
{
    readonly CompositionEngine engine;
    readonly CompositeDescriptor<TWhole>? descriptor;
    readonly CompositionRequest request;
    readonly DraftRecord? draft;
    readonly CompositeOutcome<TWhole>? early;
    readonly object gate = new();
    readonly List<SlotRegistration> registrations = [];
    readonly List<string> problems = [];
    TaskCompletionSource<CompositeOutcome<TWhole>>? execution;

    internal CompositionRoot(
        CompositionEngine engine,
        CompositeDescriptor<TWhole>? descriptor,
        CompositionRequest request,
        DraftRecord? draft,
        CompositeOutcome<TWhole>? early
    )
    {
        this.engine = engine;
        this.descriptor = descriptor;
        this.request = request;
        this.draft = draft;
        this.early = early;
        Steps = new(descriptor);
    }

    /// <summary>Typed references used to declare dependencies between slots.</summary>
    public StepCatalog<TWhole> Steps { get; }

    /// <summary>The draft this handle will fill. Null if opening failed (the failure is reported by ExecuteAsync).</summary>
    public Guid? DraftId => draft?.DraftId;

    /// <summary>Registers a command invocation for a slot without executing it.</summary>
    public CompositionRoot<TWhole> WithPartFrom<TP, TPart>(
        PartSlot<TWhole, TPart> slot,
        Invocation<TP, TPart> call,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(call);
        return Register(
            new(
                slot.Descriptor,
                RegistrationKind.Execute,
                new(member, file, line),
                PartSource<TPart>.From(call).Core,
                null
            )
        );
    }

    /// <summary>Registers a deferred or pinned part source for a slot without executing it.</summary>
    public CompositionRoot<TWhole> WithPartFrom<TPart>(
        PartSlot<TWhole, TPart> slot,
        PartSource<TPart> source,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(source);
        return Register(
            new(
                slot.Descriptor,
                RegistrationKind.Execute,
                new(member, file, line),
                source.Core,
                null
            )
        );
    }

    /// <summary>Attach an existing stored output instead of running a command. The runner re-reads and authorizes it; Value is not trusted.</summary>
    public CompositionRoot<TWhole> ReusePart<TPart>(
        PartSlot<TWhole, TPart> slot,
        Pinned<TPart> pinned
    )
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(pinned);
        // WHY check the shape now: a reused artifact has no leaf command to validate it, and with no replace API a wrongly shaped part
        // would poison the draft. The same source-generated metadata that validates the aggregate decides.
        // WHY null is refused: a part is a stored, non-null document. The leaf runtime already rejects a null model result, so "optional"
        // means the slot may be absent from the aggregate, never present-as-null. Missing and null stay distinct that way.
        Func<JsonElement, string?>? shape = null;
        if (
            descriptor is not null
            && descriptor.Whole.Options.TryGetTypeInfo(typeof(TPart), out var info)
            && info is JsonTypeInfo<TPart> typed
        )
        {
            shape = element =>
            {
                if (element.ValueKind == JsonValueKind.Null)
                {
                    return "pinned_null";
                }

                try
                {
                    return JsonSerializer.Deserialize(element, typed) is not null
                        ? null
                        : "pinned_null";
                }
                catch (JsonException)
                {
                    return "pinned_wrong_shape";
                }
            };
        }

        return Register(
            new(
                slot.Descriptor,
                RegistrationKind.Reuse,
                new("", "", 0),
                null,
                pinned.Reference,
                shape
            )
        );
    }

    CompositionRoot<TWhole> Register(SlotRegistration registration)
    {
        lock (gate)
        {
            // WHY throw here (and only here): once ExecuteAsync froze the handle its result is memoized. A later registration
            // cannot retroactively change that result, and ignoring it silently would hide a real bug.
            if (execution is not null)
            {
                throw new InvalidOperationException(
                    "The composition handle is frozen: ExecuteAsync already started."
                );
            }
            // Duplicates and wrong slots stay deferred: they become ConfigurationFailure at ExecuteAsync with zero spend.
            registrations.Add(registration);
        }
        return this;
    }

    /// <summary>
    /// Runs the composition once per handle. Repeat calls return the same result. WHY memoize: two awaits of one handle must
    /// not mean two rounds of provider spend. The first caller's token governs the shared work; a later caller's token only
    /// stops that caller from waiting.
    /// </summary>
    public async Task<CompositeOutcome<TWhole>> ExecuteAsync(CancellationToken ct = default)
    {
        TaskCompletionSource<CompositeOutcome<TWhole>> shared;
        bool first = false;
        CompositionPlan<TWhole> plan;
        lock (gate)
        {
            if (execution is null)
            {
                execution = new(TaskCreationOptions.RunContinuationsAsynchronously);
                first = true;
            }
            shared = execution;
            plan = new(
                descriptor,
                request,
                draft,
                early,
                registrations.ToArray(),
                problems.ToArray()
            );
        }
        if (first)
        {
            try
            {
                shared.SetResult(await engine.RunAsync(plan, ct).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                shared.SetException(ex);
            }
            return await shared.Task.ConfigureAwait(false);
        }
        // A caller can reuse a handle after the authenticated subject changes.
        // Never return a cached aggregate without authorizing this caller too.
        if (await engine.AuthorizeReplayAsync(plan).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        try
        {
            return await shared.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new CompositeOutcome<TWhole>.Cancelled(DraftId, []);
        }
    }
}
