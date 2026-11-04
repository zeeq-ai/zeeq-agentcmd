using System.Collections.Frozen;
using System.Text.Json;

namespace Zeeq.LlmCmd;

/// <summary>
/// One variant of an experiment, frozen after validation. A call holds this value for its lifetime,
/// so a later source update cannot change a request already in flight.
/// </summary>
/// <param name="VariantId">Variant identifier within the experiment.</param>
/// <param name="Weight">Relative share used by <see cref="WeightedExperimentSelector"/>.</param>
/// <param name="Prompt">Prompt-template changes for this variant.</param>
/// <param name="ParameterValues">Typed parameter overrides keyed by case-sensitive CLR property name.</param>
/// <param name="Route">Route for this variant, or null to inherit the configured base route.</param>
public sealed record ExperimentVariant(
    string VariantId,
    int Weight,
    PromptTemplateOverride Prompt,
    IReadOnlyDictionary<string, JsonElement> ParameterValues,
    ExecutionProfile? Route
);

/// <summary>
/// Immutable, joined experiment definition published for one command revision.
/// </summary>
/// <remarks>
/// The view joins prompt and parameter releases only when both name the same revision, so no call can
/// mix templates from one publication with routes from another.
/// </remarks>
/// <param name="CommandId">Command the view targets.</param>
/// <param name="CommandRevision">
/// Compiled command revision the view was authored against; must be at least 1. Runtime treats a mismatch with the
/// compiled command revision as stale and selects the configured default. The signature is a second, independent gate.
/// </param>
/// <param name="ContractSignature">Command contract the view was checked against. Also a stale gate.</param>
/// <param name="Revision">Immutable revision of the view.</param>
/// <param name="ExperimentId">Experiment that owns the variants.</param>
/// <param name="ControlVariantId">Variant used when no selector chooses another.</param>
/// <param name="Variants">Every variant, keyed by identifier.</param>
public sealed record ExperimentCatalogView(
    string CommandId,
    int CommandRevision,
    string ContractSignature,
    ExperimentRevision Revision,
    string ExperimentId,
    string ControlVariantId,
    IReadOnlyDictionary<string, ExperimentVariant> Variants
)
{
    /// <summary>Creates a frozen view whose collections cannot be changed by the source after publication.</summary>
    /// <param name="commandId">Command the view targets.</param>
    /// <param name="commandRevision">Compiled command revision the view was authored against; at least 1.</param>
    /// <param name="contractSignature">Command contract the view was checked against.</param>
    /// <param name="revision">Immutable revision of the view.</param>
    /// <param name="experimentId">Experiment that owns the variants.</param>
    /// <param name="controlVariantId">Variant used when no selector chooses another.</param>
    /// <param name="variants">Variants to copy into frozen storage.</param>
    /// <returns>A frozen view.</returns>
    public static ExperimentCatalogView Freeze(
        string commandId,
        int commandRevision,
        string contractSignature,
        ExperimentRevision revision,
        string experimentId,
        string controlVariantId,
        IEnumerable<ExperimentVariant> variants
    ) =>
        new(
            commandId,
            commandRevision,
            contractSignature,
            revision,
            experimentId,
            controlVariantId,
            variants.ToFrozenDictionary(v => v.VariantId, StringComparer.Ordinal)
        );
}

/// <summary>
/// The complete configured default, used as one unit when the source fails before selection.
/// </summary>
/// <remarks>
/// The default carries its own templates, parameter values, and route. Mixing a treatment template with
/// default parameters or routing would produce a prompt that was never authored or tested as a whole.
/// The generator or host creates this value. It must not call the experiment source.
/// </remarks>
/// <param name="CommandId">Command the default targets.</param>
/// <param name="ContractSignature">Command contract the default was authored against.</param>
/// <param name="SystemTemplate">Compiled or host-configured system template, or null.</param>
/// <param name="UserTemplate">Compiled or host-configured user template, which must contain non-whitespace text.</param>
/// <param name="ParameterValues">Default parameter overrides, usually empty.</param>
/// <param name="Route">Default route, or null to use the command's authored profile.</param>
/// <param name="CommandRevision">
/// Compiled command revision the default was authored for. A treatment whose revision differs is stale and
/// selects this default. Defaults to 1; the runtime should pass the command's descriptor revision.
/// </param>
public sealed record ConfiguredDefaultView(
    string CommandId,
    string ContractSignature,
    string? SystemTemplate,
    string UserTemplate,
    IReadOnlyDictionary<string, JsonElement> ParameterValues,
    ExecutionProfile? Route,
    int CommandRevision = 1
);

