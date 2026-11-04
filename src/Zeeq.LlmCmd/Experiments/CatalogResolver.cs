using System.Collections.Frozen;
using System.Text.Json;

namespace Zeeq.LlmCmd;

/// <summary>Bounds for source reads and snapshot contents.</summary>
/// <remarks>
/// The timeout keeps a slow experiment source from delaying the configured default indefinitely. Template
/// size is checked before a model send, so an oversized external template cannot reach the provider.
/// </remarks>
public sealed record ExperimentCatalogOptions
{
    /// <summary>Upper bound for one source read. Exceeding it selects the configured default.</summary>
    public TimeSpan SourceTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Maximum characters accepted in one replacement template.</summary>
    public int MaxTemplateCharacters { get; init; } = 65_536;

    internal void Validate()
    {
        if (SourceTimeout <= TimeSpan.Zero || SourceTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(SourceTimeout),
                "Source timeout must be positive and at most one minute."
            );
        }

        if (MaxTemplateCharacters is < 1 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxTemplateCharacters),
                "Template limit must be between 1 and 1,048,576 characters."
            );
        }
    }
}

/// <summary>
/// Checks and freezes an externally supplied snapshot before any caller can use it.
/// </summary>
/// <remarks>
/// Validation runs before caching and before selection. A source value is never trusted just because it
/// came from a cache or a host store. A snapshot that fails anywhere is rejected whole, so no caller can
/// receive part of a publication.
/// </remarks>
internal static class ExperimentSnapshotValidator
{
    /// <summary>Validates and freezes a source result.</summary>
    /// <param name="lookup">The lookup that produced the result.</param>
    /// <param name="result">Source result to check. Null is treated as an invalid answer.</param>
    /// <param name="maxTemplateCharacters">Replacement template limit.</param>
    /// <returns>A frozen <see cref="SourceResult{T}.Found"/> when valid; <see cref="SourceResult{T}.Invalid"/> when not; other results unchanged.</returns>
    /// <remarks>
    /// Source payloads are untrusted. A null release, a null entry, or a collection that throws while enumerated
    /// all become an invalid answer. They never escape as an exception that could bypass the configured default.
    /// </remarks>
    public static SourceResult<ExperimentSnapshot> Normalize(
        ExperimentLookup lookup,
        SourceResult<ExperimentSnapshot>? result,
        int maxTemplateCharacters
    )
    {
        if (result is null)
        {
            return new SourceResult<ExperimentSnapshot>.Invalid(SourceFailure.InvalidRelease);
        }

        if (result is not SourceResult<ExperimentSnapshot>.Found found)
        {
            return result;
        }

        try
        {
            return TryFreeze(lookup, found.Release, maxTemplateCharacters, out var frozen)
                ? new SourceResult<ExperimentSnapshot>.Found(frozen!)
                : new SourceResult<ExperimentSnapshot>.Invalid(SourceFailure.InvalidRelease);
        }
        catch (Exception)
        {
            // Hostile or misbehaving collections can throw from any member access. The details are discarded.
            return new SourceResult<ExperimentSnapshot>.Invalid(SourceFailure.InvalidRelease);
        }
    }

    static bool TryFreeze(
        ExperimentLookup lookup,
        ExperimentSnapshot snapshot,
        int maxTemplateCharacters,
        out ExperimentSnapshot? frozen
    )
    {
        frozen = null;
        if (lookup.RequestedRevision is { } requested && requested != snapshot.Revision)
        {
            return false;
        }

        var commands = new Dictionary<string, ExperimentCatalogView>(StringComparer.Ordinal);
        foreach (var (commandId, view) in snapshot.Commands)
        {
            if (
                !ExperimentIdentifier.IsValid(commandId)
                || view.CommandId != commandId
                || view.Revision != snapshot.Revision
            )
            {
                return false;
            }

            if (
                !IsValidSignature(view.ContractSignature)
                || !ExperimentIdentifier.IsValid(view.ExperimentId)
                || view.CommandRevision < 1
            )
            {
                return false;
            }

            if (!TryFreezeVariants(view, maxTemplateCharacters, out var variants))
            {
                return false;
            }

            commands.Add(
                commandId,
                ExperimentCatalogView.Freeze(
                    view.CommandId,
                    view.CommandRevision,
                    view.ContractSignature,
                    view.Revision,
                    view.ExperimentId,
                    view.ControlVariantId,
                    variants!
                )
            );
        }

        frozen = new(snapshot.Revision, commands.ToFrozenDictionary(StringComparer.Ordinal));
        return true;
    }

