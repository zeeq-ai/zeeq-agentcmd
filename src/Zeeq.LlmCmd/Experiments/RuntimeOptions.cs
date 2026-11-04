using Microsoft.Extensions.DependencyInjection;

namespace Zeeq.LlmCmd;

/// <summary>Singleton-safe host policies for runtime experiment selection and optional shadows.</summary>
/// <param name="Resolver">Resolver backed by a singleton-safe source.</param>
/// <param name="AssignmentUnit">Optional stable host assignment key, derived from the authorized actor.</param>
/// <param name="RoutePolicy">Actor-aware authorization and compatibility check for every dynamic route; null denies dynamic routes.</param>
/// <param name="ConfigureDefault">Optional local, complete default view factory.</param>
/// <param name="Scheduler">Optional scheduler for independently scoped shadow work.</param>
/// <param name="SelectShadows">Optional host choice of shadow variant identifiers from the selected immutable view.</param>
/// <param name="ShadowTimeout">Per-shadow cooperative timeout; null uses 30 seconds.</param>
/// <param name="MaxShadows">Maximum offered variants per primary, between zero and sixteen.</param>
/// <param name="DefaultConfigurationFingerprint">Stable host version for the complete local default; required for composition pins.</param>
/// <param name="ShadowScopeFactory">Host-owned factory creating an independent DI scope per shadow.</param>
/// <param name="ShadowPolicy">Explicit host approval for side effects and data destinations of each shadow.</param>
/// <param name="ObserveShadowResult">Optional nonblocking, best-effort result observer; notifications are not durable.</param>
/// <param name="ObserveShadow">Optional nonblocking disposition observer.</param>
public sealed record ExperimentRuntimeOptions(
    ExperimentResolver Resolver,
    Func<Actor, string?>? AssignmentUnit = null,
    Func<Actor, ExecutionProfile, bool>? RoutePolicy = null,
    Func<ConfiguredDefaultView, ConfiguredDefaultView>? ConfigureDefault = null,
    IShadowScheduler? Scheduler = null,
    Func<Actor, ExperimentCatalogView, IReadOnlyList<string>>? SelectShadows = null,
    TimeSpan? ShadowTimeout = null,
    Action<ShadowDisposition>? ObserveShadow = null,
    int MaxShadows = 2,
    string? DefaultConfigurationFingerprint = null,
    IServiceScopeFactory? ShadowScopeFactory = null,
    Func<Actor, ExperimentVariant, bool>? ShadowPolicy = null,
    Action<ShadowExecutionResult>? ObserveShadowResult = null
);

/// <summary>Best-effort correlation and exact stored output for a shadow, when storage succeeded.</summary>
/// <param name="ComparisonGroupId">Host-visible comparison group, not authorization.</param>
/// <param name="VariantId">Shadow variant identifier.</param>
/// <param name="Disposition">Terminal queue disposition.</param>
/// <param name="Output">Exact output reference, available only after successful storage.</param>
/// <remarks>Notifications are process-local and can be lost. Durable discovery requires a durable host implementation.</remarks>
public sealed record ShadowExecutionResult(
    string ComparisonGroupId,
    string VariantId,
    ShadowDisposition Disposition,
    OutputRef? Output
);
