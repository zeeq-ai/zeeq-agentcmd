using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Zeeq.LlmCmd.Providers;

/// <summary>Policies for content capture and isolated extraction or sink failures.</summary>
public sealed record ProviderExtractionOptions
{
    /// <summary>Raw content is off by default. When on, text is bounded per item; tool arguments are never captured.</summary>
    public RawContentPolicy RawContent { get; init; } = RawContentPolicy.Off;

    /// <summary>Reports an isolated adapter or sink defect. The model result is never changed by it.</summary>
    public Action<string, Exception>? OnFailure { get; init; }

    /// <summary>Invokes the optional failure observer. The observer is itself untrusted: its exceptions are swallowed.</summary>
    internal void ReportFailure(string stage, Exception error)
    {
        try
        {
            OnFailure?.Invoke(stage, error);
        }
        catch
        { /* an observer defect must not affect the model result */
        }
    }
}

/// <summary>Reads a MEAI response (or a streamed attempt) through the registered adapter and returns an immutable normalized snapshot.</summary>
public sealed class ProviderExtractor(
    ProviderAdapterRegistry registry,
    ProviderExtractionOptions? options = null
)
{
    static readonly string MeaiVersion =
        typeof(ChatResponse)
            .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? typeof(ChatResponse).Assembly.GetName().Version?.ToString()
        ?? "unknown";
    readonly ProviderExtractionOptions options = options ?? new();

    /// <summary>The single options source. Middleware reads raw-content policy and failure observer from here so both paths agree.</summary>
    public ProviderExtractionOptions Options => options;

    /// <summary>Extract one complete (non-streamed) response. <paramref name="explicitWire"/> is an explicit raw JSON body that takes precedence over RawRepresentation.</summary>
    public ExtractionResult Extract(
        ProviderIdentity registration,
        ChatResponse response,
        string attemptId,
        JsonElement? explicitWire = null
    )
    {
        ArgumentNullException.ThrowIfNull(response);
        var attempt = Begin(registration, attemptId, streaming: false);
        var shape = Shape(response.Messages.SelectMany(m => m.Contents), options.RawContent);
        return attempt.Complete(
            response.Usage,
            response.RawRepresentation,
            explicitWire,
            response.ResponseId,
            response.ModelId,
            response.FinishReason?.Value,
            shape.Shape,
            shape.Raw,
            AttemptEnd.Completed,
            []
        );
    }

    /// <summary>Resolve the adapter once and freeze it for the attempt.</summary>
    public ExtractionAttempt Begin(ProviderIdentity registration, string attemptId, bool streaming)
    {
        var resolution = registry.Resolve(registration);
        return new ExtractionAttempt(
            this,
            registry,
            options,
            registration,
            attemptId,
            streaming,
            resolution
        );
    }

    internal static (ContentShape Shape, List<CapturedContent>? Raw) Shape(
        IEnumerable<AIContent> contents,
        RawContentPolicy policy
    )
    {
        int text = 0,
            reasoning = 0,
            tools = 0,
            other = 0;
        List<CapturedContent>? raw = policy.Capture ? [] : null;
        foreach (var c in contents)
        {
            switch (c)
            {
                case TextReasoningContent r:
                    reasoning++;
                    raw?.Add(Bound("reasoning", r.Text, policy));
                    break;
                case TextContent t:
                    text++;
                    raw?.Add(Bound("text", t.Text, policy));
                    break;
                case FunctionCallContent f:
                    tools++;
                    raw?.Add(Bound("tool_call", $"{f.Name} id={f.CallId}", policy));
                    break;
                case UsageContent:
                    break;
                default:
                    other++;
                    break;
            }
        }

        return (new ContentShape(text, reasoning, tools, other), raw);
    }

    internal static CapturedContent Bound(string kind, string? text, RawContentPolicy policy)
    {
        text ??= "";
        return text.Length <= policy.MaxCharsPerItem
            ? new(kind, text, false)
            : new(kind, text[..policy.MaxCharsPerItem], true);
    }

    internal static string MeaiVersionString => MeaiVersion;
}

/// <summary>Request-local extraction state for exactly one provider attempt. Never share between attempts.</summary>
public sealed class ExtractionAttempt
{
    readonly ProviderExtractor owner;
    readonly ProviderAdapterRegistry registry;
    readonly ProviderExtractionOptions options;
    readonly ProviderIdentity registration;
    readonly string attemptId;
    readonly AdapterResolution resolution;
    int usageFrames;
    UsageDetails? latestFrame;
    int finished;

    internal ExtractionAttempt(
        ProviderExtractor owner,
        ProviderAdapterRegistry registry,
        ProviderExtractionOptions options,
        ProviderIdentity registration,
        string attemptId,
        bool streaming,
        AdapterResolution resolution
    )
    {
        this.owner = owner;
        this.registry = registry;
        this.options = options;
        this.registration = registration;
        this.attemptId = attemptId;
        IsStreaming = streaming;
        this.resolution = resolution;
    }

