using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Zeeq.LlmCmd;

/// <summary>Reconstructs one independent shadow composition from a host-frozen JSON payload.</summary>
/// <typeparam name="TWhole">The aggregate type to produce.</typeparam>
/// <remarks>The factory is resolved in the worker's scope. It must use only host-approved, side-effect-free pipelines.</remarks>
public interface ICompositionShadowFactory<TWhole>
{
    /// <summary>Registers parts on the new shadow root without executing it.</summary>
    /// <param name="root">New, independently pinned shadow root.</param>
    /// <param name="payload">Detached host-supplied input.</param>
    /// <param name="actor">Reauthorized actor.</param>
    /// <param name="cancellationToken">Shadow timeout and shutdown token.</param>
    ValueTask ConfigureAsync(
        CompositionRoot<TWhole> root,
        JsonElement payload,
        Actor actor,
        CancellationToken cancellationToken
    );
}

/// <summary>Host authorization for shadowing a composition using side-effect-free pipelines.</summary>
/// <remarks>Implementations are singleton-safe and recheck permissions each time; correlation is not authority.</remarks>
public interface ICompositionShadowPolicy
{
    /// <summary>Checks authorization and approves the selected side-effect-free shadow variants.</summary>
    /// <param name="actor">Authenticated actor captured for the primary.</param>
    /// <param name="snapshot">Retained publication shared by the primary and shadow.</param>
    /// <param name="variants">Selected shadow variant per command.</param>
    /// <returns>True only when the host permits the shadow.</returns>
    bool IsAuthorized(
        Actor actor,
        ExperimentSnapshot snapshot,
        IReadOnlyDictionary<string, string> variants
    );
}

/// <summary>Exact stored aggregate produced by a completed shadow composition.</summary>
/// <param name="ComparisonGroupId">Host-supplied group shared with the primary.</param>
/// <param name="PrimaryDraftId">Primary draft that established the source pin.</param>
/// <param name="ShadowDraftId">Independent shadow draft.</param>
/// <param name="Output">Exact stored aggregate output for grading.</param>
public sealed record CompositionShadowResult(
    string ComparisonGroupId,
    Guid PrimaryDraftId,
    Guid ShadowDraftId,
    OutputRef Output
);

