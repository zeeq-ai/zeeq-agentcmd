using System.Collections.Immutable;

namespace Zeeq.AgentCmd.Providers;

/// <summary>Unit of a measurement.</summary>
public enum Unit
{
    /// <summary>Model tokens.</summary>
    Tokens,
    /// <summary>Provider requests.</summary>
    Requests,
    /// <summary>Unit not established.</summary>
    Unknown,
}
/// <summary>Where one observation of a value came from. Several origins can report one fact; they are evidence, not extra consumption.</summary>
public enum Origin
{
    /// <summary>MEAI normalized usage.</summary>
    Normalized,
    /// <summary>MEAI additional count.</summary>
    NormalizedAdditional,
    /// <summary>Provider-native wire usage.</summary>
    Native,
    /// <summary>Value derived from other observations.</summary>
    Computed,
}
/// <summary>Relation of a dimension to its parent. Never sum all measurements.</summary>
public enum Relation
{
    /// <summary>A total for the dimension.</summary>
    Total,
    /// <summary>A subset that overlaps its parent.</summary>
    SubsetOfParent,
    /// <summary>A constituent part of its parent.</summary>
    PartOfParent,
    /// <summary>Consumption separate from the parent totals.</summary>
    Independent,
    /// <summary>The relationship is not established.</summary>
    Unknown,
}
/// <summary>Agreement among observations of a dimension.</summary>
public enum MeasurementStatus
{
    /// <summary>Available observations agree.</summary>
    Agreed,
    /// <summary>Observations or validation checks disagree.</summary>
    Conflicted,
}
/// <summary>Reason that usage evidence is inconsistent.</summary>
public enum ConflictKind
{
    /// <summary>Observations report different values.</summary>
    ValueMismatch,
    /// <summary>Known parts do not sum to the parent.</summary>
    PartitionSumMismatch,
    /// <summary>A subset is larger than its parent.</summary>
    SubsetExceedsParent,
}
/// <summary>Impact of an extraction diagnostic.</summary>
public enum Severity
{
    /// <summary>Informational evidence.</summary>
    Info,
    /// <summary>Extraction is partial.</summary>
    Warning,
    /// <summary>Extraction is invalid.</summary>
    Error,
}
/// <summary>Terminal state of an extraction attempt.</summary>
public enum AttemptEnd
{
    /// <summary>The response completed.</summary>
    Completed,
    /// <summary>The caller cancelled the attempt.</summary>
    Cancelled,
    /// <summary>The attempt failed.</summary>
    Failed,
    /// <summary>The stream was not completed.</summary>
    Abandoned,
}

/// <summary>Canonical dimension names. Provider-specific dimensions carry a provider prefix.</summary>
public static class Dim
{
    /// <summary>Inclusive input-token total.</summary>
    public const string Input = "input";
    /// <summary>Output-token total, including reasoning.</summary>
    public const string Output = "output";
    /// <summary>Combined input and output total.</summary>
    public const string Total = "total";
    /// <summary>Input tokens read from cache; a subset of input.</summary>
    public const string Cached = "input.cached";
    /// <summary>Input tokens outside cache reads and writes.</summary>
    public const string Uncached = "input.uncached";
    /// <summary>Input tokens written to cache; a subset of input.</summary>
    public const string CacheWrite = "input.cache_write";
    /// <summary>Five-minute cache writes; part of cache writes.</summary>
    public const string CacheWrite5m = "input.cache_write.ttl_5m";
    /// <summary>One-hour cache writes; part of cache writes.</summary>
    public const string CacheWrite1h = "input.cache_write.ttl_1h";
    /// <summary>Audio tokens within input.</summary>
    public const string InputAudio = "input.audio";
    /// <summary>Audio tokens within output.</summary>
    public const string OutputAudio = "output.audio";
    /// <summary>Reasoning tokens within output.</summary>
    public const string Reasoning = "output.reasoning";
    /// <summary>Accepted prediction tokens within output.</summary>
    public const string PredictionAccepted = "output.prediction.accepted";
    /// <summary>Rejected prediction tokens within output.</summary>
    public const string PredictionRejected = "output.prediction.rejected";
    /// <summary>Independent server-side web search requests.</summary>
    public const string WebSearchRequests = "tool.web_search.requests";
    /// <summary>Independent server-side web fetch requests.</summary>
    public const string WebFetchRequests = "tool.web_fetch.requests";
    /// <summary>DeepSeek input tokens reported as cache misses.</summary>
    public const string DeepSeekCacheMiss = "deepseek.input.cache_miss";
    /// <summary>Prefix for unmapped normalized additional counts.</summary>
    public const string AdditionalPrefix = "additional:";
}