/// <summary>
/// One atomic publication covering every command in a partition. Composition pins this value once so a
/// later step resolves its command from the same publication, even if the source is unavailable.
/// </summary>
/// <remarks>
/// Commands are looked up locally by identifier. A missing command means "no override for this command"
/// and selects that command's configured default. It never falls back to another revision.
/// </remarks>
/// <param name="Revision">Immutable revision shared by every command view.</param>
/// <param name="Commands">Frozen command views keyed by command identifier.</param>
public sealed record ExperimentSnapshot(
    ExperimentRevision Revision,
    IReadOnlyDictionary<string, ExperimentCatalogView> Commands
)
{
    /// <summary>Looks up one command view from this publication.</summary>
    /// <param name="commandId">Stable command identifier.</param>
    /// <param name="view">Frozen command view on success; otherwise null.</param>
    /// <returns>True when the publication contains the command.</returns>
    public bool TryGetCommand(string commandId, out ExperimentCatalogView? view) =>
        Commands.TryGetValue(commandId, out view);
}

/// <summary>Role of a selected variant in a comparison.</summary>
public enum ExperimentRole
{
    /// <summary>The variant whose result the caller receives.</summary>
    Primary,

    /// <summary>A comparison variant whose result is evaluated but never returned to the caller.</summary>
    Shadow,
}

/// <summary>A variant chosen from a validated view, with its role.</summary>
/// <param name="View">Immutable view the choice came from.</param>
/// <param name="VariantId">Chosen variant, validated against <paramref name="View"/>.</param>
/// <param name="Role">Primary or shadow role.</param>
/// <param name="ComparisonGroupId">Links shadow and primary results that share one input, or null.</param>
public sealed record ExperimentAssignment(
    ExperimentCatalogView View,
    string VariantId,
    ExperimentRole Role,
    string? ComparisonGroupId
)
{
    /// <summary>The experiment that owns the chosen variant.</summary>
    public string ExperimentId => View.ExperimentId;

    /// <summary>The chosen variant.</summary>
    public ExperimentVariant Variant => View.Variants[VariantId];
}

/// <summary>Why a call used the configured default instead of a treatment.</summary>
/// <remarks>
/// Each reason is closed and sanitized so evidence can record it without copying source text.
/// </remarks>
public enum ExperimentFallbackReason
{
    /// <summary>The source explicitly has no override for this command.</summary>
    NoOverride,

    /// <summary>The host selector chose the configured default.</summary>
    SelectorChoseDefault,

    /// <summary>A source did not answer before its deadline.</summary>
    Timeout,

    /// <summary>A source could not be reached or failed.</summary>
    Unavailable,

    /// <summary>A source returned only part of a release.</summary>
    IncompleteView,

    /// <summary>A release failed validation.</summary>
    InvalidRelease,

    /// <summary>The host selector failed.</summary>
    SelectorFailed,

    /// <summary>The view targets another compiled command revision than the configured default. A signature mismatch is <see cref="InvalidRelease"/>.</summary>
    StaleCommandRevision,

    /// <summary>The selector returned a variant that the view does not contain.</summary>
    UnknownVariant,

    /// <summary>
    /// Commands that share one experiment define different arms. The whole group uses its configured defaults, so no
    /// command runs an arm that another command in its experiment does not.
    /// </summary>
    IncompatibleExperimentGroup,

    /// <summary>The source service refused its own access. Actor authorization is checked earlier.</summary>
    SourceAuthorizationDenied,
}

/// <summary>Input to the host selector. It is captured once for each call.</summary>
/// <remarks>
/// The unit identifier must be stable and host-supplied, such as an account or tenant key. Trace or
/// correlation identifiers are never a valid assignment authority.
/// </remarks>
/// <param name="Lookup">The authorized lookup that produced <paramref name="View"/>.</param>
/// <param name="View">Immutable view the selector chooses from.</param>
/// <param name="Operation">Captured operation context.</param>
/// <param name="ActorId">Authenticated actor identifier, when the host has one.</param>
/// <param name="UnitId">Stable host-supplied unit key for deterministic assignment, or null.</param>
public sealed record ExperimentSelectionContext(
    ExperimentLookup Lookup,
    ExperimentCatalogView View,
    CapturedContext Operation,
    string? ActorId,
    string? UnitId
);

