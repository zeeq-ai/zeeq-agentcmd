using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Zeeq.AgentCmd;

namespace AgentCmd.Composition.Tests;

// ---- A small composite: four required parts, one optional part. ----
// WHY the serializer options: camelCase plus WhenWritingNull is exactly the combination that could drop a required
// nullable member if the aggregate were re-serialized from the typed whole. The tests pin that the runtime does not.
public sealed record Header([property: JsonRequired] string Title, string? Reference);

public sealed record Parties(string Buyer, string Seller);

public sealed record LineItems(List<string> Items);

public sealed record Totals(decimal Total, string Currency);

public sealed record Summary(string Text);

public sealed record Doc(
    Header Header,
    Parties Parties,
    [property: JsonPropertyName("line_items")] LineItems Lines,
    Totals Totals,
    Summary? Summary = null
);

public sealed record DocParams(string Text);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[
    JsonSerializable(typeof(Doc)),
    JsonSerializable(typeof(Header)),
    JsonSerializable(typeof(Parties)),
    JsonSerializable(typeof(LineItems)),
    JsonSerializable(typeof(Totals)),
    JsonSerializable(typeof(Summary)),
    JsonSerializable(typeof(DocParams))
]
public partial class DocJson : JsonSerializerContext;

// Same shapes, DEFAULT naming (PascalCase on the wire): an incompatible leaf contract for the camelCase aggregate.
[JsonSerializable(typeof(Header)), JsonSerializable(typeof(Totals))]
public partial class PascalJson : JsonSerializerContext;

public static class DocSlots
{
    public static readonly PartSlot<Doc, Header> Header = PartSlot<Doc, Header>.Declare(
        "header",
        "header"
    );
    public static readonly PartSlot<Doc, Parties> Parties = PartSlot<Doc, Parties>.Declare(
        "parties",
        "parties"
    );
    public static readonly PartSlot<Doc, LineItems> Lines = PartSlot<Doc, LineItems>.Declare(
        "lines",
        "line_items"
    );
    public static readonly PartSlot<Doc, Totals> Totals = PartSlot<Doc, Totals>.Declare(
        "totals",
        "totals"
    );
    public static readonly PartSlot<Doc, Summary> Summary = PartSlot<Doc, Summary>.Declare(
        "summary",
        "summary",
        required: false
    );
    public static SlotDescriptor[] All =>
        [
            Header.Descriptor,
            Parties.Descriptor,
            Lines.Descriptor,
            Totals.Descriptor,
            Summary.Descriptor,
        ];
}

sealed class Cmd<TP, TR> : PromptCommand<TP, TR>
{
    public Cmd(CommandRuntime runtime, PromptDescriptor<TP, TR> descriptor)
        : base(runtime, descriptor) { }
}

// ---- A scripted model. It answers by prompt prefix and counts every call. ----
sealed class FakeClient : IChatClient
{
    public readonly ConcurrentDictionary<string, int> Calls = new();
    public readonly ConcurrentDictionary<string, string> Answers = new()
    {
        ["HEADER"] = """{"title":"Invoice 7","reference":"R-1"}""",
        ["PARTIES"] = """{"buyer":"Acme","seller":"Globex"}""",
        ["LINES"] = """{"items":["a","b","c"]}""",
        ["TOTALS"] = """{"total":12.5,"currency":"USD"}""",
        ["SUMMARY"] = """{"text":"ok"}""",
    };
    public readonly ConcurrentDictionary<string, string> Prompts = new();
    public Func<string, CancellationToken, Task>? Before { get; set; }
    public HashSet<string> Throws { get; } = [];
    public int Total => Calls.Values.Sum();

    public int CallsFor(string key) => Calls.TryGetValue(key, out var n) ? n : 0;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var prompt = messages.Last(m => m.Role == ChatRole.User).Text;
        var key = prompt[..prompt.IndexOf(':')];
        Calls.AddOrUpdate(key, 1, (_, n) => n + 1);
        Prompts[key] = prompt;
        if (Before is not null)
        {
            await Before(key, cancellationToken);
        }

        if (Throws.Contains(key))
        {
            throw new InvalidOperationException("scripted provider failure");
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, Answers[key]));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

sealed class EventLog : IExecutionEventSink
{
    public readonly ConcurrentQueue<LlmEvent> All = new();

    /// <summary>Leaf command events (typed ExecutionEvent).</summary>
    public IEnumerable<ExecutionEvent> Events => All.OfType<ExecutionEvent>();

    /// <summary>Composition events from the engine (root, step and projected store outbox).</summary>
    public IEnumerable<CompositionEvent> Composition => All.OfType<CompositionEvent>();

    public ValueTask EmitAsync(LlmEvent record, CancellationToken cancellationToken)
    {
        All.Enqueue(record);
        return ValueTask.CompletedTask;
    }
}

