using System.Collections.Immutable;

namespace ProviderAdapters;

public enum Unit { Tokens, Requests, Unknown }
/// <summary>Where one observation of a value came from. Several origins can report one fact; they are evidence, not extra consumption.</summary>
public enum Origin { Normalized, NormalizedAdditional, Native, Computed }
/// <summary>Relation of a dimension to its parent. Never sum all measurements.</summary>
public enum Relation { Total, SubsetOfParent, PartOfParent, Independent, Unknown }
public enum MeasurementStatus { Agreed, Conflicted }
public enum ConflictKind { ValueMismatch, PartitionSumMismatch, SubsetExceedsParent }
public enum Severity { Info, Warning, Error }
public enum AttemptEnd { Completed, Cancelled, Failed, Abandoned }

/// <summary>Canonical dimension names. Provider-specific dimensions carry a provider prefix.</summary>
public static class Dim
{
    public const string Input = "input", Output = "output", Total = "total";
    public const string Cached = "input.cached", Uncached = "input.uncached", CacheWrite = "input.cache_write";
    public const string CacheWrite5m = "input.cache_write.ttl_5m", CacheWrite1h = "input.cache_write.ttl_1h";
    public const string InputAudio = "input.audio", OutputAudio = "output.audio", Reasoning = "output.reasoning";
    public const string PredictionAccepted = "output.prediction.accepted", PredictionRejected = "output.prediction.rejected";
    public const string WebSearchRequests = "tool.web_search.requests", WebFetchRequests = "tool.web_fetch.requests";
    public const string DeepSeekCacheMiss = "deepseek.input.cache_miss";
    public const string AdditionalPrefix = "additional:";
}

public sealed record Observation(Origin Origin, string Path, long Value, string? Note = null);
public sealed record UsageMeasurement(string Dimension, long Value, Unit Unit, Relation Relation, string? Parent, MeasurementStatus Status, IReadOnlyList<Observation> Observations);
public sealed record UsageConflict(string Dimension, ConflictKind Kind, string Message, IReadOnlyList<Observation> Observations);
public sealed record Diagnostic(Severity Severity, string Code, string Path, string Message);

/// <summary>Immutable per-attempt usage. A missing dimension means unknown, never zero.</summary>
public sealed record UsageSnapshot(IReadOnlyList<UsageMeasurement> Measurements, IReadOnlyList<UsageConflict> Conflicts)
{
    public static readonly UsageSnapshot Empty = new(ImmutableArray<UsageMeasurement>.Empty, ImmutableArray<UsageConflict>.Empty);
    public UsageMeasurement? Find(string dimension) => Measurements.FirstOrDefault(m => m.Dimension == dimension);
    public long? ValueOf(string dimension) => Find(dimension)?.Value;
    public long? Input => ValueOf(Dim.Input);
    public long? Output => ValueOf(Dim.Output);
    public long? Total => ValueOf(Dim.Total);
    public bool HasConflict(string dimension) => Conflicts.Any(c => c.Dimension == dimension);
}

public sealed record ContentShape(int TextItems, int ReasoningItems, int ToolCalls, int OtherItems);
public sealed record CapturedContent(string Kind, string Text, bool Truncated);
public sealed record RawContentPolicy(bool Capture = false, int MaxCharsPerItem = 2000)
{
    public static readonly RawContentPolicy Off = new();
}

/// <summary>Immutable normalized result of one provider attempt. It holds no native object graph.</summary>
public sealed record ResponseSnapshot(
    string AttemptId, ProviderRegistration Registration, string AdapterId, string? AdapterVersion, string? RulesRevision,
    string MeaiVersion, bool IsStreaming, AttemptEnd End, string? ResponseId, string? ModelId, string? FinishReason, string? NativeStopReason,
    UsageSnapshot Usage, ContentShape Content, IReadOnlyList<CapturedContent>? RawContent, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>Closed extraction result. Every case keeps the valid normalized baseline. Unsupported native/protocol shapes are <see cref="Partial"/>.</summary>
public closed record ExtractionResult(ResponseSnapshot Snapshot)
{
    public sealed record Extracted(ResponseSnapshot Snapshot) : ExtractionResult(Snapshot);
    public sealed record Partial(ResponseSnapshot Snapshot) : ExtractionResult(Snapshot);
    public sealed record Invalid(ResponseSnapshot Snapshot) : ExtractionResult(Snapshot);
    public static ExtractionResult From(ResponseSnapshot s) =>
        s.Diagnostics.Any(d => d.Severity == Severity.Error) ? new Invalid(s)
        : s.Diagnostics.Any(d => d.Severity == Severity.Warning) || s.Usage.Conflicts.Count > 0 ? new Partial(s)
        : new Extracted(s);
}