    static bool TryFreezeVariants(
        ExperimentCatalogView view,
        int maxTemplateCharacters,
        out IEnumerable<ExperimentVariant>? variants
    )
    {
        variants = null;
        if (view.Variants.Count == 0 || !view.Variants.ContainsKey(view.ControlVariantId))
        {
            return false;
        }

        var frozen = new List<ExperimentVariant>(view.Variants.Count);
        foreach (var (variantId, variant) in view.Variants)
        {
            if (
                !ExperimentIdentifier.IsValid(variantId)
                || variant.VariantId != variantId
                || variant.Weight < 0
            )
            {
                return false;
            }

            if (
                !IsValidPrompt(variant.Prompt, maxTemplateCharacters)
                || !TryFreezeParameters(variant.ParameterValues, out var parameters)
            )
            {
                return false;
            }

            frozen.Add(
                new ExperimentVariant(
                    variantId,
                    variant.Weight,
                    variant.Prompt,
                    parameters!,
                    variant.Route
                )
            );
        }

        variants = frozen;
        return true;
    }

    static bool IsValidPrompt(PromptTemplateOverride prompt, int maxTemplateCharacters)
    {
        // A command needs a user message, so an explicit removal of the user template is never valid.
        if (prompt.User is TemplateChange.Removed)
        {
            return false;
        }

        if (prompt.User is TemplateChange.Replaced user && string.IsNullOrWhiteSpace(user.Text))
        {
            return false;
        }

        return IsValidChange(prompt.System, maxTemplateCharacters)
            && IsValidChange(prompt.User, maxTemplateCharacters);
    }

    static bool IsValidChange(TemplateChange change, int maxTemplateCharacters) =>
        change switch
        {
            TemplateChange.Replaced replaced => !string.IsNullOrEmpty(replaced.Text)
                && replaced.Text.Length <= maxTemplateCharacters,
            _ => true,
        };

    static bool TryFreezeParameters(
        IReadOnlyDictionary<string, JsonElement> values,
        out IReadOnlyDictionary<string, JsonElement>? frozen
    )
    {
        frozen = null;
        var copy = new Dictionary<string, JsonElement>(values.Count, StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            // An undefined element means the source failed to provide a value. Explicit JSON null is allowed.
            if (!IsValidClrName(name) || value.ValueKind == JsonValueKind.Undefined)
            {
                return false;
            }

            // A JsonElement reads from its owning JsonDocument. Cloning detaches the value, so disposing the
            // source's document cannot change or invalidate a published snapshot.
            copy.Add(name, value.Clone());
        }

        frozen = copy.ToFrozenDictionary(StringComparer.Ordinal);
        return true;
    }

    static bool IsValidSignature(string signature) =>
        !string.IsNullOrEmpty(signature) && signature.Length <= 256;

    static bool IsValidClrName(string name) =>
        name.Length is > 0 and <= ExperimentIdentifier.MaxLength
        && !char.IsAsciiDigit(name[0])
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}

/// <summary>
/// Loads and assigns experiment snapshots for calls, with a configured default when the source fails.
/// </summary>
/// <remarks>
/// Each call uses one snapshot, so a later source change cannot alter a call already in flight. Loading
/// separates from assignment so composition can pin a snapshot once and resolve every later command from it.
/// Actor and run authorization happen before a lookup. A source's refusal is a source failure and selects the
/// configured default; it never bypasses authorization. Caller cancellation is never turned into a default.
/// </remarks>
public sealed class ExperimentResolver
{
    readonly IExperimentSnapshotSource _source;
    readonly ExperimentCatalogOptions _options;
    readonly IExperimentSelector? _selector;