/// <summary>Offers independently scoped, host-reconstructed composition shadows to a bounded scheduler.</summary>
/// <remarks>
/// Hosts explicitly offer work after the primary pin is stored. Accepted work is process-local unless the
/// scheduler is replaced; it is not automatically retried. A factory cannot capture the primary root.
/// </remarks>
public sealed class CompositionShadowCoordinator(
    ICompositionStore store,
    IHumanFeedbackLedger ledger,
    IServiceScopeFactory scopes,
    IShadowScheduler scheduler,
    ICompositionShadowPolicy policy,
    TimeSpan timeout,
    Action<CompositionShadowResult>? observe = null,
    Action<ShadowDisposition>? observeDisposition = null
)
{
    /// <summary>Offers a single shadow for a primary's already pinned experiment publication.</summary>
    /// <typeparam name="TWhole">The aggregate type to reconstruct.</typeparam>
    /// <param name="primaryDraftId">Authorized primary draft with an experiment pin.</param>
    /// <param name="actor">Authenticated actor to reauthorize in the worker.</param>
    /// <param name="payload">Host-frozen JSON input; cloned before scheduling.</param>
    /// <param name="variants">A selected shadow variant for every command in the pinned snapshot.</param>
    /// <param name="comparisonGroupId">Group selected for the primary by the host selector.</param>
    /// <param name="cancellationToken">Cancellation of admission, not accepted work.</param>
    /// <returns>True if the scheduler accepted the shadow.</returns>
    public async ValueTask<bool> TryScheduleAsync<TWhole>(
        Guid primaryDraftId,
        Actor actor,
        JsonElement payload,
        IReadOnlyDictionary<string, string> variants,
        string comparisonGroupId,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(variants);
        ArgumentException.ThrowIfNullOrWhiteSpace(comparisonGroupId);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
            {
                return false;
            }
            var primary = await store
                .GetDraftAsync(primaryDraftId, cancellationToken)
                .ConfigureAwait(false);
            if (
                primary?.ExperimentPin is not { Snapshot: { } snapshot } pin
                || primary.Key.Tenant != actor.Tenant
                || !ledger.CanAccessRun(primary.Key.RunId, actor)
                || !CompositionExperimentChoices.IsPrimary(pin)
                || CompositionExperimentChoices.PrimaryGroup(pin) != comparisonGroupId
            )
            {
                return false;
            }
            var frozenVariants = variants.ToImmutableDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal
            );
            if (
                frozenVariants.Count != snapshot.Commands.Count
                || !policy.IsAuthorized(actor, snapshot, frozenVariants)
            )
            {
                return false;
            }
            var assignments = new Dictionary<string, string>(StringComparer.Ordinal);
            var arms = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (id, view) in snapshot.Commands)
            {
                if (
                    !frozenVariants.TryGetValue(id, out var variant)
                    || !view.Variants.ContainsKey(variant)
                    || (
                        arms.TryGetValue(view.ExperimentId, out var existing) && existing != variant
                    )
                )
                {
                    return false;
                }
                arms[view.ExperimentId] = variant;
                assignments[id] = CompositionExperimentChoices.Encode(
                    new ExperimentChoice.Chosen(
                        new(view, variant, ExperimentRole.Shadow, comparisonGroupId)
                    )
                );
            }
            var shadowPin = ExperimentDraftPin.ForSnapshot(
                snapshot,
                actor.Tenant,
                pin.UnitId,
                pin.AssignmentId,
                assignments
            );
            var frozenPayload = payload.Clone();
            // The queued closure must never retain request-scoped admission adapters.
            var scopeFactory = scopes;
            var authorizedPolicy = policy;
            var resultObserver = observe;
            var primaryKey = primary.Key;
            var primaryFingerprint = pin.Fingerprint;
            var runId = primaryKey.RunId;
            var inputRevision = primaryKey.InputRevision;
            cancellationToken.ThrowIfCancellationRequested();
            return scheduler.TrySchedule(
                new ShadowWork(
                    async ct =>
                    {
                        await using var scope = scopeFactory.CreateAsyncScope();
                        var services = scope.ServiceProvider;
                        var scopedStore = services.GetRequiredService<ICompositionStore>();
                        var scopedLedger = services.GetRequiredService<IHumanFeedbackLedger>();
                        var scopedPrimary = await scopedStore
                            .GetDraftAsync(primaryDraftId, ct)
                            .ConfigureAwait(false);
                        if (
                            scopedPrimary is null
                            || scopedPrimary.Key != primaryKey
                            || scopedPrimary.ExperimentPin?.Fingerprint != primaryFingerprint
                            || !scopedLedger.CanAccessRun(runId, actor)
                            || !authorizedPolicy.IsAuthorized(actor, snapshot, frozenVariants)
                        )
                        {
                            throw new InvalidOperationException(
                                "Shadow authorization or storage changed"
                            );
                        }
                        var runtime = services.GetRequiredService<CommandRuntime>();
                        var registry = services.GetRequiredService<CompositionRegistry>();
                        var factory = services.GetRequiredService<
                            ICompositionShadowFactory<TWhole>
                        >();
                        var roots = new RootCommands(
                            scopedLedger,
                            () => actor,
                            scopedStore,
                            registry,
                            services.GetService<TimeProvider>(),
                            services.GetService<IExecutionEventSink>(),
                            runtime
                        );
                        var suffix = Guid.NewGuid().ToString("N");
                        var root = await roots
                            .BeginShadowAsync<TWhole>(
                                new(runId, inputRevision, "shadow-" + suffix, "shadow-" + suffix),
                                shadowPin,
                                comparisonGroupId,
                                ct
                            )
                            .ConfigureAwait(false);
                        await factory
                            .ConfigureAsync(root, frozenPayload, actor, ct)
                            .ConfigureAwait(false);
                        var outcome = await root.ExecuteAsync(ct).ConfigureAwait(false);
                        if (
                            outcome is not CompositeOutcome<TWhole>.Published published
                            || published.DraftId == primaryDraftId
                        )
                        {
                            throw new InvalidOperationException(
                                "Shadow composition was not published"
                            );
                        }
                        var saved = await scopedStore
                            .GetDraftAsync(published.DraftId, ct)
                            .ConfigureAwait(false);
                        var artifact = scopedLedger.ReadOutput(published.Aggregate.Output, actor);
                        // A second independent scope verifies readback after the admission scope may
                        // have ended. Distinct adapters are valid when they share durable facts.
                        await using var verificationScope = scopeFactory.CreateAsyncScope();
                        var verificationStore =
                            verificationScope.ServiceProvider.GetRequiredService<ICompositionStore>();
                        var verificationLedger =
                            verificationScope.ServiceProvider.GetRequiredService<IHumanFeedbackLedger>();
                        var authoritativeDraft = await verificationStore
                            .GetDraftAsync(published.DraftId, ct)
                            .ConfigureAwait(false);
                        var authoritativeOutput = verificationLedger.CanAccessRun(runId, actor)
                            ? verificationLedger.ReadOutput(published.Aggregate.Output, actor)
                            : new LedgerResult<OutputArtifact>.Rejected(
                                "run_not_accessible",
                                "Run unavailable"
                            );
                        if (
                            saved?.ExperimentPin?.Fingerprint != shadowPin.Fingerprint
                            || authoritativeDraft?.ExperimentPin?.Fingerprint
                                != shadowPin.Fingerprint
                            || authoritativeDraft.Status != DraftStatus.Sealed
                            || authoritativeDraft.Aggregate?.Reference.Output
                                != published.Aggregate.Output
                            || authoritativeOutput
                                is not LedgerResult<OutputArtifact>.Accepted authoritative
                            || authoritative.Value.Reference != published.Aggregate.Output
                            || authoritative.Value.Evidence
                                is not { Role: ExperimentRole.Shadow } authoritativeEvidence
                            || authoritativeEvidence.ComparisonGroupId != comparisonGroupId
                            || authoritativeEvidence.CompositionMembers is null
                            || !authoritativeEvidence.CompositionMembers.SequenceEqual(
                                authoritativeDraft.Aggregate.Members
                            )
                            || saved.Status != DraftStatus.Sealed
                            || saved.Aggregate?.Reference.Output != published.Aggregate.Output
                            || artifact is not LedgerResult<OutputArtifact>.Accepted accepted
                            || accepted.Value.Reference != published.Aggregate.Output
                            || accepted.Value.Value.GetRawText()
                                != authoritative.Value.Value.GetRawText()
                            || accepted.Value.Evidence
                                is not { Role: ExperimentRole.Shadow } evidence
                            || evidence.ComparisonGroupId != comparisonGroupId
                            || evidence.CompositionMembers is null
                            || !evidence.CompositionMembers.SequenceEqual(saved.Aggregate.Members)
                        )
                        {
                            throw new InvalidOperationException("Shadow evidence unavailable");
                        }
                        try
                        {
                            resultObserver?.Invoke(
                                new(
                                    comparisonGroupId,
                                    primaryDraftId,
                                    published.DraftId,
                                    published.Aggregate.Output
                                )
                            );
                        }
                        catch
                        { /* Observation cannot alter the primary or the stored shadow. */
                        }
                    },
                    timeout,
                    observeDisposition
                )
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // An optional comparison cannot change the primary result.
            return false;
        }
    }
}