// ---- Failure-injecting wrappers. They delegate to the real implementations. ----
sealed class FlakyLedger(InMemoryFeedbackLedger inner) : IHumanFeedbackLedger
{
    public Func<Guid, LedgerResult<OutputArtifact>?>? OnRecordOutput { get; set; }
    public bool RejectProduced { get; set; }
    public int RecordedOutputs;
    public readonly System.Collections.Concurrent.ConcurrentQueue<OutputRef> Produced = new();

    public Guid CreateRun(Actor actor) => inner.CreateRun(actor);

    public bool CanAccessRun(Guid runId, Actor actor) => inner.CanAccessRun(runId, actor);

    public LedgerResult<OutputArtifact> RecordOutput(
        Guid runId,
        Guid executionId,
        JsonElement value,
        string schemaVersion,
        ActivityContext origin,
        Actor actor
    )
    {
        if (OnRecordOutput?.Invoke(executionId) is { } forced)
        {
            return forced;
        }

        Interlocked.Increment(ref RecordedOutputs);
        return inner.RecordOutput(runId, executionId, value, schemaVersion, origin, actor);
    }

    public LedgerResult<OutputArtifact> RecordProducedOutput(
        Guid runId,
        ProducerRef producer,
        JsonElement value,
        string schemaVersion,
        ActivityContext origin,
        Actor actor
    ) =>
        producer is ProducerRef.Composition && RejectProduced
            ? new LedgerResult<OutputArtifact>.Rejected("scripted", "scripted")
            : Track(
                inner.RecordProducedOutput(runId, producer, value, schemaVersion, origin, actor)
            );

    LedgerResult<OutputArtifact> Track(LedgerResult<OutputArtifact> result)
    {
        if (result is LedgerResult<OutputArtifact>.Accepted ok)
        {
            Produced.Enqueue(ok.Value.Reference);
        }

        return result;
    }

    public LedgerResult<OutputArtifact> ReadOutput(OutputRef target, Actor actor) =>
        inner.ReadOutput(target, actor);

    public LedgerResult<GradeReceipt> RecordGrade(GradeRequest request, Actor actor) =>
        inner.RecordGrade(request, actor);

    public LedgerResult<CorrectionReceipt> SubmitCorrection(
        CorrectionRequest request,
        Actor actor
    ) => inner.SubmitCorrection(request, actor);
}

sealed class FlakyStore(ICompositionStore inner) : ICompositionStore
{
    public Func<AppendEvent, bool>? FailAppend { get; set; }
    public bool FailAttach { get; set; }
    public bool FailSeal { get; set; }

    /// <summary>From the Nth GetDraftAsync call on (1-based), reads fail. Null means never.</summary>
    public int? FailGetFrom { get; set; }
    int gets;

    public ValueTask<OpenDraftResult> OpenDraftAsync(OpenDraft request, CancellationToken ct) =>
        inner.OpenDraftAsync(request, ct);

    public ValueTask<DraftRecord?> GetDraftAsync(Guid draftId, CancellationToken ct) =>
        FailGetFrom is { } from && Interlocked.Increment(ref gets) >= from
            ? throw new IOException("scripted")
            : inner.GetDraftAsync(draftId, ct);

    public ValueTask<ClaimResult> ClaimSlotAsync(ClaimSlot request, CancellationToken ct) =>
        inner.ClaimSlotAsync(request, ct);

    public ValueTask<TransitionResult> AttachSlotAsync(AttachSlot request, CancellationToken ct) =>
        FailAttach ? throw new IOException("scripted") : inner.AttachSlotAsync(request, ct);

    public ValueTask<TransitionResult> ReleaseSlotAsync(
        ReleaseSlot request,
        CancellationToken ct
    ) => inner.ReleaseSlotAsync(request, ct);

    public ValueTask<TransitionResult> AppendEventAsync(
        AppendEvent request,
        CancellationToken ct
    ) =>
        FailAppend?.Invoke(request) == true
            ? throw new IOException("scripted")
            : inner.AppendEventAsync(request, ct);

    public ValueTask<SealResult> SealAsync(SealDraft request, CancellationToken ct) =>
        FailSeal ? throw new IOException("scripted") : inner.SealAsync(request, ct);
}

