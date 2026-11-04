using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using ExperimentDemo;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Zeeq.LlmCmd;
using Zeeq.LlmCmd.Generated;

if (JsonSerializer.IsReflectionEnabledByDefault)
{
    throw new InvalidOperationException("The example must not use JSON reflection.");
}

var directory = Path.Combine(
    Path.GetTempPath(),
    "llmcmd-experiments-" + Guid.NewGuid().ToString("N")
);
Directory.CreateDirectory(directory);
var path = Path.Combine(directory, "experiments.json");

try
{
    var actor = new Actor("demo", "account-42");
    var original = new SyntheticClient();
    var alternative = new SyntheticClient();
    var results = Channel.CreateUnbounded<ShadowExecutionResult>();
    await using var scheduler = new InMemoryShadowScheduler(capacity: 4, concurrency: 1);

    // The source owns the file format. The cache only controls how often latest is read.
    var cache = new ExperimentSnapshotCache(
        new ExperimentCacheOptions { LatestDefinitionLifetime = TimeSpan.FromMilliseconds(100) }
    );
    var source = new CachedExperimentSnapshotSource(
        "invoice-file",
        new FileExperimentSnapshotSource(path),
        cache
    );
    var resolver = new ExperimentResolver(source, selector: new WeightedExperimentSelector());
    var services = new ServiceCollection();
    services.AddKeyedChatClient("primary", _ => original);
    services.AddKeyedChatClient("alternative", _ => alternative);
    services
        .AddLlmCmd(builder =>
            builder
                .AddChatClient("primary")
                .AddChatClient("alternative")
                .AddProfile("base", "primary", "synthetic-default")
                .UseDefaultProfile("base")
                .UseActor(_ => actor)
                .UseExperiments(sp => new ExperimentRuntimeOptions(
                    resolver,
                    AssignmentUnit: current => current.Subject,
                    RoutePolicy: (_, route) =>
                        route.Provider == "alternative" && route.Model == "synthetic-candidate",
                    Scheduler: scheduler,
                    SelectShadows: (_, _) => ["control", "candidate"],
                    MaxShadows: 1,
                    DefaultConfigurationFingerprint: "demo-default-v1",
                    ShadowScopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
                    // These synthetic pipelines have no tools or external side effects.
                    ShadowPolicy: (_, _) => true,
                    ObserveShadowResult: result => results.Writer.TryWrite(result)
                ))
        )
        .AddGeneratedCommands();

    await using var provider = services.BuildServiceProvider();
    var command = provider.GetRequiredService<ExtractInvoice>();
    var contract = command.ExperimentContract ?? throw new InvalidOperationException();
    var ledger = provider.GetRequiredService<IHumanFeedbackLedger>();

    // Publish control as the primary and compare the candidate in the background.
    await File.WriteAllTextAsync(path, Publication(contract, "r1", 1, 0));
    var first = await RunAsync(command, results, ledger, actor);
    if (first.Primary != "control" || first.Shadow != "candidate")
    {
        throw new InvalidOperationException("The first experiment used the wrong variants.");
    }

    // Publish a new version without rebuilding or restarting the application.
    await File.WriteAllTextAsync(path, Publication(contract, "r2", 0, 1));
    await Task.Delay(TimeSpan.FromMilliseconds(250));
    var second = await RunAsync(command, results, ledger, actor);
    if (
        second.Primary != "candidate"
        || second.Shadow != "control"
        || second.Prompt != "Compare invoice 42 in fr."
        || second.Revision != "r2"
        || !alternative.Calls.Any(c => c.Model == "synthetic-candidate" && c.Temperature == .2f)
    )
    {
        throw new InvalidOperationException("The updated experiment was not applied.");
    }

    await scheduler.CompleteAsync();
    Console.WriteLine($"First publication: primary={first.Primary}, shadow={first.Shadow}");
    Console.WriteLine($"Second publication: primary={second.Primary}, shadow={second.Shadow}");
    Console.WriteLine("Both shadow outputs were read by their exact stored references.");
}
finally
{
    Directory.Delete(directory, recursive: true);
}

static async Task<(string? Primary, string Shadow, string? Prompt, string? Revision)> RunAsync(
    ExtractInvoice command,
    Channel<ShadowExecutionResult> results,
    IHumanFeedbackLedger ledger,
    Actor actor
)
{
    var outcome = await command.WithParameters(new InvoiceInput("invoice 42")).ExecuteAsync();
    if (outcome is not Outcome<InvoiceResult>.Success { Delivery: OutputDelivery.Stored } primary)
    {
        throw new InvalidOperationException("The primary output was not stored.");
    }

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var shadow = await results.Reader.ReadAsync(timeout.Token);
    if (
        shadow.Disposition != ShadowDisposition.Completed
        || shadow.Output is null
        || shadow.ComparisonGroupId != primary.Snapshot.Experiment?.ComparisonGroupId
        || ledger.ReadOutput(shadow.Output, actor)
            is not LedgerResult<OutputArtifact>.Accepted stored
        || stored.Value.Evidence is not { Role: ExperimentRole.Shadow }
    )
    {
        throw new InvalidOperationException("The shadow output was not stored with its evidence.");
    }

    return (
        primary.Snapshot.Experiment?.VariantId,
        shadow.VariantId,
        primary.Snapshot.FormattedPrompt,
        primary.Snapshot.Experiment?.Revision
    );
}

static string Publication(
    ExperimentCommandContract contract,
    string revision,
    int control,
    int candidate
) =>
    $$"""
        {
          "format": 1,
          "partitions": {
            "demo": {
              "revision": "{{revision}}",
              "commands": {
                "{{contract.CommandId}}": {
                  "commandRevision": {{contract.CommandRevision}},
                  "contractSignature": "{{contract.ContractSignature}}",
                  "experimentId": "invoice-prompt",
                  "controlVariantId": "control",
                  "variants": {
                    "control": {
                      "weight": {{control}},
                      "prompt": {
                        "system": { "mode": "inherit" },
                        "user": { "mode": "inherit" }
                      }
                    },
                    "candidate": {
                      "weight": {{candidate}},
                      "prompt": {
                        "system": { "mode": "inherit" },
                        "user": { "mode": "replaced", "text": "Compare {Document} in {Locale}." }
                      },
                      "parameters": { "Locale": "fr" },
                      "route": {
                        "key": "candidate-route",
                        "provider": "alternative",
                        "model": "synthetic-candidate",
                        "temperature": 0.2
                      }
                    }
                  }
                }
              }
            }
          }
        }
        """;

/// <summary>A deterministic client so the experiment can run without credentials or network calls.</summary>
sealed class SyntheticClient : IChatClient
{
    public ConcurrentQueue<(string? Model, float? Temperature)> Calls { get; } = new();

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        Calls.Enqueue((options?.ModelId, options?.Temperature));
        return Task.FromResult(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"Value\":\"ok\"}"))
        );
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