    /// <summary>Creates a resolver over one host-supplied snapshot source.</summary>
    /// <param name="source">Snapshot source. Pass a caching decorator when one is needed.</param>
    /// <param name="options">Timeouts and size limits; null uses defaults.</param>
    /// <param name="selector">Host assignment policy; null always selects the control variant.</param>
    public ExperimentResolver(
        IExperimentSnapshotSource source,
        ExperimentCatalogOptions? options = null,
        IExperimentSelector? selector = null
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        _options = options ?? new();
        _options.Validate();
        _source = source;
        _selector = selector;
    }

    /// <summary>Reads one atomic, validated snapshot for an authorized lookup.</summary>
    /// <param name="lookup">Authorized lookup.</param>
    /// <param name="cancellationToken">Caller cancellation. Cancellation propagates instead of selecting a default.</param>
    /// <returns>A loaded snapshot, or a sanitized reason to use the configured default.</returns>
    public async ValueTask<ExperimentSnapshotResult> LoadAsync(
        ExperimentLookup lookup,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(lookup);
        var fetched = await GuardAsync(
                token => _source.GetAsync(lookup, token),
                _options.SourceTimeout,
                cancellationToken
            )
            .ConfigureAwait(false);
        var normalized = ExperimentSnapshotValidator.Normalize(
            lookup,
            fetched,
            _options.MaxTemplateCharacters
        );

        if (normalized is SourceResult<ExperimentSnapshot>.Found found)
        {
            return new ExperimentSnapshotResult.Loaded(found.Release);
        }

        if (normalized is SourceResult<ExperimentSnapshot>.NoOverride)
        {
            return new ExperimentSnapshotResult.Unavailable(ExperimentFallbackReason.NoOverride);
        }

        return normalized switch
        {
            SourceResult<ExperimentSnapshot>.Unavailable unavailable =>
                new ExperimentSnapshotResult.Unavailable(MapFailure(unavailable.Failure)),
            SourceResult<ExperimentSnapshot>.Invalid invalid =>
                new ExperimentSnapshotResult.Unavailable(MapFailure(invalid.Failure)),
            _ => new ExperimentSnapshotResult.Unavailable(ExperimentFallbackReason.InvalidRelease),
        };
    }

    /// <summary>Chooses a variant of a loaded snapshot for one call, or the configured default.</summary>
    /// <param name="snapshot">Snapshot from <see cref="LoadAsync"/>. Composition passes the value it pinned.</param>
    /// <param name="lookup">Lookup that produced the snapshot.</param>
    /// <param name="configuredDefault">Complete default for the command, used when no treatment applies.</param>
    /// <param name="operation">Captured operation context for the selector.</param>
    /// <param name="actorId">Authenticated actor identifier, when the host has one.</param>
    /// <param name="unitId">Stable host-supplied unit key for deterministic assignment, or null.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <returns>A treatment assignment or the complete configured default.</returns>
    public async ValueTask<ExperimentResolution> AssignAsync(
        ExperimentSnapshot snapshot,
        ExperimentLookup lookup,
        ConfiguredDefaultView configuredDefault,
        CapturedContext operation,
        string? actorId,
        string? unitId,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(configuredDefault);

        // A missing command is an explicit "no override" for that command only.
        if (!snapshot.TryGetCommand(configuredDefault.CommandId, out var view) || view is null)
        {
            return new ExperimentResolution.ConfiguredDefault(
                configuredDefault,
                ExperimentFallbackReason.NoOverride
            );
        }

        // A contract change means the treatment was authored for a different input or output shape.
        if (view.ContractSignature != configuredDefault.ContractSignature)
        {
            return new ExperimentResolution.ConfiguredDefault(
                configuredDefault,
                ExperimentFallbackReason.InvalidRelease
            );
        }

        // A different compiled revision means the treatment was authored for another version of the command.
        if (view.CommandRevision != configuredDefault.CommandRevision)
        {
            return new ExperimentResolution.ConfiguredDefault(
                configuredDefault,
                ExperimentFallbackReason.StaleCommandRevision
            );
        }

        var choice = await SelectAsync(lookup, view, operation, actorId, unitId, cancellationToken)
            .ConfigureAwait(false);
        return choice switch
        {
            ExperimentChoice.Chosen chosen => new ExperimentResolution.Treatment(chosen.Assignment),
            ExperimentChoice.UseDefault useDefault => new ExperimentResolution.ConfiguredDefault(
                configuredDefault,
                useDefault.Reason
            ),
            _ => new ExperimentResolution.ConfiguredDefault(
                configuredDefault,
                ExperimentFallbackReason.SelectorFailed
            ),
        };
    }

