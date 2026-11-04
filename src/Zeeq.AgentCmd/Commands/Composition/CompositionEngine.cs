using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zeeq.AgentCmd;

// The engine turns a frozen plan into stored facts. Order of operations is the design:
//   1. validate wiring (no store writes, no spend)
//   2. per slot, in dependency order and in parallel: claim (authoritative store) -> build call from STORED dependencies -> run -> attach/release
//   3. only if every required slot is attached: assemble from stored wire JSON -> validate -> record aggregate -> seal (CAS)
// WHY claim before spend: a crash after the provider call must leave a visible Claimed slot ("Pending"), never an Empty one that
// looks safe to pay for again. WHY attach/release use CancellationToken.None: the caller already paid, so recording wins over cancel.

sealed record SlotResolution(StepResult Step, OutputRef? Output, bool Infrastructure = false);

sealed record FinishResult(
    AggregateArtifact? Artifact,
    object? Whole,
    string? FailKind,
    string? FailCode,
    string? FailMessage,
    bool Replayed
);

sealed record SlotIntent(RegistrationKind Kind, string? Fingerprint, OutputRef? Pinned);

sealed record RunScope(
    Actor Actor,
    Guid DraftId,
    Guid RunId,
    string DefinitionId,
    int Revision,
    string DefinitionHash,
    string InputRevision,
    string? TrialId,
    ExperimentDraftPin? ExperimentPin = null,
    string? ShadowGroup = null
);

