using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeeq.LlmCmd;

/// <summary>
/// Wire shape of the file-backed experiment document. Every field is nullable, so a missing or wrong value reaches
/// the mapper as null and becomes an invalid release. No partial document is ever served.
/// </summary>
/// <remarks>
/// These types stay internal so the public API is only <see cref="FileExperimentSnapshotSource"/>. The format is
/// versioned by <see cref="ExperimentFileDocument.Format"/>, so an incompatible edit fails closed.
/// </remarks>
internal sealed class ExperimentFileDocument
{
    /// <summary>Format version. Only version 1 is accepted.</summary>
    public int? Format { get; set; }

    /// <summary>Publications keyed by the opaque partition text that the host passes to lookups.</summary>
    public Dictionary<string, ExperimentFilePartition?>? Partitions { get; set; }
}

/// <summary>One partition's publication. Every command in it shares the revision, so one edit publishes atomically.</summary>
internal sealed class ExperimentFilePartition
{
    /// <summary>Immutable revision name for this publication.</summary>
    public string? Revision { get; set; }

    /// <summary>Commands keyed by command identifier.</summary>
    public Dictionary<string, ExperimentFileCommand?>? Commands { get; set; }
}

/// <summary>One command's experiment. The identity fields are the same values a generated command publishes.</summary>
internal sealed class ExperimentFileCommand
{
    /// <summary>Compiled command revision, as reported by the command's experiment contract.</summary>
    public int? CommandRevision { get; set; }

    /// <summary>Contract signature the view was authored against; the compatibility gate.</summary>
    public string? ContractSignature { get; set; }

    /// <summary>Experiment that owns the variants.</summary>
    public string? ExperimentId { get; set; }

    /// <summary>Variant used when no selector chooses another.</summary>
    public string? ControlVariantId { get; set; }

    /// <summary>Variants keyed by identifier.</summary>
    public Dictionary<string, ExperimentFileVariant?>? Variants { get; set; }
}

/// <summary>One variant: its share, prompt changes, typed parameter overrides, and optional route.</summary>
internal sealed class ExperimentFileVariant
{
    /// <summary>Relative share. A missing weight is invalid, never zero, so traffic cannot shift silently.</summary>
    public int? Weight { get; set; }

    /// <summary>Prompt changes for the system and user templates.</summary>
    public ExperimentFilePrompt? Prompt { get; set; }

    /// <summary>Typed parameter overrides keyed by case-sensitive CLR property name.</summary>
    public Dictionary<string, JsonElement>? Parameters { get; set; }

    /// <summary>Route for this variant; omitted means the configured base route.</summary>
    public ExperimentFileRoute? Route { get; set; }
}

/// <summary>Prompt changes. The JSON names are "system" and "user".</summary>
internal sealed class ExperimentFilePrompt
{
    /// <summary>System template change.</summary>
    [JsonPropertyName("system")]
    public ExperimentFileTemplate? SystemTemplate { get; set; }

    /// <summary>User template change.</summary>
    [JsonPropertyName("user")]
    public ExperimentFileTemplate? UserTemplate { get; set; }
}

/// <summary>One template change: "inherit", "removed", or "replaced" with text.</summary>
internal sealed class ExperimentFileTemplate
{
    /// <summary>"inherit", "removed", or "replaced". Anything else is invalid.</summary>
    public string? Mode { get; set; }

    /// <summary>Replacement text; required when <see cref="Mode"/> is "replaced".</summary>
    public string? Text { get; set; }
}

/// <summary>Route override. Sampling fields go in the profile defaults, not on the route itself.</summary>
internal sealed class ExperimentFileRoute
{
    /// <summary>Declared profile key; it must be a valid profile identifier.</summary>
    public string? Key { get; set; }

    /// <summary>Provider name for the profile.</summary>
    public string? Provider { get; set; }

    /// <summary>Model name for the profile.</summary>
    public string? Model { get; set; }

    /// <summary>Sampling temperature; null preserves the command or provider default.</summary>
    public float? Temperature { get; set; }

    /// <summary>Output budget; null preserves the default.</summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>Nucleus sampling value; null preserves the default.</summary>
    public float? TopP { get; set; }

    /// <summary>Top-k limit; null preserves the default.</summary>
    public int? TopK { get; set; }
}
