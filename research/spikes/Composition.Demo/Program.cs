using System.Text.Json;
using CommandRoot;
using CompositionDemo;
using HumanGrading;
using LlmCmd.Generated;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

if (JsonSerializer.IsReflectionEnabledByDefault) throw new InvalidOperationException("Reflection serialization must stay disabled.");
var actor = new Actor("demo", "reviewer");
var ledger = new HumanLedger();
var runId = ledger.CreateRun(actor);
var client = new DemoClient();
using var runtime = CommandRuntime.Create(() => client,
    new Dictionary<string,Profile> { ["extract"] = new("synthetic-model") }, ledger, () => actor);
var services = new ServiceCollection();
services.AddLlmCommandsFromComposition_Demo(runtime);
using var provider = services.BuildServiceProvider();
var roots = provider.GetRequiredService<IRootCommands>();
var header = provider.GetRequiredService<ExtractHeader>();
var parties = provider.GetRequiredService<ExtractParties>();
var lines = provider.GetRequiredService<ExtractLineItems>();
var quickLines = provider.GetRequiredService<ExtractQuickLineItems>();
var totals = provider.GetRequiredService<ExtractTotals>();
var text = new SourceText("example invoice");
var root = await roots.BeginAsync<DocumentExtraction>(new CompositionRequest(runId, "input-v1", "demo"));

// Registration is deferred and returns the same bounded handle. No model call
// runs until ExecuteAsync has checked the complete plan and its dependencies.
root.WithHeaderFrom(header.WithParameters(text))
    .WithPartiesFrom(parties, text);
var detailed = !args.Contains("--quick", StringComparer.Ordinal);
if (detailed) root.WithLineItemsFrom(lines.WithParameters(text));
else root.WithLineItemsFrom(quickLines.WithParameters(text));

// Then receives the exact stored prerequisite, not a mutable in-memory guess.
var callbacks = 0;
root.WithTotalsFrom(root.Steps.LineItems.Then(pinned =>
{
    callbacks++;
    return totals.WithParameters(new TotalsParameters(text.Document, pinned.Value.Count));
}));
if (client.Calls != 0) throw new InvalidOperationException("Registration called the model.");
var outcome = await root.ExecuteAsync();
if (outcome is not CompositeOutcome<DocumentExtraction>.Published published)
    throw new InvalidOperationException($"Composition did not publish: {outcome.GetType().Name}");
if (published.Value.Header.Number != "INV-1" || published.Value.LineItems.Count != 2 || published.Value.Totals.Amount != 12.50m)
    throw new InvalidOperationException("Aggregate differs from its stored parts.");
if (published.Members.Count != 4 || published.Members.Single(x => x.SlotId == "Header").JsonName != "header_data")
    throw new InvalidOperationException("The manifest lost a part or its JSON name.");
var caller = published.Steps.Single(x => x.SlotId == "Header").Caller;
if (caller is null || caller.File != __CallerFile() || caller.Line <= 0 || string.IsNullOrEmpty(caller.Member))
    throw new InvalidOperationException("The deferred call lost its application caller.");
var repeated = await root.ExecuteAsync();
if (repeated is not CompositeOutcome<DocumentExtraction>.Published again || again.Aggregate != published.Aggregate || client.Calls != 4 || callbacks != 1)
    throw new InvalidOperationException("Repeating a handle changed its result or ran work twice.");
// The assembled output has its own producer identity. Grade that exact stored
// version and field; no component execution is misidentified as the producer.
var outputRef = published.Aggregate.Output;
if (outputRef.Producer is not ProducerRef.Composition producer || producer.Id != published.DraftId || outputRef.ExecutionId is not null)
    throw new InvalidOperationException("The aggregate has the wrong producer identity.");
using var expected = JsonDocument.Parse("12.50");
var gradeRequest = new GradeRequest(Guid.NewGuid(), outputRef, "/Totals/Amount", expected.RootElement, 1.0, "Matches the invoice");
var grade = ledger.RecordGrade(gradeRequest, actor);
var gradeReplay = ledger.RecordGrade(gradeRequest, actor);
if (grade is not LedgerResult<GradeReceipt>.Accepted accepted || accepted.Replayed || accepted.Value.Event.Target != outputRef ||
    accepted.Value.Event.Pointer != "/Totals/Amount" || accepted.Value.Event.ObservedValue.GetDecimal() != 12.50m ||
    gradeReplay is not LedgerResult<GradeReceipt>.Accepted replay || !replay.Replayed || replay.Value.Event.EventId != gradeRequest.EventId)
    throw new InvalidOperationException("Grading did not preserve the aggregate reference or idempotency.");
Console.WriteLine($"Published {published.Value.Header.Number}; {published.Value.LineItems.Count} lines; total {published.Value.Totals.Amount}.");
Console.WriteLine("Generated registration, deferred branch, pinned dependency and aggregate grading passed; reflection serialization disabled.");

static string __CallerFile([System.Runtime.CompilerServices.CallerFilePath] string file="") => file;

sealed class DemoClient : IChatClient
{
    public int Calls;
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options=null, CancellationToken cancellationToken=default)
    {
        Interlocked.Increment(ref Calls);
        var prompt=messages.Single(m=>m.Role==ChatRole.User).Text ?? "";
        string body=prompt.StartsWith("HEADER ",StringComparison.Ordinal) ? """{"Number":"INV-1"}"""
            : prompt.StartsWith("PARTIES ",StringComparison.Ordinal) ? """{"Seller":"Example Co"}"""
            : prompt.StartsWith("LINES ",StringComparison.Ordinal) || prompt.StartsWith("QUICK ",StringComparison.Ordinal) ? """{"Count":2}"""
            : prompt.StartsWith("TOTALS ",StringComparison.Ordinal) && prompt.EndsWith(" 2",StringComparison.Ordinal) ? """{"Amount":12.50}"""
            : throw new InvalidOperationException("Unexpected fake prompt");
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,body)) { ModelId="synthetic-model", Usage=new() { InputTokenCount=5, OutputTokenCount=3 } });
    }
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    public object? GetService(Type type,object? key=null)=>null;
    public void Dispose() { }
}
