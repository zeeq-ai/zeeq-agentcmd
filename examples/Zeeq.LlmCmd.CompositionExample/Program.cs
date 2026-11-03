using System.Text.Json;
using CompositionDemo;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Zeeq.LlmCmd;
using Zeeq.LlmCmd.Generated;

if (JsonSerializer.IsReflectionEnabledByDefault)
{
    throw new InvalidOperationException("Reflection serialization must stay disabled.");
}

// One host supplies the client and authority for leaves and the composition.
// The generated catalog registers both typed commands and the whole descriptor.
var actor = new Actor("demo", "reviewer");
var client = new DemoClient();
var services = new ServiceCollection()
    .AddLlmCmd(options =>
        options
            .UseChatClient(_ => client)
            .AddProfile("extract", "synthetic-model")
            .UseActor(_ => actor)
    )
    .AddGeneratedCommands();
using var provider = services.BuildServiceProvider();
var ledger = provider.GetRequiredService<IHumanFeedbackLedger>();
var runId = ledger.CreateRun(actor);
var roots = provider.GetRequiredService<IRootCommands>();
var header = provider.GetRequiredService<ExtractHeader>();
var parties = provider.GetRequiredService<ExtractParties>();
var lines = provider.GetRequiredService<ExtractLineItems>();
var quickLines = provider.GetRequiredService<ExtractQuickLineItems>();
var totals = provider.GetRequiredService<ExtractTotals>();
var text = new SourceText("example invoice");

// The run, input revision and key identify the draft for idempotent replay.
var root = await roots.BeginAsync<DocumentExtraction>(
    new CompositionRequest(runId, "input-v1", "demo")
);

// Registration is deferred and returns the same bounded handle. No model call
// runs until ExecuteAsync has checked the complete plan and its dependencies.
root.WithHeaderFrom(header.WithParameters(text)).WithPartiesFrom(parties, text);
var detailed = !args.Contains("--quick", StringComparer.Ordinal);
if (detailed)
{
    root.WithLineItemsFrom(lines.WithParameters(text));
}
else
{
    root.WithLineItemsFrom(quickLines.WithParameters(text));
}

// Then receives the exact stored prerequisite, not a mutable in-memory guess.
var callbacks = 0;
root.WithTotalsFrom(
    root.Steps.LineItems.Then(pinned =>
    {
        callbacks++;
        return totals.WithParameters(new TotalsParameters(text.Document, pinned.Value.Count));
    })
);
if (client.Calls != 0)
{
    throw new InvalidOperationException("Registration called the model.");
}

// Execution freezes the plan and checks its dependencies before claiming work.
var outcome = await root.ExecuteAsync();
if (outcome is not CompositeOutcome<DocumentExtraction>.Published published)
{
    throw new InvalidOperationException($"Composition did not publish: {outcome.GetType().Name}");
}
if (
    published.Value.Header.Number != "INV-1"
    || published.Value.LineItems.Count != 2
    || published.Value.Totals.Amount != 12.50m
)
{
    throw new InvalidOperationException("Aggregate differs from its stored parts.");
}
if (
    published.Members.Count != 4
    || published.Members.Single(x => x.SlotId == "Header").JsonName != "header_data"
)
{
    throw new InvalidOperationException("The manifest lost a part or its JSON name.");
}
var caller = published.Steps.Single(x => x.SlotId == "Header").Caller;
if (
    caller is null
    || caller.File != __CallerFile()
    || caller.Line <= 0
    || string.IsNullOrEmpty(caller.Member)
)
{
    throw new InvalidOperationException("The deferred call lost its application caller.");
}
var repeated = await root.ExecuteAsync();
if (
    repeated is not CompositeOutcome<DocumentExtraction>.Published again
    || again.Aggregate != published.Aggregate
    || client.Calls != 4
    || callbacks != 1
)
{
    throw new InvalidOperationException("Repeating a handle changed its result or ran work twice.");
}

