using System.Text.Json;
using Microsoft.Extensions.AI;
using ExecutionCapture;

var collector = new FakeCollector();
using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => new FakeClient() }, null, new FakeSnapshot(), collector, new FakeCost(), new Settings("fake", "example-model"));
var result = await executor.ExecuteAsync(new DemoCommand("Ada", 2));
var json = JsonSerializer.Serialize(result, result.GetType(), new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(json);
Directory.CreateDirectory("/tmp/opencode/llmcmd-runtime");
if (result is ExecutionResult.Success success)
    File.WriteAllText("/tmp/opencode/llmcmd-runtime/sample-record.json", JsonSerializer.Serialize(success.Capture, new JsonSerializerOptions { WriteIndented = true }));

sealed class FakeSnapshot : ISnapshotProvider { public ValueTask<Snapshot?> GetAsync(string id, CancellationToken ct) => ValueTask.FromResult<Snapshot?>(null); }
sealed class FakeCollector : ICollector { public ValueTask CollectAsync(Capture capture, CancellationToken ct) => ValueTask.CompletedTask; }
sealed class FakeClient : IChatClient {
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Example response")) { ModelId = options?.ModelId, Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 10 } });
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
