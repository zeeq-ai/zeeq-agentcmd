using System.Diagnostics;
using System.Text.Json;
using ExecutionCapture;
using HumanGrading;
using Microsoft.Extensions.AI;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ProfileRouting;

const string source = "LlmCmd.GenAI";
var endpoint = new Uri(Environment.GetEnvironmentVariable("CAPTURE_OTLP") ?? throw new InvalidOperationException("CAPTURE_OTLP missing"));
var resource = ResourceBuilder.CreateDefault().AddService("llmcmd-producer");
using var traces = Sdk.CreateTracerProviderBuilder().SetResourceBuilder(resource).AddSource("ExecutionCapture.Spike", "CommandRoot.Spike", ProfileTelemetry.SourceName, source, HumanLedger.Activity.Name).AddOtlpExporter(o => o.Endpoint = endpoint).Build();
using var metrics = Sdk.CreateMeterProviderBuilder().SetResourceBuilder(resource).AddMeter("ExecutionCapture.Spike", ProfileTelemetry.SourceName, source).AddOtlpExporter(o => o.Endpoint = endpoint).Build();
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var ledger = new HumanLedger();
var actor = new Actor("tenant-demo", "reviewer-demo");
static IResult Reject(string code, string message) => Results.Json(new { code, message }, statusCode: code switch { "not_found" => 404, "conflict" or "value_conflict" or "idempotency_conflict" => 409, _ => 400 });
app.MapGet("/health", () => Results.Ok());
app.MapPost("/profiles/{selected}", async (string selected, bool? failPrimary, CancellationToken ct) => {
    if (selected is not ("openai" or "anthropic")) return Results.BadRequest();
    var catalog = CommandRoot.Profiles.Instance;
    var built = ProfileRouter.TryCreate(new[] {
        new ExecutionProfile(catalog.OpenAI, "openai", "synthetic-openai-model", .2f, new RetryPolicy(1, TimeSpan.Zero, false), catalog.Anthropic),
        new ExecutionProfile(catalog.Anthropic, "anthropic", "synthetic-anthropic-model", .8f)
    }, new Dictionary<string, ProviderRegistration> {
        ["openai"] = new(() => new ChatClientBuilder(new ProfileFakeClient("openai", failPrimary ?? true)).UseOpenTelemetry(sourceName: source, configure: c => c.EnableSensitiveData = false).Build()),
        ["anthropic"] = new(() => new ChatClientBuilder(new ProfileFakeClient("anthropic", false)).UseOpenTelemetry(sourceName: source, configure: c => c.EnableSensitiveData = false).Build())
    });
    if (built is not RouterBuildResult.Ready ready) return Results.Problem("Invalid synthetic profile configuration");
    using var router = ready.Router;
    using var runtime = CommandRoot.CommandRuntime.Create(router, ledger, () => actor);
    var run = ledger.CreateRun(actor);
    var result = await new CommandRoot.ExtractInvoice(runtime).WithParameters(new CommandRoot.InvoiceParameters("synthetic invoice"))
        .WithProfile<CommandRoot.Profiles>(p => selected == "openai" ? p.OpenAI : p.Anthropic).InRun(run).ExecuteAsync(ct);
    traces.ForceFlush(); metrics.ForceFlush();
    if (result is not CommandRoot.Outcome<CommandRoot.Invoice>.Success success || success.Delivery is not CommandRoot.OutputDelivery.Stored stored)
        return Results.Problem("Synthetic routed execution failed");
    return Results.Json(new { success.Value, success.RunId, success.ExecutionId, success.Snapshot, stored.Output.Origin });
});
app.MapPost("/execute/{sensitive:bool}", async (bool sensitive, bool? omitUsage) => {
    var sink = new MemoryCollector();
    using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => new FakeClient(omitUsage == true) }, b => b.UseOpenTelemetry(sourceName: source, configure: c => c.EnableSensitiveData = sensitive), new EmptySnapshot(), sink, new FakeCost(), new Settings("fake", "platform"));
    var result = (ExecutionResult.Success)await executor.ExecuteAsync(new DemoCommand("Ada", 2), new Settings(Model: "fake-request-model"));
    traces.ForceFlush(); metrics.ForceFlush();
    return Results.Json(new { result = result.Capture, collected = sink.Record });
});
app.MapPost("/runs/extract", async () => {
    var sink = new MemoryCollector();
    using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => new FakeClient(false, true) }, b => b.UseOpenTelemetry(sourceName: source, configure: c => c.EnableSensitiveData = false), new EmptySnapshot(), sink, new FakeCost(), new Settings("fake", "platform"));
    var response = (ExecutionResult.Success)await executor.ExecuteAsync(new DemoCommand("Ada", 2), new Settings(Model: "fake-request-model"));
    var capture = response.Capture;
    if (!ActivityContext.TryParse($"00-{capture.TraceId}-{capture.SpanId}-01", null, out var origin)) return Results.Problem("Missing execution trace context");
    using var output = JsonDocument.Parse(response.Response.Text);
    var run = ledger.CreateRun(actor);
    var recorded = ledger.RecordOutput(run, capture.ExecutionId, output.RootElement, "extract.v1", origin, actor);
    traces.ForceFlush(); metrics.ForceFlush();
    return recorded switch {
        LedgerResult<OutputArtifact>.Accepted ok => Results.Json(new { output = ok.Value, execution = capture }),
        LedgerResult<OutputArtifact>.Rejected error => Reject(error.Code, error.Message)
    };
});
app.MapPost("/runs/corrections", (CorrectionRequest request) => {
    var result = ledger.SubmitCorrection(request, actor);
    traces.ForceFlush(); metrics.ForceFlush();
    return result switch {
        LedgerResult<CorrectionReceipt>.Accepted ok => Results.Json(ok.Value),
        LedgerResult<CorrectionReceipt>.Rejected error => Reject(error.Code, error.Message)
    };
});
app.MapPost("/runs/grades", (GradeRequest request) => {
    var result = ledger.RecordGrade(request, actor);
    traces.ForceFlush(); metrics.ForceFlush();
    return result switch {
        LedgerResult<GradeReceipt>.Accepted ok => Results.Json(ok.Value),
        LedgerResult<GradeReceipt>.Rejected error => Reject(error.Code, error.Message)
    };
});
app.Run();
sealed class EmptySnapshot : ISnapshotProvider { public ValueTask<Snapshot?> GetAsync(string id, CancellationToken ct) => ValueTask.FromResult<Snapshot?>(null); }
sealed class MemoryCollector : ICollector { public Capture? Record { get; private set; } public ValueTask CollectAsync(Capture record, CancellationToken ct) { Record = record; return ValueTask.CompletedTask; } }
sealed class FakeClient(bool omitUsage, bool structured = false) : IChatClient {
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, structured ? "{\"items\":[{\"quantity\":2}]}" : "fake response")) { ModelId = "fake-response-model", Usage = omitUsage ? null : new UsageDetails { InputTokenCount = 7, OutputTokenCount = 3 }, ResponseId = "fake-response-id" });
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public object? GetService(Type type, object? key = null) => null; public void Dispose() { }
}
sealed class ProfileFakeClient(string provider, bool fail) : IChatClient {
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        fail ? Task.FromException<ChatResponse>(new HttpRequestException("Synthetic unavailable", null, System.Net.HttpStatusCode.ServiceUnavailable)) :
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"Total\":12,\"Currency\":\"USD\"}")) { ModelId = "synthetic-" + provider + "-response", ResponseId = "synthetic-" + provider + "-id", Usage = new() { InputTokenCount = 7, OutputTokenCount = 3 } });
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public object? GetService(Type type, object? key = null) => null;
    public void Dispose() { }
}