    /// <summary>Whether this attempt consumes a streaming response.</summary>
    public bool IsStreaming { get; }

    /// <summary>Number of normalized usage frames observed.</summary>
    public int UsageFrames => usageFrames;
    IProviderResponseAdapter? Adapter => (resolution as AdapterResolution.Matched)?.Adapter;

    /// <summary>Streaming: observe one normalized usage frame. The adapter declares whether later frames replace earlier ones; frames are never summed.</summary>
    public void ObserveUsageFrame(UsageDetails usage)
    {
        usageFrames++;
        latestFrame = usage;
    }

    /// <summary>The most recent usage frame, or null if none arrived.</summary>
    public UsageDetails? LatestUsageFrame => latestFrame;

    /// <summary>Streaming finalization. Idempotent: only the first call returns a result.</summary>
    public ExtractionResult? FinishStream(
        AttemptEnd end,
        string? responseId,
        string? modelId,
        string? finishReason,
        ContentShape shape,
        IReadOnlyList<CapturedContent>? raw
    )
    {
        if (Interlocked.Exchange(ref finished, 1) != 0)
        {
            return null;
        }

        var extra = new List<Diagnostic>
        {
            new(
                Severity.Warning,
                "stream-native-not-extracted",
                "stream",
                "Streaming extraction reads normalized UsageContent only; native stream events are not extracted, so coverage is never complete"
            ),
        };
        if (usageFrames > 1)
        {
            extra.Add(
                Adapter?.StreamUsage == StreamUsageMode.LatestSnapshot
                    ? new(
                        Severity.Info,
                        "stream-usage-replaced",
                        "stream",
                        $"{usageFrames} usage frames observed; the declared cumulative/terminal rule kept the latest and did not sum"
                    )
                    : new(
                        Severity.Warning,
                        "stream-usage-frames-ambiguous",
                        "stream",
                        $"{usageFrames} usage frames observed and the adapter did not declare cumulative or delta semantics; the latest frame was kept and nothing was summed"
                    )
            );
        }

        if (end != AttemptEnd.Completed)
        {
            extra.Add(
                new(
                    Severity.Warning,
                    "attempt-" + end.ToString().ToLowerInvariant(),
                    "stream",
                    $"Attempt ended as {end}; any missing final accounting stays unknown"
                )
            );
        }

        return Complete(
            latestFrame,
            null,
            null,
            responseId,
            modelId,
            finishReason,
            shape,
            raw,
            end,
            extra
        );
    }

    internal ExtractionResult Complete(
        UsageDetails? usage,
        object? native,
        JsonElement? explicitWire,
        string? responseId,
        string? modelId,
        string? finishReason,
        ContentShape shape,
        IReadOnlyList<CapturedContent>? raw,
        AttemptEnd end,
        List<Diagnostic> diagnostics
    )
    {
        var builder = new UsageBuilder();
        ReadBaseline(usage, Adapter, builder, diagnostics);
        string? nativeStop = null;
        ProviderBillingContext? billing = null;
        switch (resolution)
        {
            case AdapterResolution.Ambiguous a:
                diagnostics.Add(
                    new(
                        Severity.Warning,
                        "ambiguous-adapter",
                        "registry",
                        $"Equal-precedence adapters: {string.Join(", ", a.AdapterIds)}; using normalized fields only"
                    )
                );
                break;
            case AdapterResolution.NormalizedOnly n:
                diagnostics.Add(new(Severity.Warning, "no-adapter", "registry", n.Reason));
                break;
            case AdapterResolution.Matched m when !IsStreaming:
                if (AcquireWire(m.Adapter, native, explicitWire, diagnostics) is { } wire)
                {
                    var clone = builder.Clone();
                    var local = new List<Diagnostic>();
                    var ctx = new AdapterContext(wire, clone, local);
                    try
                    {
                        m.Adapter.Enrich(ctx);
                        builder = clone;
                        diagnostics.AddRange(local);
                        nativeStop = ctx.NativeStopReason;
                        billing = ctx.BillingContext;
                    }
                    catch (Exception e)
                    {
                        // A defect in the adapter keeps the baseline and drops its partial writes.
                        diagnostics.Add(
                            new(
                                Severity.Warning,
                                "adapter-failed",
                                m.Adapter.Id,
                                $"Adapter threw {e.GetType().Name}; normalized baseline kept"
                            )
                        );
                        options.ReportFailure("adapter", e);
                    }
                }
                break;
        }
        if (builder.IsEmpty && !diagnostics.Any(d => d.Severity == Severity.Error))
        {
            diagnostics.Add(
                new(
                    Severity.Warning,
                    "usage-absent",
                    "usage",
                    "No usage was reported; counts stay unknown, not zero"
                )
            );
        }

        var a2 = Adapter;
        var snapshot = new ResponseSnapshot(
            attemptId,
            registration,
            a2?.Id ?? "normalized-only",
            a2?.Version,
            a2?.RulesRevision,
            ProviderExtractor.MeaiVersionString,
            IsStreaming,
            end,
            responseId,
            modelId,
            finishReason,
            nativeStop,
            builder.Build(),
            shape,
            raw,
            [.. diagnostics]
        )
        {
            BillingContext = billing,
        };
        return ExtractionResult.From(snapshot);
    }

