using System.Text.Json;
using CommandDemo;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Zeeq.AgentCmd;
using Zeeq.AgentCmd.Generated;

if (JsonSerializer.IsReflectionEnabledByDefault)
{
    throw new InvalidOperationException("Reflection serialization must stay disabled");
}

// The host owns the client and actor policy. Generated registration binds the
// authored command to its generated schema and serializer contract.
var actor = new Actor("demo", "developer");
var client = new DemoClient();
var reviewClient = new DemoClient("synthetic-claude");
var services = new ServiceCollection();

// MEAI owns these pipelines. AgentCmd borrows them by key, so disposing the
// runtime cannot close a client still used elsewhere by the host.
services.AddKeyedChatClient("openai", _ => client);
services.AddKeyedChatClient("claude", _ => reviewClient);
services
    .AddAgentCmd(options =>
        options
            .AddChatClient("openai", fixedModel: "synthetic-model")
            .AddChatClient("claude")
            .AddProfile(
                "extraction",
                "openai",
                "synthetic-model",
                new(MaxOutputTokens: 64, ReasoningEffort: ReasoningEffort.None)
            )
            .AddProfile(
                "review",
                "claude",
                "synthetic-claude",
                new(MaxOutputTokens: 512, ReasoningEffort: ReasoningEffort.Low)
            )
            .UseDefaultProfile("extraction")
            .UseActor(_ => actor)
    )
    .AddGeneratedCommands();
using var provider = services.BuildServiceProvider();
var extract = provider.GetRequiredService<ExtractInvoice>();

// Configuring a leaf creates an immutable invocation; only ExecuteAsync sends it.
var invocation = extract
    .WithParameters(new InvoiceParameters(Document: "Invoice for USD 12.50", Locale: "en"))
    .WithProfile("extraction");
var outcome = await invocation.ExecuteAsync();
if (outcome is not Outcome<Invoice>.Success success)
{
    throw new InvalidOperationException("The example command did not produce a valid invoice.");
}

// The prompt and output contract stay the same. A different profile selects
// the other client pipeline and its own model, reasoning and output budget.
var review = await extract
    .WithParameters(new InvoiceParameters("Invoice for USD 12.50"))
    .WithProfile("review")
    .ExecuteAsync();
if (review is not Outcome<Invoice>.Success { Snapshot.Model: "synthetic-claude" })
{
    throw new InvalidOperationException("The review profile did not select its model.");
}

if (
    success.Value.Total != 12.5m
    || success.Value.Currency != "USD"
    || success.Value.Count != 3
    || success.Delivery is not OutputDelivery.Stored
)
{
    throw new InvalidOperationException("Typed execution failed");
}

// Required fields and deserialization share one contract; an optional constructor
// default must not become a required field in the schema sent to the model.
var required = client
    .Schema.GetProperty("required")
    .EnumerateArray()
    .Select(e => e.GetString())
    .ToArray();
if (!required.Contains("Total") || !required.Contains("Currency") || required.Contains("Count"))
{
    throw new InvalidOperationException("Schema disagrees with the record");
}

// The same generated contract also rejects malformed provider output.
client.Response = "{\"Total\":12.5}";
if (
    await extract.WithParameters(new("doc")).ExecuteAsync()
    is not Outcome<Invoice>.Failed { Failure: ExecutionFailure.InvalidOutput }
)
{
    throw new InvalidOperationException("Missing required field was accepted");
}
Console.WriteLine(
    $"Typed result: {success.Value.Total} {success.Value.Currency}; Count={success.Value.Count}"
);
Console.WriteLine(
    "Generated registration, schema, validation and storage passed; reflection serialization disabled."
);

/// <summary>Deterministic MEAI client that exercises the generated contract without network calls.</summary>
sealed class DemoClient(string model = "synthetic-model") : IChatClient
{
    public string Response = "{\"Total\":12.5,\"Currency\":\"USD\"}";
    public JsonElement Schema;

    public object? GetService(Type type, object? key = null) =>
        type == typeof(ChatClientMetadata)
            ? new ChatClientMetadata("synthetic", defaultModelId: model)
            : null;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        Schema = ((ChatResponseFormatJson)options!.ResponseFormat!).Schema!.Value;
        return Task.FromResult(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, Response))
            {
                ModelId = model,
                Usage = new() { InputTokenCount = 7, OutputTokenCount = 3 },
            }
        );
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException();

    public void Dispose() { }
}
