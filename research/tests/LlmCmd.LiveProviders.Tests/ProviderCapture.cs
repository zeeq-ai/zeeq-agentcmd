using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Anthropic;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using ProviderAdapters;
using ProviderAdapters.Platforms;

namespace LlmCmd.LiveProviders.Tests;

internal sealed record CaptureRun(ChatResponse Response, ResponseSnapshot Snapshot,
    string NativeType, IReadOnlyList<string> ObserverFailures);

internal static class ProviderCapture
{
    internal static async Task<CaptureRun> RunAsync(LiveSettings settings, HttpMessageHandler? transport = null)
    {
        // One short, non-streaming request, with both SDK retry loops disabled.
        // Retrying a paid request is an explicit decision, never a test-runner
        // retry hidden from the per-attempt capture.
        using var http = new HttpClient(transport ?? new SocketsHttpHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(45) };
        IChatClient provider = settings.Provider switch
        {
            "openai" => new ChatClient(settings.Model, new ApiKeyCredential(settings.ApiKey), new OpenAIClientOptions
            {
                Transport = new HttpClientPipelineTransport(http),
                RetryPolicy = new ClientRetryPolicy(0)
            }).AsIChatClient(),
            "anthropic" => new AnthropicClient
            {
                ApiKey = settings.ApiKey,
                HttpClient = http,
                BaseUrl = "https://api.anthropic.com",
                MaxRetries = 0,
                Timeout = TimeSpan.FromSeconds(45)
            }.AsIChatClient(settings.Model, defaultMaxOutputTokens: 32),
            _ => throw new ArgumentException("Unknown live-test provider")
        };

        var failures = new List<string>();
        var extractor = new ProviderExtractor(new ProviderAdapterRegistry(
            [new OpenAiChatAdapter(), new AnthropicMessagesAdapter()],
            [new OpenAiSdkProjector(), new AnthropicSdkProjector()]),
            new ProviderExtractionOptions { OnFailure = (stage, _) => failures.Add(stage) });
        var sink = new RecordingSink();
        var registration = new ProviderRegistration(settings.Provider, "live-tests." + settings.Provider,
            settings.Provider == "openai" ? ApiKind.ChatCompletions : ApiKind.Messages);
        using var captured = new ProviderExtractionChatClient(provider, extractor, registration, sink);
        var response = await captured.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "Reply with OK.")],
            new ChatOptions { MaxOutputTokens = 32 });
        if (sink.Results.Count != 1)
            throw new InvalidOperationException("Expected exactly one captured provider attempt");
        return new(response, sink.Results[0].Snapshot, response.RawRepresentation?.GetType().FullName ?? "missing", failures);
    }

    internal static async Task RunLiveAsync(string provider)
    {
        var settings = LiveSettings.Load(provider);
        CaptureRun run;
        try { run = await RunAsync(settings); }
        catch (Exception error)
        {
            // SDK exceptions can include response bodies. Keep those out of
            // JUnit and terminal logs, including inner-exception formatting.
            throw new InvalidOperationException($"{provider} live request failed ({error.GetType().Name}). Check credentials, model access and quota.");
        }
        var directory = Path.GetFullPath(Environment.GetEnvironmentVariable("LLMCMD_LIVE_EVIDENCE")
            ?? Path.Combine("artifacts", "live-providers"));
        Directory.CreateDirectory(directory);
        var evidence = new
        {
            Live = true,
            CapturedAt = DateTimeOffset.UtcNow,
            RequestedModel = settings.Model,
            NativeType = run.NativeType,
            Snapshot = run.Snapshot,
            ObserverFailures = run.ObserverFailures
        };
        await File.WriteAllTextAsync(Path.Combine(directory, provider + ".capture.json"),
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        await CaptureAssertions.CheckAsync(run);
    }

    private sealed class RecordingSink : IExtractionSink
    {
        internal List<ExtractionResult> Results { get; } = [];
        public ValueTask OnExtractedAsync(ExtractionResult result, CancellationToken cancellationToken)
        { Results.Add(result); return ValueTask.CompletedTask; }
    }
}