/// <summary>Host-chosen result of an assignment.</summary>
/// <remarks>
/// A selection cannot grant access. An authorization failure is reported by the source, not by a selector.
/// </remarks>
public closed record ExperimentSelection
{
    /// <summary>Choose a variant of the view.</summary>
    /// <param name="VariantId">Variant to run. The resolver rejects an unknown identifier.</param>
    /// <param name="Role">Primary or shadow role.</param>
    /// <param name="ComparisonGroupId">Comparison group, when the variant is part of a comparison.</param>
    public sealed record Variant(
        string VariantId,
        ExperimentRole Role = ExperimentRole.Primary,
        string? ComparisonGroupId = null
    ) : ExperimentSelection;

    /// <summary>Run the configured default instead of any treatment.</summary>
    public sealed record UseConfiguredDefault : ExperimentSelection;
}

/// <summary>Host-implemented assignment policy.</summary>
/// <remarks>
/// Implementations must be singleton-safe and deterministic for a given context when assignment must
/// be repeatable. Throwing causes the configured default, not a model call on an unknown arm.
/// </remarks>
public interface IExperimentSelector
{
    /// <summary>Chooses a variant or the configured default for one call.</summary>
    /// <param name="context">Captured, immutable selection context.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <returns>The selection to apply.</returns>
    ValueTask<ExperimentSelection> SelectAsync(
        ExperimentSelectionContext context,
        CancellationToken cancellationToken
    );
}

/// <summary>Closed result of loading one snapshot.</summary>
/// <remarks>
/// A load either yields a complete, validated snapshot or a reason to run the configured default.
/// There is no partial result.
/// </remarks>
public closed record ExperimentSnapshotResult
{
    /// <summary>A complete, validated snapshot.</summary>
    /// <param name="Snapshot">Frozen publication that composition may pin.</param>
    public sealed record Loaded(ExperimentSnapshot Snapshot) : ExperimentSnapshotResult;

    /// <summary>No usable snapshot. Callers run the configured default for each command.</summary>
    /// <param name="Reason">Sanitized reason recorded with each default result.</param>
    public sealed record Unavailable(ExperimentFallbackReason Reason) : ExperimentSnapshotResult;
}

/// <summary>Closed result of choosing one variant of a view, before any configured default is known.</summary>
/// <remarks>
/// Composition calls this once per command view and pins the choices, so later commands use the same
/// assignment even when a selector is nondeterministic. Command contracts are checked when each command is prepared.
/// </remarks>
public closed record ExperimentChoice
{
    /// <summary>The selector chose a validated variant of the view.</summary>
    /// <param name="Assignment">Frozen assignment to record and run.</param>
    public sealed record Chosen(ExperimentAssignment Assignment) : ExperimentChoice;

    /// <summary>No treatment applies. The caller runs its configured default for the command.</summary>
    /// <param name="Reason">Sanitized reason recorded with the result.</param>
    public sealed record UseDefault(ExperimentFallbackReason Reason) : ExperimentChoice;
}

/// <summary>
/// One complete snapshot with every command's choice made once. Composition stores this value as its pin,
/// so a deferred step resolves from storage without a second source read or selector call.
/// </summary>
/// <param name="Snapshot">Frozen publication that all choices came from.</param>
/// <param name="Choices">The choice for each command in <paramref name="Snapshot"/>, keyed by command identifier.</param>
public sealed record ExperimentPin(
    ExperimentSnapshot Snapshot,
    IReadOnlyDictionary<string, ExperimentChoice> Choices
)
{
    /// <summary>Looks up the pinned choice for one command.</summary>
    /// <param name="commandId">Stable command identifier.</param>
    /// <param name="choice">Pinned choice on success; otherwise null.</param>
    /// <returns>True when the pin contains the command.</returns>
    public bool TryGetChoice(string commandId, out ExperimentChoice? choice) => Choices.TryGetValue(commandId, out choice);
}

/// <summary>Closed result of assigning one call to a variant or the default.</summary>
/// <remarks>
/// A <see cref="ConfiguredDefault"/> result is always the complete default for the command, and it never
/// mixes a treatment template with default routing. Actor authorization happens before any lookup.
/// </remarks>
public closed record ExperimentResolution
{
    /// <summary>A treatment was chosen from a validated view.</summary>
    /// <param name="Assignment">Frozen assignment that the call must use and record.</param>
    public sealed record Treatment(ExperimentAssignment Assignment) : ExperimentResolution;

    /// <summary>The complete configured default was used.</summary>
    /// <param name="View">Configured default to run as one unit.</param>
    /// <param name="Reason">Sanitized reason recorded with the result.</param>
    public sealed record ConfiguredDefault(ConfiguredDefaultView View, ExperimentFallbackReason Reason) : ExperimentResolution;
}