    JsonElement? AcquireWire(
        IProviderResponseAdapter adapter,
        object? native,
        JsonElement? explicitWire,
        List<Diagnostic> diagnostics
    )
    {
        JsonElement? wire = explicitWire;
        if (wire is null && native is not null)
        {
            switch (native)
            {
                case JsonElement je:
                    wire = je;
                    break;
                case JsonDocument jd:
                    wire = jd.RootElement;
                    break;
                case JsonNode jn:
                    // A native JSON tree needs no runtime CLR serializer graph.
                    using (var buffer = new MemoryStream())
                    {
                        using (var writer = new Utf8JsonWriter(buffer))
                        {
                            jn.WriteTo(writer);
                        }
                        using var document = JsonDocument.Parse(buffer.ToArray());
                        wire = document.RootElement.Clone();
                    }
                    break;
                default:
                    if (registry.FindProjector(adapter.Api, native) is not { } projector)
                    {
                        diagnostics.Add(
                            new(
                                Severity.Warning,
                                "unsupported-native",
                                "raw",
                                $"No projector for native type {native.GetType().FullName}; normalized usage kept"
                            )
                        );
                        return null;
                    }
                    try
                    {
                        wire = projector.Project(native);
                    }
                    catch (Exception e)
                    {
                        diagnostics.Add(
                            new(
                                Severity.Warning,
                                "native-projection-failed",
                                projector.Id,
                                $"Projector threw {e.GetType().Name}; normalized usage kept"
                            )
                        );
                        options.ReportFailure("projector", e);
                        return null;
                    }
                    break;
            }
        }

        if (wire is null)
        {
            diagnostics.Add(
                new(
                    Severity.Warning,
                    "native-unavailable",
                    "raw",
                    "No native response or raw JSON was available; provider-specific enrichment skipped"
                )
            );
            return null;
        }
        if (wire.Value.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(
                new(
                    Severity.Error,
                    "invalid-shape",
                    "raw",
                    $"Wire body must be a JSON object but was {wire.Value.ValueKind}"
                )
            );
            return null;
        }
        return wire;
    }

    static void ReadBaseline(
        UsageDetails? u,
        IProviderResponseAdapter? adapter,
        UsageBuilder b,
        List<Diagnostic> diagnostics
    )
    {
        if (u is null)
        {
            return;
        }

        void Core(long? v, string dim, Relation rel, string? parent, string path)
        {
            if (v is null)
            {
                return;
            }

            if (v < 0)
            {
                diagnostics.Add(
                    new(
                        Severity.Error,
                        "negative-count",
                        path,
                        "Counts must not be negative; treated as unknown"
                    )
                );
                return;
            }
            b.Observe(dim, v.Value, Unit.Tokens, rel, parent, Origin.Normalized, path);
        }
        Core(u.InputTokenCount, Dim.Input, Relation.Total, null, "meai:Usage.InputTokenCount");
        Core(u.OutputTokenCount, Dim.Output, Relation.Total, null, "meai:Usage.OutputTokenCount");
        Core(u.TotalTokenCount, Dim.Total, Relation.Total, null, "meai:Usage.TotalTokenCount");
        Core(
            u.CachedInputTokenCount,
            Dim.Cached,
            Relation.SubsetOfParent,
            Dim.Input,
            "meai:Usage.CachedInputTokenCount"
        );
        Core(
            u.ReasoningTokenCount,
            Dim.Reasoning,
            Relation.SubsetOfParent,
            Dim.Output,
            "meai:Usage.ReasoningTokenCount"
        );
        Core(
            u.InputAudioTokenCount,
            Dim.InputAudio,
            Relation.SubsetOfParent,
            Dim.Input,
            "meai:Usage.InputAudioTokenCount"
        );
        Core(
            u.OutputAudioTokenCount,
            Dim.OutputAudio,
            Relation.SubsetOfParent,
            Dim.Output,
            "meai:Usage.OutputAudioTokenCount"
        );
        if (u.AdditionalCounts is null)
        {
            return;
        }

        foreach (var (key, value) in u.AdditionalCounts)
        {
            var path = $"meai:Usage.AdditionalCounts[{key}]";
            var rule =
                adapter?.AdditionalCounts.GetValueOrDefault(key)
                ?? new AdditionalCountRule(
                    Dim.AdditionalPrefix + key,
                    Unit.Unknown,
                    Relation.Unknown,
                    null
                );
            if (value < 0)
            {
                diagnostics.Add(
                    new(
                        Severity.Error,
                        "negative-count",
                        path,
                        "Counts must not be negative; treated as unknown"
                    )
                );
                continue;
            }
            b.Observe(
                rule.Dimension,
                value,
                rule.Unit,
                rule.Relation,
                rule.Parent,
                Origin.NormalizedAdditional,
                path
            );
        }
    }
}