// ---- One fully wired world per test. ----
sealed class Rig : IDisposable
{
    public readonly InMemoryFeedbackLedger RealLedger = new();
    public readonly FlakyLedger Ledger;
    public readonly Actor Actor = new("tenant-a", "reviewer");
    public readonly Guid Run;
    public readonly FakeClient Client = new();
    public readonly EventLog Events = new();
    public readonly CommandRuntime Runtime;
    public readonly InMemoryCompositionStore RealStore = new();
    public readonly FlakyStore Store;
    public readonly CompositeDescriptor<Doc> Descriptor;
    public readonly CompositionRegistry Registry;
    public readonly RootCommands Root;
    public readonly Cmd<DocParams, Header> Header;
    public readonly Cmd<DocParams, Parties> Parties;
    public readonly Cmd<DocParams, LineItems> Lines;
    public readonly Cmd<DocParams, Totals> Totals;
    public readonly Cmd<DocParams, Summary> Summary;
    public readonly Cmd<DocParams, Header> PascalHeader;
    public readonly Cmd<DocParams, Totals> PascalTotals;

    /// <summary>The subject the host reports at call time. Tests change it to model a different caller executing a handle.</summary>
    public Actor? Acting { get; set; }

    public Rig(
        Func<Doc, string?>? validate = null,
        string hash = "hash-v1",
        Actor? rootActor = null
    )
    {
        Acting = rootActor;
        Ledger = new(RealLedger);
        Store = new(RealStore);
        Run = RealLedger.CreateRun(Actor);
        Runtime = CommandRuntime.Create(
            () => Client,
            new Dictionary<string, Profile> { ["extraction"] = new("model-a", .2f) },
            Ledger,
            () => Actor,
            events: Events
        );
        Header = Make("header.extract", "HEADER", DocJson.Default.Header);
        Parties = Make("parties.extract", "PARTIES", DocJson.Default.Parties);
        Lines = Make("lines.extract", "LINES", DocJson.Default.LineItems);
        Totals = Make("totals.extract", "TOTALS", DocJson.Default.Totals);
        Summary = Make("summary.extract", "SUMMARY", DocJson.Default.Summary);
        PascalHeader = Make("header.pascal", "HEADER", PascalJson.Default.Header);
        PascalTotals = Make("totals.pascal", "TOTALS", PascalJson.Default.Totals);
        Descriptor = new(
            "document.extraction",
            1,
            hash,
            DocJson.Default.Doc,
            DocSlots.All,
            validate
        );
        Registry = new([Descriptor]);
        Root = new(Ledger, () => Acting ?? Actor, Store, Registry, null, Events);
    }

    Cmd<DocParams, TR> Make<TR>(
        string id,
        string prefix,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TR> result
    ) =>
        new(
            Runtime,
            new PromptDescriptor<DocParams, TR>(
                id,
                1,
                "extraction",
                "",
                prefix + ":{Text}",
                new string('a', 64),
                id.Replace('.', '_'),
                new Lazy<JsonElement>(() =>
                    JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()
                ),
                result,
                p => prefix + ":" + p.Text,
                _ => "",
                p =>
                    [
                        new ParameterRecord(
                            "Text",
                            "string",
                            JsonSerializer.SerializeToElement(p.Text),
                            null
                        ),
                    ],
                _ => null,
                _ => null
            )
        );

    public Task<CompositionRoot<Doc>> Begin(string key = "k", string revision = "r1") =>
        Root.BeginAsync<Doc>(new(Run, revision, key));

    /// <summary>Registers the four required parts as plain, independent calls.</summary>
    public CompositionRoot<Doc> AddAll(
        CompositionRoot<Doc> root,
        string text = "doc",
        bool skipHeader = false
    )
    {
        if (!skipHeader)
        {
            root.WithPartFrom(DocSlots.Header, Header.WithParameters(new(text)));
        }

        root.WithPartFrom(DocSlots.Parties, Parties.WithParameters(new(text)));
        root.WithPartFrom(DocSlots.Lines, Lines.WithParameters(new(text)));
        root.WithPartFrom(DocSlots.Totals, Totals.WithParameters(new(text)));
        return root;
    }

    public async Task<DraftRecord> DraftAsync(Guid id) =>
        (await RealStore.GetDraftAsync(id, default))!;

    public void Dispose() => Runtime.Dispose();
}

static class Check
{
    /// <summary>The four slots the tests register. The optional summary slot is never registered, so it stays Empty.</summary>
    public static IEnumerable<SlotRecord> Registered(this DraftRecord draft) =>
        draft.Slots.Values.Where(s => s.SlotId != "summary");

    public static T Is<T>(object value)
        where T : class =>
        value as T
        ?? throw new InvalidOperationException(
            $"Expected {typeof(T).Name} but got {value.GetType().Name}: {value}"
        );

    public static CompositeOutcome<Doc>.Published Published(CompositeOutcome<Doc> outcome) =>
        Is<CompositeOutcome<Doc>.Published>(outcome);

    public static bool Throws<T>(Action action)
        where T : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (T)
        {
            return true;
        }
    }

    public static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition not reached");
            }

            await Task.Delay(10);
        }
    }

    public static int Line([System.Runtime.CompilerServices.CallerLineNumber] int line = 0) => line;
}