sealed class CompositionEngine(
    IHumanFeedbackLedger ledger,
    Func<Actor> actorProvider,
    ICompositionStore store,
    TimeProvider clock,
    IExecutionEventSink? events = null,
    CommandRuntime? runtime = null
)
{
    // Highest stored outbox sequence already projected to the runtime event sink, per draft. A second Execute projects only what is new.
    readonly Dictionary<Guid, long> projected = [];

    // In-process singleflight. WHY: two handles (or two awaits in one process) on the same slot must share ONE command run.
    // The store claim stays authoritative across processes; this table only lets a local second caller wait instead of reporting Pending.
    // WHY the flight records the leader's INTENT (kind, call fingerprint, pin): a follower may share the leader's result only when it asked for
    // the same thing. Another handle with a different input or pin must not receive (and possibly publish) the leader's output.
    readonly ConcurrentDictionary<
        (Guid Draft, string Slot),
        (SlotIntent Intent, Task<SlotResolution> Task)
    > slotFlights = new();

    // WHY keyed by subject too: a finish result carries the whole value. Another actor must not receive it through a shared flight.
    readonly ConcurrentDictionary<
        (Guid Draft, string Tenant, string Subject),
        Task<FinishResult>
    > finishFlights = new();

    internal async Task<CompositeOutcome<TWhole>> RunAsync<TWhole>(
        CompositionPlan<TWhole> plan,
        CancellationToken ct
    )
    {
        var draftId = plan.Draft?.DraftId;
        // WHY enter the root context here, synchronously, inside this async method: deferred callbacks and any auxiliary call they make then see
        // the draft's context as AgentCmdContext.Current. The async state machine restores the caller's ambient context when this method
        // returns, and each slot task copies it, so siblings and the caller never see each other's frames.
        var root = EnterRoot(plan);
        using var ambient = root?.Scope;
        if (root is not null)
        {
            await EmitAsync(
                    "composition.root.started",
                    root.Context,
                    plan.Descriptor!.Id,
                    null,
                    null,
                    null,
                    null,
                    null
                )
                .ConfigureAwait(false);
        }

        CompositeOutcome<TWhole> outcome;
        try
        {
            outcome = await CoreAsync(plan, root?.Context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = new CompositeOutcome<TWhole>.Cancelled(draftId, []);
        }
        catch (ValidationHookException)
        {
            outcome = ct.IsCancellationRequested
                ? new CompositeOutcome<TWhole>.Cancelled(draftId, [])
                : new CompositeOutcome<TWhole>.Unavailable(
                    draftId,
                    "hook_failure:ValidateInput",
                    []
                );
        }
        // Exception text is never copied into an outcome: it may carry parameter or provider content.
        catch (Exception)
        {
            outcome = new CompositeOutcome<TWhole>.Unavailable(draftId, "engine_failure", []);
        }
        if (root is null)
        {
            // A handle that never reached execution (bad request, unknown composite, no access, store down at Begin) is still an observable
            // root. Without a draft there is only a run, or nothing, to correlate with; the event says so rather than inventing a context.
            OperationContext? run = null;
            try
            {
                if (plan.Request.RunId != Guid.Empty)
                {
                    run = OperationContext.ForRun(plan.Request.RunId);
                }
            }
            catch (ArgumentException) { }
            var (name, code) = Describe(outcome);
            await EmitAsync(
                    "composition.root.rejected",
                    run,
                    plan.Descriptor?.Id ?? "unknown",
                    null,
                    null,
                    null,
                    name,
                    code
                )
                .ConfigureAwait(false);
        }
        else
        {
            await ProjectStoredEventsAsync(root.Context, plan.Descriptor!.Id, draftId!.Value)
                .ConfigureAwait(false);
            var (name, code) = Describe(outcome);
            await EmitAsync(
                    "composition.root.finished",
                    root.Context,
                    plan.Descriptor.Id,
                    null,
                    null,
                    null,
                    name,
                    code
                )
                .ConfigureAwait(false);
        }
        return outcome;
    }

    static (string Name, string? Code) Describe<TWhole>(CompositeOutcome<TWhole> outcome) =>
        outcome switch
        {
            CompositeOutcome<TWhole>.Published => ("published", null),
            CompositeOutcome<TWhole>.Incomplete => ("incomplete", null),
            CompositeOutcome<TWhole>.Conflict c => ("conflict", c.Code),
            CompositeOutcome<TWhole>.Unavailable u => ("unavailable", u.Reason),
            CompositeOutcome<TWhole>.ConfigurationFailure f => (
                "configuration_failure",
                f.Problems.FirstOrDefault()
            ),
            CompositeOutcome<TWhole>.Cancelled => ("cancelled", null),
        };

    // WHY best effort: events are observation. A failing sink must never change what a run returns or what is stored.
    // WHY the captured context: the delivery may run later on another thread, where the ambient scope is gone. The value is read here.
    async Task EmitAsync(
        string kind,
        OperationContext? operation,
        string definitionId,
        string? slotId,
        Guid? stepId,
        Guid? executionId,
        string? outcome,
        string? code,
        long? sequence = null,
        Guid? artifactId = null
    )
    {
        if (events is null)
        {
            return;
        }

        try
        {
            var context = new CompositionEventContext(
                operation is null
                    ? new(null, null, ContextSource.None, false)
                    : AgentCmdContext.Capture(operation),
                definitionId,
                null,
                slotId,
                stepId,
                executionId,
                artifactId,
                null,
                sequence
            );
            await ExecutionEvents
                .EmitSafelyAsync(events, new CompositionEvent(kind, context, outcome, code))
                .ConfigureAwait(false);
        }
        catch (Exception) { }
    }

    // The store's outbox is the authoritative event log. Project each entry once to the runtime sink, ordered by its stored sequence.
    // Journal entries are skipped: the leaf already emitted those as typed ExecutionEvents.
    async Task ProjectStoredEventsAsync(OperationContext root, string definitionId, Guid draftId)
    {
        if (events is null)
        {
            return;
        }

        try
        {
            var draft = await store
                .GetDraftAsync(draftId, CancellationToken.None)
                .ConfigureAwait(false);
            if (draft is null)
            {
                return;
            }

            CompositionOutboxEvent[] fresh;
            lock (projected)
            {
                var from = projected.GetValueOrDefault(draftId);
                fresh = draft
                    .Events.Where(e => e.Sequence > from)
                    .OrderBy(e => e.Sequence)
                    .ToArray();
                if (fresh.Length > 0)
                {
                    projected[draftId] = fresh[^1].Sequence;
                }
            }
            foreach (
                var e in fresh.Where(e => !e.Kind.StartsWith("journal.", StringComparison.Ordinal))
            )
            {
                await EmitAsync(
                        "composition.store." + e.Kind,
                        root,
                        definitionId,
                        e.SlotId,
                        e.StepId,
                        e.ExecutionId,
                        null,
                        e.Detail,
                        e.Sequence
                    )
                    .ConfigureAwait(false);
            }
        }
        catch (Exception) { }
    }

    sealed record RootFrame(IDisposable Scope, OperationContext Context);

    static RootFrame? EnterRoot<TWhole>(CompositionPlan<TWhole> plan)
    {
        if (plan.Early is not null || plan.Draft is null || plan.Descriptor is null)
        {
            return null;
        }

        try
        {
            var context = OperationContext.FromResolvedReference(
                new(
                    Guid.NewGuid(),
                    plan.Request.RunId,
                    new DraftReference(
                        plan.Draft.DraftId,
                        plan.Descriptor.Id,
                        plan.Descriptor.Revision,
                        plan.Descriptor.DefinitionHash,
                        1,
                        plan.Request.InputRevision
                    ),
                    plan.Request.TrialId
                )
            );
            return new(AgentCmdContext.BeginScope(context), context);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // A memoized handle may be awaited by a different request or subject. The
    // cached result is still protected by the ledger's current authorization.
    internal async Task<CompositeOutcome<TWhole>?> AuthorizeReplayAsync<TWhole>(
        CompositionPlan<TWhole> plan
    )
    {
        if (plan.Early is not null || plan.Draft is null)
        {
            return null;
        }

        var problem = AuthorizationProblem(plan.Request, plan.Draft, out _);
        if (problem is null)
        {
            return null;
        }

        OperationContext? context = null;
        try
        {
            var descriptor = plan.Descriptor!;
            context = OperationContext.FromResolvedReference(
                new(
                    Guid.NewGuid(),
                    plan.Request.RunId,
                    new DraftReference(
                        plan.Draft.DraftId,
                        descriptor.Id,
                        descriptor.Revision,
                        descriptor.DefinitionHash,
                        1,
                        plan.Request.InputRevision
                    ),
                    plan.Request.TrialId
                )
            );
        }
        catch (ArgumentException) { }
        await EmitAsync(
                "composition.root.rejected",
                context,
                plan.Descriptor?.Id ?? "unknown",
                null,
                null,
                null,
                "configuration_failure",
                problem
            )
            .ConfigureAwait(false);
        return new CompositeOutcome<TWhole>.ConfigurationFailure(
            [problem],
            plan.Draft.DraftId,
            plan.Request
        );
    }

    string? AuthorizationProblem(CompositionRequest request, DraftRecord draft, out Actor? actor)
    {
        actor = null;
        try
        {
            actor = actorProvider();
        }
        catch (Exception) { }
        if (
            actor is null
            || string.IsNullOrWhiteSpace(actor.Tenant)
            || string.IsNullOrWhiteSpace(actor.Subject)
        )
        {
            return "actor_invalid";
        }

        if (actor.Tenant != draft.Key.Tenant)
        {
            return "actor_tenant_changed";
        }

        try
        {
            return ledger.CanAccessRun(request.RunId, actor) ? null : "run_not_accessible";
        }
        catch (Exception)
        {
            return "run_not_accessible";
        }
    }

    async Task<CompositeOutcome<TWhole>> CoreAsync<TWhole>(
        CompositionPlan<TWhole> plan,
        OperationContext? rootContext,
        CancellationToken ct
    )
    {
        var capturedRoot = rootContext is null ? null : AgentCmdContext.Capture(rootContext);
        if (plan.Early is not null)
        {
            return plan.Early;
        }

        var descriptor = plan.Descriptor!;
        var draftId = plan.Draft!.DraftId;
        if (ct.IsCancellationRequested)
        {
            return new CompositeOutcome<TWhole>.Cancelled(draftId, []);
        }

        // WHY authorize again at execute: the handle may be executed later, or by another subject, than the one that began it. The ledger decides
        // who may touch the run, and this check comes BEFORE any stored draft or reference is read or returned.
        var problem = AuthorizationProblem(plan.Request, plan.Draft, out var actor);
        if (problem is not null)
        {
            return new CompositeOutcome<TWhole>.ConfigurationFailure(
                [problem],
                draftId,
                plan.Request
            );
        }

        DraftRecord fresh;
        try
        {
            fresh = await store.GetDraftAsync(draftId, ct).ConfigureAwait(false) ?? plan.Draft;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new CompositeOutcome<TWhole>.Unavailable(draftId, "store_unavailable", []);
        }

        var (regs, order, problems) = Validate(plan, fresh);
        if (problems.Count > 0)
        {
            return new CompositeOutcome<TWhole>.ConfigurationFailure(
                problems,
                draftId,
                plan.Request
            );
        }

        if (
            plan.ShadowPin is not null
            && plan.Registrations.Any(registration => registration.Kind == RegistrationKind.Reuse)
        )
        {
            return new CompositeOutcome<TWhole>.ConfigurationFailure(
                ["shadow_reuse_forbidden"],
                draftId,
                plan.Request
            );
        }

        ExperimentDraftPin? experimentPin = fresh.ExperimentPin;
        if (runtime?.Experiments is { } experimentOptions)
        {
            if (rootContext is null || capturedRoot is null)
            {
                return new CompositeOutcome<TWhole>.Unavailable(
                    draftId,
                    "experiment_context_invalid",
                    []
                );
            }
            if (string.IsNullOrWhiteSpace(experimentOptions.DefaultConfigurationFingerprint))
            {
                return new CompositeOutcome<TWhole>.Unavailable(
                    draftId,
                    "experiment_default_identity_missing",
                    []
                );
            }
            try
            {
                if (experimentPin is null && plan.ShadowPin is not null)
                {
                    var pinnedShadow = await store
                        .PinExperimentAsync(draftId, plan.ShadowPin, ct)
                        .ConfigureAwait(false);
                    experimentPin = pinnedShadow switch
                    {
                        ExperimentPinResult.Applied applied => applied.Pin,
                        ExperimentPinResult.Existing existing
                            when existing.Pin.Fingerprint == plan.ShadowPin.Fingerprint =>
                            existing.Pin,
                        _ => null,
                    };
                    if (experimentPin is null)
                    {
                        return new CompositeOutcome<TWhole>.Unavailable(
                            draftId,
                            "shadow_pin_rejected",
                            []
                        );
                    }
                }
                if (
                    plan.ShadowPin is not null
                    && experimentPin?.Fingerprint != plan.ShadowPin.Fingerprint
                )
                {
                    return new CompositeOutcome<TWhole>.Unavailable(
                        draftId,
                        "shadow_pin_conflict",
                        []
                    );
                }
                if (experimentPin is null)
                {
                    if (!ExperimentLookup.TryCreate(actor!.Tenant, null, out var lookup))
                    {
                        return new CompositeOutcome<TWhole>.Unavailable(
                            draftId,
                            "experiment_partition_invalid",
                            []
                        );
                    }
                    var unit = experimentOptions.AssignmentUnit?.Invoke(actor);
                    var loaded = await experimentOptions
                        .Resolver.LoadAsync(lookup!, ct)
                        .ConfigureAwait(false);
                    ExperimentDraftPin candidate;
                    if (loaded is ExperimentSnapshotResult.Loaded available)
                    {
                        var selected = await experimentOptions
                            .Resolver.PreselectAsync(
                                lookup!,
                                available.Snapshot,
                                capturedRoot,
                                actor.Subject,
                                unit,
                                ct
                            )
                            .ConfigureAwait(false);
                        if (
                            selected
                                .Choices.Values.OfType<ExperimentChoice.Chosen>()
                                .Any(choice => choice.Assignment.Role != ExperimentRole.Primary)
                        )
                        {
                            // A returned whole is a primary exposure, never a shadow comparison.
                            candidate = ExperimentDraftPin.ForDefault(
                                ExperimentFallbackReason.InvalidRelease,
                                actor.Tenant,
                                experimentOptions.DefaultConfigurationFingerprint,
                                unit
                            );
                        }
                        else
                        {
                            var assignments = selected.Choices.ToDictionary(
                                pair => pair.Key,
                                pair => CompositionExperimentChoices.Encode(pair.Value),
                                StringComparer.Ordinal
                            );
                            candidate = ExperimentDraftPin.ForSnapshot(
                                selected.Snapshot,
                                actor.Tenant,
                                unit,
                                experimentOptions.DefaultConfigurationFingerprint,
                                assignments
                            );
                        }
                    }
                    else
                    {
                        candidate = ExperimentDraftPin.ForDefault(
                            ((ExperimentSnapshotResult.Unavailable)loaded).Reason,
                            actor.Tenant,
                            experimentOptions.DefaultConfigurationFingerprint,
                            unit
                        );
                    }
                    // Known direct inputs are validated before publication. If a treatment is invalid,
                    // select one complete configured-default view before any claim or provider send.
                    if (
                        !await DirectInputsValid(
                                candidate,
                                plan.Registrations,
                                runtime,
                                actor,
                                rootContext,
                                ct
                            )
                            .ConfigureAwait(false)
                    )
                    {
                        if (candidate.Snapshot is null)
                        {
                            return new CompositeOutcome<TWhole>.Unavailable(
                                draftId,
                                "experiment_default_invalid",
                                []
                            );
                        }
                        candidate = ExperimentDraftPin.ForDefault(
                            ExperimentFallbackReason.InvalidRelease,
                            actor.Tenant,
                            experimentOptions.DefaultConfigurationFingerprint,
                            unit
                        );
                        if (
                            !await DirectInputsValid(
                                    candidate,
                                    plan.Registrations,
                                    runtime,
                                    actor,
                                    rootContext,
                                    ct
                                )
                                .ConfigureAwait(false)
                        )
                        {
                            return new CompositeOutcome<TWhole>.Unavailable(
                                draftId,
                                "experiment_default_invalid",
                                []
                            );
                        }
                    }
                    var pinned = await store
                        .PinExperimentAsync(draftId, candidate, ct)
                        .ConfigureAwait(false);
                    experimentPin = pinned switch
                    {
                        ExperimentPinResult.Applied applied => applied.Pin,
                        ExperimentPinResult.Existing existing => existing.Pin,
                        _ => null,
                    };
                    if (
                        experimentPin is null
                        && pinned is ExperimentPinResult.Rejected { Code: "draft_sealed" }
                    )
                    {
                        // The winner may have sealed between our read and CAS. Its retained pin is still authoritative.
                        experimentPin = (
                            await store.GetDraftAsync(draftId, ct).ConfigureAwait(false)
                        )?.ExperimentPin;
                    }
                    if (experimentPin is null)
                    {
                        return new CompositeOutcome<TWhole>.Unavailable(
                            draftId,
                            "experiment_pin_rejected",
                            []
                        );
                    }
                }
                if (
                    experimentPin.Partition != actor!.Tenant
                    || (
                        experimentPin.Snapshot is null
                            ? experimentPin.DefaultConfigurationFingerprint
                            : experimentPin.AssignmentId
                    ) != experimentOptions.DefaultConfigurationFingerprint
                )
                {
                    return new CompositeOutcome<TWhole>.Unavailable(
                        draftId,
                        "experiment_default_identity_changed",
                        []
                    );
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ValidationHookException)
            {
                throw;
            }
            catch (Exception)
            {
                return new CompositeOutcome<TWhole>.Unavailable(
                    draftId,
                    "experiment_pin_unavailable",
                    []
                );
            }
        }
        else if (experimentPin is not null)
        {
            return new CompositeOutcome<TWhole>.Unavailable(
                draftId,
                "experiment_runtime_unavailable",
                []
            );
        }

        var scope = new RunScope(
            actor!,
            draftId,
            plan.Request.RunId,
            descriptor.Id,
            descriptor.Revision,
            descriptor.DefinitionHash,
            plan.Request.InputRevision,
            plan.Request.TrialId,
            experimentPin,
            plan.ShadowGroup
        );

        // Slots already attached by an earlier handle are known facts: dependents may use them without any claim.
        var tasks = new Dictionary<string, Task<SlotResolution>>(StringComparer.Ordinal);
        foreach (
            var slot in fresh.Slots.Values.Where(s =>
                s.Status == SlotStatus.Attached && !regs.ContainsKey(s.SlotId)
            )
        )
        {
            tasks[slot.SlotId] = Task.FromResult(Known(slot));
        }

        foreach (var id in order)
        {
            var reg = regs[id];
            var deps = (reg.Source?.Dependencies ?? [])
                .Select(d => (Id: d, Task: tasks[d]))
                .ToArray();
            tasks[id] = RunSlotAsync(scope, reg, deps, descriptor, ct);
        }
        var resolutions = await Task.WhenAll(order.Select(id => tasks[id])).ConfigureAwait(false);
        var steps = plan
            .Registrations.Select(r => tasks[r.Slot.SlotId])
            .Select(t => t.Result.Step)
            .ToArray();
        // Duplicates were rejected above, so Registrations and `order` cover the same slots.

        // WHY guard from here: every slot has settled and paid. An exception below must not discard the receipts of completed steps.
        try
        {
            return await CompleteAsync(
                    plan,
                    descriptor,
                    scope,
                    actor!,
                    fresh,
                    regs,
                    resolutions,
                    steps,
                    ct
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new CompositeOutcome<TWhole>.Cancelled(draftId, steps);
        }
        catch (Exception)
        {
            return new CompositeOutcome<TWhole>.Unavailable(draftId, "engine_failure", steps);
        }
    }

    private static async Task<bool> DirectInputsValid(
        ExperimentDraftPin candidate,
        IReadOnlyList<SlotRegistration> registrations,
        CommandRuntime runtime,
        Actor actor,
        OperationContext rootContext,
        CancellationToken ct
    )
    {
        foreach (
            var registration in registrations.Where(r =>
                r.Kind == RegistrationKind.Execute && r.Source is { Dependencies.Count: 0 }
            )
        )
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var call = registration.Source!.Build(
                    new Dictionary<string, DependencyInput>(StringComparer.Ordinal)
                );
                if (!ReferenceEquals(call.Runtime, runtime) || !call.UsesExperiments)
                {
                    return false;
                }
                var prepared = await call.PrepareExperimentAsync(candidate, rootContext, actor, ct)
                    .ConfigureAwait(false);
                if (prepared.ExperimentInvalid)
                {
                    return false;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ValidationHookException)
            {
                throw;
            }
            catch (Exception)
            {
                return false;
            }
        }
        return true;
    }

    async Task<CompositeOutcome<TWhole>> CompleteAsync<TWhole>(
        CompositionPlan<TWhole> plan,
        CompositeDescriptor<TWhole> descriptor,
        RunScope scope,
        Actor actor,
        DraftRecord fresh,
        Dictionary<string, SlotRegistration> regs,
        SlotResolution[] resolutions,
        StepResult[] steps,
        CancellationToken ct
    )
    {
        var draftId = scope.DraftId;
        if (resolutions.Any(r => r.Infrastructure))
        {
            return new CompositeOutcome<TWhole>.Unavailable(draftId, "store_unavailable", steps);
        }

        var conflict = steps.FirstOrDefault(s => s.Status == StepStatus.Conflict);
        if (conflict is not null)
        {
            return new CompositeOutcome<TWhole>.Conflict(
                draftId,
                conflict.Code ?? "conflict",
                "A slot conflicts with stored state: " + conflict.SlotId,
                steps
            );
        }

        DraftRecord latest;
        try
        {
            latest =
                await store.GetDraftAsync(draftId, CancellationToken.None).ConfigureAwait(false)
                ?? fresh;
        }
        catch (Exception)
        {
            return new CompositeOutcome<TWhole>.Unavailable(draftId, "store_unavailable", steps);
        }

        var unfilled = latest
            .Slots.Values.Where(s =>
                s.Status != SlotStatus.Attached
                && (
                    descriptor.Slots.First(d => d.SlotId == s.SlotId).Required
                    || regs.ContainsKey(s.SlotId)
                )
            )
            .ToArray();
        if (unfilled.Length > 0)
        {
            return ct.IsCancellationRequested
                ? new CompositeOutcome<TWhole>.Cancelled(draftId, steps)
                : new CompositeOutcome<TWhole>.Incomplete(draftId, steps);
        }

        if (ct.IsCancellationRequested)
        {
            return new CompositeOutcome<TWhole>.Cancelled(draftId, steps);
        }

        var finish = await FinishAsync(scope, descriptor, latest, actor).ConfigureAwait(false);
        if (finish.Artifact is null)
        {
            return finish.FailKind switch
            {
                "unavailable" => new CompositeOutcome<TWhole>.Unavailable(
                    draftId,
                    finish.FailCode ?? "unavailable",
                    steps
                ),
                _ => new CompositeOutcome<TWhole>.Conflict(
                    draftId,
                    finish.FailCode ?? "conflict",
                    finish.FailMessage ?? "Parts do not compose",
                    steps
                ),
            };
        }

        return new CompositeOutcome<TWhole>.Published(
            (TWhole)finish.Whole!,
            draftId,
            finish.Artifact.Reference,
            finish.Artifact.Members,
            steps,
            finish.Replayed
        );
    }

    // Step 1. Everything here is local and free: no store write, no ledger write, no provider call.
    static (
        Dictionary<string, SlotRegistration> Regs,
        List<string> Order,
        List<string> Problems
    ) Validate<TWhole>(CompositionPlan<TWhole> plan, DraftRecord draft)
    {
        var descriptor = plan.Descriptor!;
        var problems = new List<string>(plan.Problems);
        var known = descriptor.Slots.ToDictionary(s => s.SlotId, StringComparer.Ordinal);
        var regs = new Dictionary<string, SlotRegistration>(StringComparer.Ordinal);
        foreach (var r in plan.Registrations)
        {
            // Compare the whole descriptor, not just the id: a slot from another definition with the same id must not be accepted.
            if (!known.TryGetValue(r.Slot.SlotId, out var d) || d != r.Slot)
            {
                problems.Add("unknown_slot:" + r.Slot.SlotId);
                continue;
            }
            if (!regs.TryAdd(r.Slot.SlotId, r))
            {
                problems.Add("duplicate_slot:" + r.Slot.SlotId);
                continue;
            }
            if (r.Source?.Problem is { } p)
            {
                problems.Add(p);
            }
            // A call known now can be checked now. Dependent calls are checked when they are built, still before any claim.
            if (r.Source is { Dependencies.Count: 0 } known0)
            {
                try
                {
                    if (
                        ContractProblem(
                            known0.Build(new Dictionary<string, DependencyInput>()),
                            r.Slot,
                            descriptor
                        ) is
                        { } contract
                    )
                    {
                        problems.Add(contract);
                    }
                }
                catch (Exception)
                {
                    problems.Add("call_unavailable:" + r.Slot.SlotId);
                }
            }
        }
        foreach (var d in descriptor.Slots)
        {
            if (
                d.Required
                && !regs.ContainsKey(d.SlotId)
                && draft.Slots[d.SlotId].Status != SlotStatus.Attached
            )
            {
                problems.Add("missing_required_slot:" + d.SlotId);
            }
        }

        foreach (var r in regs.Values)
        {
            foreach (var dep in r.Source?.Dependencies ?? [])
            {
                if (dep == r.Slot.SlotId)
                {
                    problems.Add("self_dependency:" + dep);
                }
                else if (
                    !regs.ContainsKey(dep)
                    && draft.Slots.GetValueOrDefault(dep)?.Status != SlotStatus.Attached
                )
                {
                    problems.Add("unknown_dependency:" + r.Slot.SlotId + "->" + dep);
                }
            }
        }
        // Kahn's algorithm in registration order. Anything left over is a cycle.
        var order = new List<string>();
        var remaining = regs.Keys.ToList();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        bool progress = true;
        while (remaining.Count > 0 && progress)
        {
            progress = false;
            foreach (var id in remaining.ToArray())
            {
                if (
                    (regs[id].Source?.Dependencies ?? []).All(d =>
                        !regs.ContainsKey(d) || placed.Contains(d)
                    )
                )
                {
                    order.Add(id);
                    placed.Add(id);
                    remaining.Remove(id);
                    progress = true;
                }
            }
        }
        if (
            remaining.Count > 0
            && !problems.Any(p => p.StartsWith("self_dependency", StringComparison.Ordinal))
        )
        {
            problems.Add("dependency_cycle:" + string.Join(",", remaining));
        }

        return (regs, order, problems.Distinct().ToList());
    }

    // WHY refuse instead of remapping: the aggregate copies the component's stored wire JSON unchanged under the slot's JsonName, then
    // reads it with the whole's metadata. If the leaf writes "Title" and the whole reads "title", the part would silently deserialize
    // as missing, after the money is spent. Normalizing names would also mean rewriting the original wire JSON, which is never done.
    static string? ContractProblem(
        PreparedCall call,
        SlotDescriptor slot,
        ICompositeDescriptor descriptor
    )
    {
        System.Text.Json.Serialization.Metadata.JsonTypeInfo leaf;
        try
        {
            leaf = call.ResultContract;
        }
        catch (Exception)
        {
            return "result_contract_missing:" + slot.SlotId;
        }
        if (!descriptor.WholeInfo.Options.TryGetTypeInfo(slot.PartType, out var part))
        {
            return "no_json_typeinfo:" + slot.SlotId;
        }

        return ContractCompatibility.Compatible(leaf, part)
            ? null
            : "incompatible_contract:" + slot.SlotId;
    }

    static SlotResolution Known(SlotRecord slot) =>
        new(
            new(slot.SlotId, StepStatus.Replayed, slot.StepId, slot.ExecutionId, slot.Output),
            slot.Output
        );

    // Every step carries its DraftId and, when this call created it, its own StepId and OperationContext, whichever path produced it.
    async Task<SlotResolution> RunSlotAsync(
        RunScope scope,
        SlotRegistration reg,
        (string Id, Task<SlotResolution> Task)[] deps,
        ICompositeDescriptor descriptor,
        CancellationToken ct
    )
    {
        var stepId = Guid.NewGuid();
        OperationContext? operation = null;
        try
        {
            // WorkId = StepId: every event of this slot, including the ones a leaf command emits, correlates to this one step.
            operation = OperationContext.FromResolvedReference(
                new(
                    Guid.NewGuid(),
                    scope.RunId,
                    new DraftReference(
                        scope.DraftId,
                        scope.DefinitionId,
                        scope.Revision,
                        scope.DefinitionHash,
                        1,
                        scope.InputRevision
                    ),
                    scope.TrialId
                ),
                stepId
            );
        }
        catch (ArgumentException) { }

        SlotResolution result;
        try
        {
            result = await RunSlotCoreAsync(scope, reg, deps, descriptor, stepId, operation, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = new(
                new(reg.Slot.SlotId, StepStatus.Cancelled, Code: "cancelled_before_claim"),
                null
            );
        }
        catch (Exception)
        {
            result = new(
                new(reg.Slot.SlotId, StepStatus.Failed, Code: "engine_failure"),
                null,
                Infrastructure: true
            );
        }

        // A replayed step keeps the identity of the attempt that produced it; only steps THIS call created get this call's identity.
        var own = (result.Step.StepId ?? stepId) == stepId;
        result = result with
        {
            Step = result.Step with
            {
                DraftId = scope.DraftId,
                StepId = result.Step.StepId ?? stepId,
                Operation = own ? operation : null,
            },
        };
        // One composition event per step this call created. ExecutionId is only the leaf's real one: skipped, callback-failed and reused steps have none.
        if (own && operation is not null && result.Step.Status != StepStatus.Replayed)
        {
            await EmitAsync(
                    "composition.step." + result.Step.Status.ToString().ToLowerInvariant(),
                    operation,
                    scope.DefinitionId,
                    reg.Slot.SlotId,
                    result.Step.StepId,
                    result.Step.ExecutionId,
                    result.Step.Status.ToString(),
                    result.Step.Code
                )
                .ConfigureAwait(false);
        }

        return result;
    }

    async Task<SlotResolution> RunSlotCoreAsync(
        RunScope scope,
        SlotRegistration reg,
        (string Id, Task<SlotResolution> Task)[] deps,
        ICompositeDescriptor descriptor,
        Guid stepId,
        OperationContext? operation,
        CancellationToken ct
    )
    {
        var id = reg.Slot.SlotId;
        var depResults = await Task.WhenAll(deps.Select(d => d.Task)).ConfigureAwait(false);
        var blocked = deps.Zip(depResults).Where(p => p.Second.Output is null).ToArray();
        // A dependency that did not attach means this call cannot be built from stored evidence. Skip it: no claim, no spend.
        // The blocking steps stay attached as Causes, so the original failure (code, ExecutionFailure, snapshot) is not lost.
        if (blocked.Length > 0)
        {
            return new(
                new(
                    id,
                    StepStatus.Skipped,
                    stepId,
                    Code: "dependency_not_attached:"
                        + string.Join(",", blocked.Select(b => b.First.Id)),
                    Causes: blocked.Select(b => b.Second.Step).ToArray()
                ),
                null
            );
        }

        if (operation is null)
        {
            return new(new(id, StepStatus.Failed, stepId, Code: "context_invalid"), null);
        }

        var depRefs = deps.Zip(depResults)
            .ToDictionary(p => p.First.Id, p => p.Second.Output!, StringComparer.Ordinal);

        // Build BEFORE joining the flight: the follower must know what it is asking for in order to be compared with the leader.
        var prepared = Prepare(scope, reg, depRefs, descriptor, stepId, operation);
        if (prepared.Failure is not null)
        {
            return prepared.Failure;
        }

        if (prepared.Call is { } call)
        {
            if (scope.ExperimentPin is { } pin)
            {
                if (
                    runtime is null
                    || !ReferenceEquals(call.Runtime, runtime)
                    || !call.UsesExperiments
                )
                {
                    return new(
                        new(id, StepStatus.Failed, stepId, Code: "experiment_runtime_mismatch"),
                        null
                    );
                }
                try
                {
                    prepared = prepared with
                    {
                        Call = await call.PrepareExperimentAsync(
                                pin,
                                operation,
                                scope.Actor,
                                ct,
                                scope.ShadowGroup
                            )
                            .ConfigureAwait(false),
                    };
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (ValidationHookException)
                {
                    ct.ThrowIfCancellationRequested();
                    return new(
                        new(id, StepStatus.Failed, stepId, Code: "hook_failure:ValidateInput"),
                        null
                    );
                }
                catch (Exception)
                {
                    return new(
                        new(id, StepStatus.Failed, stepId, Code: "pinned_experiment_invalid"),
                        null
                    );
                }
            }
            else if (call.UsesExperiments)
            {
                return new(
                    new(id, StepStatus.Failed, stepId, Code: "experiment_runtime_missing"),
                    null
                );
            }
        }

        var intent = new SlotIntent(reg.Kind, prepared.Call?.Fingerprint, reg.Pinned);

        for (var round = 0; ; round++)
        {
            if (ct.IsCancellationRequested)
            {
                return new(
                    new(id, StepStatus.Cancelled, stepId, Code: "cancelled_before_claim"),
                    null
                );
            }

            var flight = new TaskCompletionSource<SlotResolution>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var key = (scope.DraftId, id);
            if (!slotFlights.TryAdd(key, (intent, flight.Task)))
            {
                if (!slotFlights.TryGetValue(key, out var leader))
                {
                    continue;
                }

                var shared = await leader.Task.ConfigureAwait(false);
                // A leader cancelled by ITS token says nothing about this caller's token, so try again as leader.
                if (
                    shared.Step.Status == StepStatus.Cancelled
                    && !ct.IsCancellationRequested
                    && round < 3
                )
                {
                    continue;
                }
                // Another request for the same slot: do not hand over the leader's output. Ask the authoritative store, which compares
                // the claim fingerprint / pin and answers Attached-with-other-input as a Conflict. Bounded so a busy slot cannot spin.
                if (leader.Intent != intent)
                {
                    if (round < 3)
                    {
                        continue;
                    }

                    return new(
                        new(
                            id,
                            StepStatus.Conflict,
                            stepId,
                            Code: "slot_in_flight_with_different_input"
                        ),
                        null
                    );
                }
                return
                    shared.Step.Status == StepStatus.Attached
                    || shared.Step.Status == StepStatus.Reused
                    ? shared with
                    {
                        Step = shared.Step with { Status = StepStatus.Replayed },
                    }
                    : shared;
            }
            SlotResolution result;
            try
            {
                result = await LeadAsync(scope, reg, prepared, stepId, operation, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                result = new(
                    new(id, StepStatus.Failed, stepId, Code: "engine_failure"),
                    null,
                    Infrastructure: true
                );
            }
            slotFlights.TryRemove(key, out _);
            flight.SetResult(result);
            return result;
        }
    }

    sealed record Prepared(PreparedCall? Call, SlotResolution? Failure);

    // Reads stored dependencies and runs the user's callback. Neither spends money and neither writes the store.
    // WHY before the claim: the fingerprint of the exact call is needed at claim time to tell a true replay from a changed request.
    // A failure here leaves no claim behind and stays on this step's envelope.
    Prepared Prepare(
        RunScope scope,
        SlotRegistration reg,
        IReadOnlyDictionary<string, OutputRef> depRefs,
        ICompositeDescriptor descriptor,
        Guid stepId,
        OperationContext operation
    )
    {
        var id = reg.Slot.SlotId;
        if (reg.Kind != RegistrationKind.Execute)
        {
            return new(null, null);
        }

        var inputs = new Dictionary<string, DependencyInput>(StringComparer.Ordinal);
        foreach (var (dep, output) in depRefs)
        {
            var (artifact, _) = Read(output, scope.Actor);
            if (artifact is null)
            {
                return new(
                    null,
                    new(
                        new(id, StepStatus.Failed, stepId, Code: "dependency_unreadable:" + dep),
                        null
                    )
                );
            }

            inputs[dep] = new(artifact.Reference, artifact.Value.Clone());
        }
        PreparedCall call;
        // A throwing callback is an application bug in a deferred closure. It is recorded on this step, never thrown out of ExecuteAsync.
        // Only the exception TYPE is kept: the message may contain document or parameter text.
        // The callback runs inside the slot's own context scope, so a helper call it makes is correlated to this step. The scope is
        // entered and left on this synchronous path, so it cannot leak into a sibling slot.
        try
        {
            using (AgentCmdContext.BeginScope(operation))
            {
                call = reg.Source!.Build(inputs);
            }
        }
        catch (Exception ex)
        {
            return new(
                null,
                new(
                    new(
                        id,
                        StepStatus.Failed,
                        stepId,
                        Code: "callback_failed:" + ex.GetType().Name
                    ),
                    null
                )
            );
        }
        // The leaf stores into its runtime's ledger and the engine reads through its own. A mismatch would attach refs it can never read.
        if (!ReferenceEquals(call.Ledger, ledger))
        {
            return new(
                null,
                new(new(id, StepStatus.Failed, stepId, Code: "runtime_ledger_mismatch"), null)
            );
        }

        if (ContractProblem(call, reg.Slot, descriptor) is { } contract)
        {
            return new(null, new(new(id, StepStatus.Failed, stepId, Code: contract), null));
        }

        return new(call, null);
    }

    async Task<SlotResolution> LeadAsync(
        RunScope scope,
        SlotRegistration reg,
        Prepared prepared,
        Guid stepId,
        OperationContext operation,
        CancellationToken ct
    )
    {
        var id = reg.Slot.SlotId;
        var call = prepared.Call;
        var fingerprint = call?.Fingerprint;
        // WHY a Reuse claim carries NO execution id: nothing runs for a pin. The producing execution is named by the pinned output itself.
        Guid? executionId = reg.Kind == RegistrationKind.Execute ? Guid.NewGuid() : null;

        ClaimResult claim;
        try
        {
            claim = await store
                .ClaimSlotAsync(
                    new(
                        scope.DraftId,
                        id,
                        stepId,
                        reg.Kind == RegistrationKind.Execute
                            ? MemberProvenance.Executed
                            : MemberProvenance.Reused,
                        executionId,
                        fingerprint
                    ),
                    ct
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new(new(id, StepStatus.Cancelled, stepId, Code: "cancelled_before_claim"), null);
        }
        catch (Exception)
        {
            return new(
                new(id, StepStatus.Failed, stepId, Code: "store_unavailable"),
                null,
                Infrastructure: true
            );
        }

        switch (claim)
        {
            case ClaimResult.AlreadyAttached a:
                // Replay the known receipt. No command runs. WHY compare kind and provenance as well as fingerprint/pin: a slot attached by a
                // pin has no call fingerprint, so a real call against it must not pass a "fingerprint is null" shortcut and silently replay the pin.
                // A pin names an output, so the same output is the same request even when a command produced it; the member keeps its true provenance.
                var same =
                    reg.Kind == RegistrationKind.Reuse
                        ? a.Slot.Output == reg.Pinned
                        : a.Slot.Provenance == MemberProvenance.Executed
                            && a.Slot.Fingerprint is not null
                            && a.Slot.Fingerprint == fingerprint;
                if (!same)
                {
                    return new(
                        new(
                            id,
                            StepStatus.Conflict,
                            a.Slot.StepId,
                            a.Slot.ExecutionId,
                            a.Slot.Output,
                            reg.Kind == RegistrationKind.Reuse
                                ? "slot_attached_to_different_output"
                                : "slot_attached_with_different_input"
                        ),
                        null
                    );
                }

                return Known(a.Slot);
            case ClaimResult.InFlight f:
                // Unknown spend: another attempt claimed and never resolved. Do NOT run again.
                return new(
                    new(
                        id,
                        StepStatus.Pending,
                        f.Slot.StepId,
                        f.Slot.ExecutionId,
                        null,
                        "claimed_unresolved"
                    ),
                    null
                );
            case ClaimResult.DraftSealed:
                return new(new(id, StepStatus.Conflict, stepId, Code: "draft_sealed"), null);
            case ClaimResult.Rejected r:
                return new(
                    new(id, StepStatus.Conflict, stepId, Code: "claim_rejected:" + r.Code),
                    null
                );
        }
        return reg.Kind == RegistrationKind.Reuse
            ? await ReuseAsync(scope, reg, stepId).ConfigureAwait(false)
            : await RunCallAsync(scope, reg, call!, stepId, executionId!.Value, operation, ct)
                .ConfigureAwait(false);
    }

    async Task<SlotResolution> ReuseAsync(RunScope scope, SlotRegistration reg, Guid stepId)
    {
        var id = reg.Slot.SlotId;
        var pinned = reg.Pinned!;
        // Re-read through the ledger as the actor: authorization and existence are the ledger's decision, not the caller's Value.
        if (pinned.RunId != scope.RunId)
        {
            return await FailAsync(scope, id, stepId, StepStatus.Conflict, "pinned_other_run", null)
                .ConfigureAwait(false);
        }

        var (artifact, code) = Read(pinned, scope.Actor);
        if (artifact is null)
        {
            return await FailAsync(
                    scope,
                    id,
                    stepId,
                    StepStatus.Conflict,
                    "pinned_unreadable:" + code,
                    null
                )
                .ConfigureAwait(false);
        }

        if (reg.Shape?.Invoke(artifact.Value) is { } shapeError)
        {
            return await FailAsync(scope, id, stepId, StepStatus.Conflict, shapeError, null)
                .ConfigureAwait(false);
        }

        return await AttachAsync(scope, id, stepId, pinned, null, StepStatus.Reused, null)
            .ConfigureAwait(false);
    }

    async Task<SlotResolution> RunCallAsync(
        RunScope scope,
        SlotRegistration reg,
        PreparedCall call,
        Guid stepId,
        Guid executionId,
        OperationContext context,
        CancellationToken ct
    )
    {
        var id = reg.Slot.SlotId;
        // The context is captured by value when the leaf starts and is never read from ambient state again, so every leaf event of this
        // attempt carries DraftId + SlotId + StepId (WorkId = StepId) even after the caller's own scope has been disposed.
        var journal = new SlotJournal(store, scope.DraftId, id, stepId);
        ComponentRun run;
        try
        {
            run = await call.ExecuteAsync(context, executionId, id, stepId, reg.Site, journal, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            run = new(ComponentKind.Cancelled, null, executionId, "cancelled", null);
        }
        catch (Exception)
        {
            run = new(ComponentKind.Failed, null, executionId, "bridge_exception", null);
        }

        // The leaf reports the execution id it actually used. It is authoritative; the reserved one is only a fallback for logs.
        var observed = run.ExecutionId ?? executionId;
        var settled = await SettleAsync().ConfigureAwait(false);
        return settled with
        {
            Step = settled.Step with
            {
                Failure = run.Failure,
                Snapshot = run.Snapshot,
                Delivery = run.Delivery,
                Context = run.Context,
            },
        };

        async Task<SlotResolution> SettleAsync()
        {
            if (journal.CompletionLost)
            {
                // The attempt's authoritative terminal record is absent even when
                // the provider failed or cancelled. Do not make that spend eligible
                // for automatic retry merely because no output was returned.
                return new(
                    new(
                        id,
                        StepStatus.Pending,
                        stepId,
                        observed,
                        run.Output,
                        "journal_completion_unavailable"
                    ),
                    null,
                    Infrastructure: true
                );
            }
            if (scope.ExperimentPin is not null && run.Kind == ComponentKind.Stored)
            {
                var evidence = Read(run.Output!, scope.Actor).Artifact?.Evidence;
                if (evidence is null)
                {
                    // The model may have spent; keep the claim pending rather than making this send retryable.
                    return new(
                        new(
                            id,
                            StepStatus.Pending,
                            stepId,
                            observed,
                            run.Output,
                            "output_evidence_unavailable"
                        ),
                        null,
                        Infrastructure: true
                    );
                }
            }
            switch (run.Kind)
            {
                case ComponentKind.Stored when run.Output!.RunId == scope.RunId:
                    return await AttachAsync(
                            scope,
                            id,
                            stepId,
                            run.Output,
                            observed,
                            StepStatus.Attached,
                            run.Caller ?? reg.Site
                        )
                        .ConfigureAwait(false);
                case ComponentKind.Stored:
                    return await FailAsync(
                            scope,
                            id,
                            stepId,
                            StepStatus.Failed,
                            "output_run_mismatch",
                            observed
                        )
                        .ConfigureAwait(false);
                case ComponentKind.Cancelled when journal.Started:
                    // WHY not released: attempt.started is stored, so a provider request may already have been sent and billed. A released
                    // (Cancelled) slot would be claimed and paid for again by the next Execute. Keeping it Claimed makes the unknown spend visible
                    // and blocks an automatic repeat; an operator resolves it with ICompositionStore.ReleaseSlotAsync using this StepId.
                    return new(
                        new(id, StepStatus.Pending, stepId, observed, null, "cancelled_after_send"),
                        null
                    );
                case ComponentKind.Cancelled:
                    return await FailAsync(
                            scope,
                            id,
                            stepId,
                            StepStatus.Cancelled,
                            "cancelled",
                            observed
                        )
                        .ConfigureAwait(false);
                default:
                    // NotStored: the model answered but the ledger did not store it. The value is NOT stored evidence, so the slot stays unfilled.
                    return await FailAsync(
                            scope,
                            id,
                            stepId,
                            StepStatus.Failed,
                            run.Code ?? "failed",
                            observed
                        )
                        .ConfigureAwait(false);
            }
        }
    }

    async Task<SlotResolution> AttachAsync(
        RunScope scope,
        string slotId,
        Guid stepId,
        OutputRef output,
        Guid? executionId,
        StepStatus status,
        CallSite? site
    )
    {
        try
        {
            var applied = await store
                .AttachSlotAsync(
                    new(scope.DraftId, slotId, stepId, output, executionId),
                    CancellationToken.None
                )
                .ConfigureAwait(false);
            if (applied is TransitionResult.Applied)
            {
                return new(new(slotId, status, stepId, executionId, output, Caller: site), output);
            }
        }
        catch (Exception) { }
        // Output exists but the store did not record the attach: leave the slot Claimed (Pending) with the output visible to an operator.
        return new(
            new(slotId, StepStatus.Pending, stepId, executionId, output, "attach_failed"),
            null,
            Infrastructure: true
        );
    }

    async Task<SlotResolution> FailAsync(
        RunScope scope,
        string slotId,
        Guid stepId,
        StepStatus status,
        string code,
        Guid? executionId
    )
    {
        try
        {
            var applied = await store
                .ReleaseSlotAsync(
                    new(
                        scope.DraftId,
                        slotId,
                        stepId,
                        status == StepStatus.Cancelled ? SlotStatus.Cancelled : SlotStatus.Failed,
                        code,
                        executionId
                    ),
                    CancellationToken.None
                )
                .ConfigureAwait(false);
            if (applied is TransitionResult.Applied)
            {
                return new(new(slotId, status, stepId, executionId, null, code), null);
            }
        }
        catch (Exception) { }
        return new(
            new(slotId, StepStatus.Pending, stepId, executionId, null, code + "|release_failed"),
            null,
            Infrastructure: true
        );
    }

    (OutputArtifact? Artifact, string? Code) Read(OutputRef reference, Actor actor)
    {
        try
        {
            return ledger.ReadOutput(reference, actor) switch
            {
                LedgerResult<OutputArtifact>.Accepted ok when ok.Value.Reference == reference => (
                    ok.Value,
                    null
                ),
                LedgerResult<OutputArtifact>.Accepted => (null, "reference_mismatch"),
                LedgerResult<OutputArtifact>.Rejected rejected => (null, rejected.Code),
            };
        }
        catch (Exception)
        {
            return (null, "ledger_unavailable");
        }
    }

    // Step 3. One finisher per draft per process; other callers wait for it. Cross-process races are decided by the store's seal CAS.
    async Task<FinishResult> FinishAsync(
        RunScope scope,
        ICompositeDescriptor descriptor,
        DraftRecord latest,
        Actor actor
    )
    {
        var flight = new TaskCompletionSource<FinishResult>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var flightKey = (scope.DraftId, actor.Tenant, actor.Subject);
        if (!finishFlights.TryAdd(flightKey, flight.Task))
        {
            return finishFlights.TryGetValue(flightKey, out var other)
                ? await other.ConfigureAwait(false)
                : await FinishAsync(scope, descriptor, latest, actor).ConfigureAwait(false);
        }

        FinishResult result;
        try
        {
            result = await FinishCoreAsync(scope, descriptor, latest, actor).ConfigureAwait(false);
        }
        catch (Exception)
        {
            result = new(null, null, "unavailable", "finish_failed", null, false);
        }
        flight.SetResult(result);
        finishFlights.TryRemove(flightKey, out _);
        return result;
    }

    async Task<FinishResult> FinishCoreAsync(
        RunScope scope,
        ICompositeDescriptor descriptor,
        DraftRecord latest,
        Actor actor
    )
    {
        // A sealed draft is the authority: return it. WHY: sealing is idempotent, so a second handle must replay, not rebuild.
        // WHY read the aggregate through the ledger first: the sealed JSON lives in the composition store, which knows nothing about this actor.
        // The ledger read is the authorization for returning the whole.
        if (latest.Status == DraftStatus.Sealed && latest.Aggregate is { } sealedAggregate)
        {
            var authoritative = Read(sealedAggregate.Reference.Output, actor).Artifact;
            if (authoritative is null)
            {
                return new(null, null, "unavailable", "aggregate_unreadable", null, false);
            }
            if (
                latest.ExperimentPin is not null
                && (
                    authoritative.Evidence?.CompositionMembers is not { } manifest
                    || !manifest.SequenceEqual(sealedAggregate.Members)
                )
            )
            {
                return new(
                    null,
                    null,
                    "unavailable",
                    "aggregate_evidence_unavailable",
                    null,
                    false
                );
            }
            // The ledger's exact authorized output is what later grading reads. A separate store's
            // stale or corrupt JSON must never substitute another value during replay.
            return Parse(
                descriptor,
                sealedAggregate with
                {
                    Json = authoritative.Value.GetRawText(),
                },
                replayed: true
            );
        }

        var members = new List<CompositeMember>();
        var obj = new JsonObject();
        foreach (var d in descriptor.Slots)
        {
            var slot = latest.Slots[d.SlotId];
            if (slot.Status != SlotStatus.Attached)
            {
                continue; // an optional slot nobody filled
            }

            var (artifact, code) = Read(slot.Output!, actor);
            if (artifact is null)
            {
                return new(null, null, "unavailable", "member_unreadable:" + d.SlotId, null, false);
            }
            // Exact wire JSON of the stored version, explicit nulls included. See AggregateProjection.ExactComponentWire.
            obj[d.JsonName] = JsonNode.Parse(artifact.Value.GetRawText());
            members.Add(
                new(
                    d.SlotId,
                    d.JsonName,
                    slot.Output!,
                    slot.Provenance ?? MemberProvenance.Executed,
                    slot.ExecutionId,
                    slot.StepId
                )
            );
        }
        var json = obj.ToJsonString();
        var candidate = new AggregateArtifact(
            new(
                scope.DraftId,
                Guid.Empty,
                null!,
                descriptor.Id,
                descriptor.Revision,
                descriptor.DefinitionHash
            ),
            json,
            members,
            AggregateProjection.ExactComponentWire,
            clock.GetUtcNow()
        );
        var checkedResult = Parse(descriptor, candidate, replayed: false);
        if (checkedResult.Artifact is null)
        {
            return checkedResult;
        }

        // Record the aggregate as its own artifact with a composition producer, so graders can target it without it posing as a model call.
        // WHY ProducerRef.Composition(DraftId): the draft is sealed once, so its producer identity is stable. A retry after a failed seal
        // records the same JSON under the same producer and the ledger replays the existing artifact instead of minting an orphan.
        OutputArtifact stored;
        try
        {
            using var document = JsonDocument.Parse(json);
            var producer = new ProducerRef.Composition(scope.DraftId);
            var recorded = scope.ExperimentPin is { } pin
                ? ledger.RecordProducedOutputWithEvidence(
                    scope.RunId,
                    producer,
                    document.RootElement.Clone(),
                    scope.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    default(ActivityContext),
                    actor,
                    new ExperimentEvidence(
                        "composition",
                        Revision: pin.Snapshot?.Revision.Value,
                        Reason: pin.DefaultReason?.ToString(),
                        Role: scope.ShadowGroup is null
                            ? ExperimentRole.Primary
                            : ExperimentRole.Shadow,
                        ComparisonGroupId: scope.ShadowGroup
                            ?? CompositionExperimentChoices.PrimaryGroup(pin),
                        Members: members.Select(member => member.Output).ToArray(),
                        CompositionMembers: members.ToArray(),
                        CommandId: descriptor.Id,
                        CommandRevision: descriptor.Revision,
                        DefinitionHash: descriptor.DefinitionHash
                    )
                )
                : ledger.RecordProducedOutput(
                    scope.RunId,
                    producer,
                    document.RootElement.Clone(),
                    scope.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    default(ActivityContext),
                    actor
                );
            if (recorded is not LedgerResult<OutputArtifact>.Accepted ok)
            {
                return new(null, null, "unavailable", "aggregate_not_stored", null, false);
            }

            stored = ok.Value;
            if (
                stored.Reference.RunId != scope.RunId
                || stored.Reference.Producer is not ProducerRef.Composition composition
                || composition.Id != scope.DraftId
                || stored.Value.GetRawText() != json
            )
            {
                return new(null, null, "unavailable", "aggregate_value_mismatch", null, false);
            }
            if (
                scope.ExperimentPin is not null
                && (
                    stored.Evidence?.CompositionMembers is not { } recordedMembers
                    || !recordedMembers.SequenceEqual(members)
                )
            )
            {
                return new(
                    null,
                    null,
                    "unavailable",
                    "aggregate_evidence_unavailable",
                    null,
                    false
                );
            }
        }
        catch (Exception)
        {
            return new(null, null, "unavailable", "aggregate_not_stored", null, false);
        }

        var aggregate = candidate with
        {
            Reference = new(
                scope.DraftId,
                stored.Reference.ArtifactId,
                stored.Reference,
                descriptor.Id,
                descriptor.Revision,
                descriptor.DefinitionHash
            ),
        };
        SealResult sealedResult;
        try
        {
            sealedResult = await store
                .SealAsync(new(scope.DraftId, aggregate), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return new(null, null, "unavailable", "seal_failed", null, false);
        }
        // Known limit: if another process wins the seal, the artifact recorded above is the same replayed artifact (same producer, same JSON)
        // when the membership is equal. With different membership the ledger refuses the second record as a conflict.
        return sealedResult switch
        {
            SealResult.Sealed => Parse(descriptor, aggregate, replayed: false),
            SealResult.AlreadySealed s when s.Draft.Aggregate is { } winner =>
                winner.Members.SequenceEqual(members)
                    ? await FinishCoreAsync(scope, descriptor, s.Draft, actor).ConfigureAwait(false)
                    : new(
                        null,
                        null,
                        "conflict",
                        "sealed_with_different_membership",
                        "The draft was sealed with other members",
                        false
                    ),
            SealResult.Rejected r => new(
                null,
                null,
                "conflict",
                r.Code,
                "Seal refused: " + r.Code,
                false
            ),
            _ => new(null, null, "unavailable", "seal_failed", null, false),
        };
    }

    // The typed whole is only used to VALIDATE and to hand back to the caller; the stored JSON is never regenerated from it.
    static FinishResult Parse(
        ICompositeDescriptor descriptor,
        AggregateArtifact artifact,
        bool replayed
    )
    {
        object? whole;
        try
        {
            whole = JsonSerializer.Deserialize(artifact.Json, descriptor.WholeInfo);
        }
        catch (JsonException)
        {
            return new(
                null,
                null,
                "conflict",
                "aggregate_invalid",
                "Stored parts do not deserialize into the whole",
                false
            );
        }
        if (whole is null)
        {
            return new(null, null, "conflict", "aggregate_invalid", "Aggregate is null", false);
        }

        try
        {
            if (descriptor.ValidateObject(whole) is { } reason)
            {
                return new(null, null, "conflict", "aggregate_invalid", reason, false);
            }
        }
        catch (Exception)
        {
            return new(
                null,
                null,
                "conflict",
                "aggregate_validator_failed",
                "The aggregate validator threw",
                false
            );
        }
        return new(artifact, whole, null, null, null, replayed);
    }
}
