using System.Text.Json;

namespace Zeeq.LlmCmd;

/// <summary>A parameter value and the authority that supplied it for one experiment execution.</summary>
/// <param name="Name">Case-sensitive CLR parameter name.</param>
/// <param name="TypeId">Stable parameter type identifier.</param>
/// <param name="Value">Captured JSON value.</param>
/// <param name="Origin">Bounded origin such as caller, default, or external override.</param>
/// <param name="JsonName">Serialized name, when available.</param>
public sealed record ExperimentParameter(
    string Name,
    string TypeId,
    JsonElement Value,
    string Origin,
    string? JsonName = null
);

/// <summary>
/// Captured experiment identity and the actual configuration associated with an output.
/// A host controls how sensitive prompt and parameter content is retained or redacted.
/// </summary>
/// <param name="CommandId">Compiled command identity.</param>
/// <param name="CommandRevision">Compiled command revision.</param>
/// <param name="DefinitionHash">Compiled definition fingerprint.</param>
/// <param name="Source">Whether the configured default or an external source supplied the view.</param>
/// <param name="Revision">Immutable source revision, when available.</param>
/// <param name="ExperimentId">Experiment identifier, when assigned.</param>
/// <param name="VariantId">Variant that actually ran, when assigned.</param>
/// <param name="Reason">Sanitized source fallback or selection reason, when applicable.</param>
/// <param name="Role">Primary or shadow role.</param>
/// <param name="ComparisonGroupId">Links independent results for one comparison, without granting access.</param>
/// <param name="SystemTemplate">Effective unrendered system template, subject to capture policy.</param>
/// <param name="UserTemplate">Effective unrendered user template, subject to capture policy.</param>
/// <param name="FormattedSystem">Effective system message, subject to capture policy.</param>
/// <param name="FormattedUser">Effective user message, subject to capture policy.</param>
/// <param name="Parameters">Effective parameter values and origins, subject to capture policy.</param>
/// <param name="Client">Requested registered client, when known.</param>
/// <param name="Model">Requested model or deployment, when known.</param>
/// <param name="Settings">Effective request defaults, when known.</param>
/// <param name="Attempts">Observed provider attempts, when available.</param>
/// <param name="Members">Exact component outputs used for an aggregate, when applicable.</param>
/// <param name="CompositionMembers">Slot names, exact outputs, and attachment provenance for an aggregate.</param>
public sealed record ExperimentEvidence(
    string Source,
    string? Revision = null,
    string? ExperimentId = null,
    string? VariantId = null,
    string? Reason = null,
    ExperimentRole Role = ExperimentRole.Primary,
    string? ComparisonGroupId = null,
    string? SystemTemplate = null,
    string? UserTemplate = null,
    string? FormattedSystem = null,
    string? FormattedUser = null,
    IReadOnlyList<ExperimentParameter>? Parameters = null,
    string? Client = null,
    string? Model = null,
    ProfileDefaults? Settings = null,
    IReadOnlyList<Attempt>? Attempts = null,
    IReadOnlyList<OutputRef>? Members = null,
    string? CommandId = null,
    int? CommandRevision = null,
    string? DefinitionHash = null,
    IReadOnlyList<CompositeMember>? CompositionMembers = null
);