// The assembled output has its own producer identity. Grade that exact stored
// version; no component execution is misidentified as the producer.
var outputRef = published.Aggregate.Output;
if (
    outputRef.Producer is not ProducerRef.Composition producer
    || producer.Id != published.DraftId
    || outputRef.ExecutionId is not null
)
{
    throw new InvalidOperationException("The aggregate has the wrong producer identity.");
}

// Read the exact published version through the ledger. Evaluation does not
// depend on Published.Value, the in-memory parts, or the last leaf execution.
if (ledger.ReadOutput(outputRef, actor) is not LedgerResult<OutputArtifact>.Accepted stored)
{
    throw new InvalidOperationException("The aggregate could not be read.");
}

var descriptor =
    provider.GetRequiredService<CompositionRegistry>().Find<DocumentExtraction>()
    ?? throw new InvalidOperationException("The aggregate contract is not registered.");
var whole =
    stored.Value.Value.Deserialize(descriptor.Whole)
    ?? throw new InvalidOperationException("The stored aggregate could not be decoded.");
if (
    whole.Header.Number != "INV-1"
    || whole.Parties.Seller != "Example Co"
    || whole.LineItems.Count != 2
    || whole.Totals.Amount != 12.50m
)
{
    throw new InvalidOperationException("The stored whole failed evaluation.");
}

// Grade only the whole and replay the same event to verify idempotency.
var gradeRequest = new GradeRequest(
    Guid.NewGuid(),
    outputRef,
    "",
    stored.Value.Value,
    1.0,
    "The stored invoice satisfies its cross-part checks"
);
var grade = ledger.RecordGrade(gradeRequest, actor);
var gradeReplay = ledger.RecordGrade(gradeRequest, actor);
if (
    grade is not LedgerResult<GradeReceipt>.Accepted accepted
    || accepted.Replayed
    || accepted.Value.Event.Target != outputRef
    || accepted.Value.Event.Pointer != ""
    || !JsonElement.DeepEquals(accepted.Value.Event.ObservedValue, stored.Value.Value)
    || gradeReplay is not LedgerResult<GradeReceipt>.Accepted replay
    || !replay.Replayed
    || replay.Value.Event.EventId != gradeRequest.EventId
    || client.Calls != 4
)
{
    throw new InvalidOperationException(
        "Grading did not preserve the aggregate reference or idempotency."
    );
}
Console.WriteLine(
    $"Published {published.Value.Header.Number}; {published.Value.LineItems.Count} lines; total {published.Value.Totals.Amount}."
);
Console.WriteLine(
    "Generated registration, deferred branch, pinned dependency and aggregate grading passed; reflection serialization disabled."
);

static string __CallerFile([System.Runtime.CompilerServices.CallerFilePath] string file = "") =>
    file;

/// <summary>Returns deterministic parts without a provider so the composition workflow runs offline.</summary>
sealed class DemoClient : IChatClient
{
    public int Calls;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        Interlocked.Increment(ref Calls);
        var prompt = messages.Single(m => m.Role == ChatRole.User).Text ?? "";
        // Prompt prefixes select the expected part. The totals response also
        // requires the pinned line count, so a missing dependency cannot pass.
        string body;
        if (prompt.StartsWith("HEADER ", StringComparison.Ordinal))
        {
            body = """{"Number":"INV-1"}""";
        }
        else if (prompt.StartsWith("PARTIES ", StringComparison.Ordinal))
        {
            body = """{"Seller":"Example Co"}""";
        }
        else if (
            prompt.StartsWith("LINES ", StringComparison.Ordinal)
            || prompt.StartsWith("QUICK ", StringComparison.Ordinal)
        )
        {
            body = """{"Count":2}""";
        }
        else if (
            prompt.StartsWith("TOTALS ", StringComparison.Ordinal)
            && prompt.EndsWith(" 2", StringComparison.Ordinal)
        )
        {
            body = """{"Amount":12.50}""";
        }
        else
        {
            throw new InvalidOperationException("Unexpected fake prompt");
        }

        return Task.FromResult(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, body))
            {
                ModelId = "synthetic-model",
                Usage = new() { InputTokenCount = 5, OutputTokenCount = 3 },
            }
        );
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException();

    public object? GetService(Type type, object? key = null) => null;

    public void Dispose() { }
}