/// <summary>One source observation of a count; multiple sources are evidence of the same usage.</summary>
/// <param name="Origin">Evidence source.</param>
/// <param name="Path">Source path identifying the observation.</param>
/// <param name="Value">Observed non-negative count.</param>
/// <param name="Note">Optional source-specific interpretation.</param>
public sealed record Observation(Origin Origin, string Path, long Value, string? Note = null);
/// <summary>A canonical count, its unit and relation, and the evidence supporting it.</summary>
/// <param name="Dimension">Canonical dimension name.</param>
/// <param name="Value">Selected observed value; inspect status before using it.</param>
/// <param name="Unit">Unit of the count.</param>
/// <param name="Relation">Relationship to its parent.</param>
/// <param name="Parent">Parent dimension, if applicable.</param>
/// <param name="Status">Whether observations and checks agree.</param>
/// <param name="Observations">Source evidence for this dimension.</param>
public sealed record UsageMeasurement(string Dimension, long Value, Unit Unit, Relation Relation, string? Parent, MeasurementStatus Status, IReadOnlyList<Observation> Observations);
/// <summary>A disagreement in usage evidence or a failed partition check.</summary>
/// <param name="Dimension">Dimension whose evidence is inconsistent.</param>
/// <param name="Kind">Kind of disagreement.</param>
/// <param name="Message">Human-readable explanation.</param>
/// <param name="Observations">Evidence relevant to the conflict.</param>
public sealed record UsageConflict(string Dimension, ConflictKind Kind, string Message, IReadOnlyList<Observation> Observations);
/// <summary>A structured extraction finding with a severity and source path.</summary>
/// <param name="Severity">Effect on extraction classification.</param>
/// <param name="Code">Stable diagnostic code.</param>
/// <param name="Path">Source path related to the finding.</param>
/// <param name="Message">Human-readable explanation.</param>
public sealed record Diagnostic(Severity Severity, string Code, string Path, string Message);

/// <summary>Immutable per-attempt usage. A missing dimension means unknown, never zero.</summary>
/// <param name="Measurements">Known measurements; omitted dimensions are unknown.</param>
/// <param name="Conflicts">Disagreements and failed relationship checks.</param>
public sealed record UsageSnapshot(IReadOnlyList<UsageMeasurement> Measurements, IReadOnlyList<UsageConflict> Conflicts)
{
    /// <summary>An empty snapshot with no known dimensions.</summary>
    public static readonly UsageSnapshot Empty = new(ImmutableArray<UsageMeasurement>.Empty, ImmutableArray<UsageConflict>.Empty);
    /// <summary>Finds a measurement by canonical dimension, or null when absent.</summary>
    public UsageMeasurement? Find(string dimension) => Measurements.FirstOrDefault(m => m.Dimension == dimension);
    /// <summary>Returns a measured value, or null when absent; inspect status before using it.</summary>
    public long? ValueOf(string dimension) => Find(dimension)?.Value;
    /// <summary>Inclusive input count, if observed.</summary>
    public long? Input => ValueOf(Dim.Input);
    /// <summary>Output count, if observed.</summary>
    public long? Output => ValueOf(Dim.Output);
    /// <summary>Combined token count, if observed.</summary>
    public long? Total => ValueOf(Dim.Total);
    /// <summary>Indicates whether the named dimension has a recorded conflict.</summary>
    public bool HasConflict(string dimension) => Conflicts.Any(c => c.Dimension == dimension);
}

/// <summary>Counts of response content kinds without preserving their text.</summary>
/// <param name="TextItems">Number of text items.</param>
/// <param name="ReasoningItems">Number of reasoning items.</param>
/// <param name="ToolCalls">Number of tool calls.</param>
/// <param name="OtherItems">Number of other content items.</param>
public sealed record ContentShape(int TextItems, int ReasoningItems, int ToolCalls, int OtherItems);
/// <summary>Opt-in captured content with its kind and truncation state.</summary>
/// <param name="Kind">Kind of captured content.</param>
/// <param name="Text">Captured text subject to the capture policy.</param>
/// <param name="Truncated">Whether the text was truncated.</param>
public sealed record CapturedContent(string Kind, string Text, bool Truncated);
/// <summary>Opt-in policy for capturing a bounded amount of response text.</summary>
/// <param name="Capture">Whether to capture content; false by default.</param>
/// <param name="MaxCharsPerItem">Maximum characters retained for each content item.</param>
public sealed record RawContentPolicy(bool Capture = false, int MaxCharsPerItem = 2000)
{
    /// <summary>Default policy that does not capture content.</summary>
    public static readonly RawContentPolicy Off = new();
}