    /// <summary>
    /// Makes every command's choice once for a loaded snapshot, so composition can pin the complete result before any
    /// command is prepared. Commands that share one experiment get one coordinated choice.
    /// </summary>
    /// <param name="lookup">The authorized lookup that produced <paramref name="snapshot"/>.</param>
    /// <param name="snapshot">Snapshot from <see cref="LoadAsync"/>.</param>
    /// <param name="operation">Captured operation context for the selector.</param>
    /// <param name="actorId">Authenticated actor identifier, when the host has one.</param>
    /// <param name="unitId">Stable host-supplied unit key, or null.</param>
    /// <param name="cancellationToken">Caller cancellation. Cancellation propagates and produces no partial pin.</param>
    /// <returns>A frozen pin of the snapshot and every command's choice.</returns>
    /// <remarks>
    /// <para>
    /// Commands are grouped by <see cref="ExperimentCatalogView.ExperimentId"/>. The selector runs once per group,
    /// on the first command in identifier order. Its variant is then applied to every command in the group, so a
    /// stateful selector cannot give two commands of one experiment different arms.
    /// </para>
    /// <para>
    /// Commands in one group must have the same control variant and the same variant identifiers and weights. If
    /// they differ, every command in the group uses its configured default with
    /// <see cref="ExperimentFallbackReason.IncompatibleExperimentGroup"/>, and the selector does not run for that group.
    /// Separate experiments are selected independently.
    /// </para>
    /// </remarks>
    public async ValueTask<ExperimentPin> PreselectAsync(
        ExperimentLookup lookup,
        ExperimentSnapshot snapshot,
        CapturedContext operation,
        string? actorId,
        string? unitId,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var choices = new Dictionary<string, ExperimentChoice>(StringComparer.Ordinal);
        var groups = snapshot
            .Commands.Values.GroupBy(view => view.ExperimentId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var members = group.OrderBy(view => view.CommandId, StringComparer.Ordinal).ToArray();
            var representative = members[0];
            if (!members.All(member => HasSameArms(representative, member)))
            {
                foreach (var member in members)
                {
                    choices[member.CommandId] = new ExperimentChoice.UseDefault(
                        ExperimentFallbackReason.IncompatibleExperimentGroup
                    );
                }

                continue;
            }

            var choice = await SelectAsync(
                    lookup,
                    representative,
                    operation,
                    actorId,
                    unitId,
                    cancellationToken
                )
                .ConfigureAwait(false);
            foreach (var member in members)
            {
                choices[member.CommandId] = choice is ExperimentChoice.Chosen chosen
                    ? new ExperimentChoice.Chosen(chosen.Assignment with { View = member })
                    : choice;
            }
        }

        return new(snapshot, choices.ToFrozenDictionary(StringComparer.Ordinal));
    }

    /// <summary>Whether two commands of one experiment offer the same arms, so one choice applies to both.</summary>
    static bool HasSameArms(ExperimentCatalogView left, ExperimentCatalogView right)
    {
        if (
            left.ControlVariantId != right.ControlVariantId
            || left.Variants.Count != right.Variants.Count
        )
        {
            return false;
        }

        foreach (var (variantId, variant) in left.Variants)
        {
            if (
                !right.Variants.TryGetValue(variantId, out var other)
                || other.Weight != variant.Weight
            )
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Chooses one variant of a validated view without requiring a configured default. Composition uses this to
    /// pin one choice per command view before any command is prepared.
    /// </summary>
    /// <param name="lookup">The authorized lookup that produced <paramref name="view"/>.</param>
    /// <param name="view">Frozen command view from a loaded snapshot.</param>
    /// <param name="operation">Captured operation context for the selector.</param>
    /// <param name="actorId">Authenticated actor identifier, when the host has one.</param>
    /// <param name="unitId">Stable host-supplied unit key for deterministic assignment, or null.</param>
    /// <param name="cancellationToken">Caller cancellation. Cancellation propagates; it never becomes a choice.</param>
    /// <returns>A chosen assignment, or a sanitized reason to use the configured default.</returns>
    /// <remarks>
    /// The selector runs at most once per call. Its exceptions become a sanitized <see cref="ExperimentFallbackReason.SelectorFailed"/>
    /// result, and its unknown variant identifiers never reach a model call.
    /// </remarks>
    public async ValueTask<ExperimentChoice> SelectAsync(
        ExperimentLookup lookup,
        ExperimentCatalogView view,
        CapturedContext operation,
        string? actorId,
        string? unitId,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(operation);

        if (_selector is null)
        {
            return new ExperimentChoice.Chosen(
                new(view, view.ControlVariantId, ExperimentRole.Primary, null)
            );
        }

        ExperimentSelection? selection;
        try
        {
            selection = await _selector
                .SelectAsync(new(lookup, view, operation, actorId, unitId), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new ExperimentChoice.UseDefault(ExperimentFallbackReason.SelectorFailed);
        }

        if (selection is null)
        {
            return new ExperimentChoice.UseDefault(ExperimentFallbackReason.SelectorFailed);
        }

        if (selection is ExperimentSelection.Variant chosen)
        {
            if (!view.Variants.ContainsKey(chosen.VariantId))
            {
                return new ExperimentChoice.UseDefault(ExperimentFallbackReason.UnknownVariant);
            }

            return new ExperimentChoice.Chosen(
                new(view, chosen.VariantId, chosen.Role, chosen.ComparisonGroupId)
            );
        }

        return new ExperimentChoice.UseDefault(ExperimentFallbackReason.SelectorChoseDefault);
    }

    /// <summary>Loads a snapshot and chooses a variant for one standalone call.</summary>
    /// <param name="lookup">Authorized lookup.</param>
    /// <param name="configuredDefault">Complete default for the command.</param>
    /// <param name="operation">Captured operation context.</param>
    /// <param name="actorId">Authenticated actor identifier, when the host has one.</param>
    /// <param name="unitId">Stable host-supplied unit key, or null.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <returns>A treatment assignment or the complete configured default.</returns>
    public async ValueTask<ExperimentResolution> ResolveAsync(
        ExperimentLookup lookup,
        ConfiguredDefaultView configuredDefault,
        CapturedContext operation,
        string? actorId,
        string? unitId,
        CancellationToken cancellationToken
    )
    {
        var loaded = await LoadAsync(lookup, cancellationToken).ConfigureAwait(false);
        if (loaded is ExperimentSnapshotResult.Loaded found)
        {
            return await AssignAsync(
                    found.Snapshot,
                    lookup,
                    configuredDefault,
                    operation,
                    actorId,
                    unitId,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        return new ExperimentResolution.ConfiguredDefault(
            configuredDefault,
            ((ExperimentSnapshotResult.Unavailable)loaded).Reason
        );
    }

    static ExperimentFallbackReason MapFailure(SourceFailure failure) =>
        failure switch
        {
            SourceFailure.Timeout => ExperimentFallbackReason.Timeout,
            SourceFailure.Unavailable => ExperimentFallbackReason.Unavailable,
            SourceFailure.IncompleteView => ExperimentFallbackReason.IncompleteView,
            SourceFailure.AuthorizationDenied => ExperimentFallbackReason.SourceAuthorizationDenied,
            _ => ExperimentFallbackReason.InvalidRelease,
        };

    static async ValueTask<SourceResult<ExperimentSnapshot>> GuardAsync(
        Func<CancellationToken, ValueTask<SourceResult<ExperimentSnapshot>>> fetch,
        TimeSpan timeout,
        CancellationToken callerToken
    )
    {
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            callerToken,
            deadline.Token
        );
        try
        {
            // WaitAsync enforces the deadline even when a source ignores its token.
            return await fetch(linked.Token).AsTask().WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return new SourceResult<ExperimentSnapshot>.Unavailable(SourceFailure.Timeout);
        }
        catch (Exception)
        {
            // The exception text may contain provider or host detail, so it is intentionally discarded.
            return new SourceResult<ExperimentSnapshot>.Unavailable(SourceFailure.Unavailable);
        }
    }
}