/// <summary>
/// Safe billing categories. Null means absent; "unknown" means present but invalid
/// or unsupported. The distinction prevents a new provider tier or inference
/// region from silently inheriting a cheaper caller-assumed standard rate.
/// </summary>
/// <param name="ServiceTier">Observed service tier, null if absent, or unknown if unsupported.</param>
/// <param name="InferenceGeo">Observed inference geography, null if absent, or unknown if unsupported.</param>
/// <param name="Speed">Observed speed category, null if absent, or unknown if unsupported.</param>
public sealed record ProviderBillingContext(string? ServiceTier, string? InferenceGeo, string? Speed);

/// <summary>Immutable normalized result of one provider attempt. It holds no native object graph.</summary>
/// <param name="AttemptId">Host-assigned identifier for this attempt.</param>
/// <param name="Registration">Explicit provider and protocol registration.</param>
/// <param name="AdapterId">Selected adapter identity.</param>
/// <param name="AdapterVersion">Adapter implementation version, if available.</param>
/// <param name="RulesRevision">Extraction rule revision, if available.</param>
/// <param name="MeaiVersion">Version of the MEAI abstraction used for extraction.</param>
/// <param name="IsStreaming">Whether this attempt used streaming.</param>
/// <param name="End">Terminal attempt state.</param>
/// <param name="ResponseId">Provider response identifier, if present.</param>
/// <param name="ModelId">Provider-reported model identifier, if present.</param>
/// <param name="FinishReason">Normalized completion reason, if present.</param>
/// <param name="NativeStopReason">Provider-specific stop reason, if present.</param>
/// <param name="Usage">Normalized usage evidence; missing dimensions remain unknown.</param>
/// <param name="Content">Counts of response content kinds.</param>
/// <param name="RawContent">Opt-in bounded content, or null when capture is disabled.</param>
/// <param name="Diagnostics">Extraction warnings and errors.</param>
public sealed record ResponseSnapshot(
    string AttemptId, ProviderIdentity Registration, string AdapterId, string? AdapterVersion, string? RulesRevision,
    string MeaiVersion, bool IsStreaming, AttemptEnd End, string? ResponseId, string? ModelId, string? FinishReason, string? NativeStopReason,
    UsageSnapshot Usage, ContentShape Content, IReadOnlyList<CapturedContent>? RawContent, IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>Requested model or deployment target; separate from the provider-reported model used for pricing.</summary>
    public string? RequestedModelId
    {
        get; init;
    }

    /// <summary>Observed billing categories; null means no categories were captured.</summary>
    public ProviderBillingContext? BillingContext
    {
        get; init;
    }
}

/// <summary>Closed extraction result. Every case keeps the valid normalized baseline. Unsupported native/protocol shapes are <see cref="Partial"/>.</summary>
public closed record ExtractionResult(ResponseSnapshot Snapshot)
{
    /// <summary>Extraction completed without warnings or conflicts.</summary>
    public sealed record Extracted(ResponseSnapshot Snapshot) : ExtractionResult(Snapshot);
    /// <summary>Extraction retained useful evidence but has warnings or conflicts.</summary>
    public sealed record Partial(ResponseSnapshot Snapshot) : ExtractionResult(Snapshot);
    /// <summary>Extraction retained a baseline but has at least one error.</summary>
    public sealed record Invalid(ResponseSnapshot Snapshot) : ExtractionResult(Snapshot);
    /// <summary>Classifies a snapshot from its diagnostics and usage conflicts.</summary>
    public static ExtractionResult From(ResponseSnapshot s) =>
        s.Diagnostics.Any(d => d.Severity == Severity.Error) ? new Invalid(s)
        : s.Diagnostics.Any(d => d.Severity == Severity.Warning) || s.Usage.Conflicts.Count > 0 ? new Partial(s)
        : new Extracted(s);
}
