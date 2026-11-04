using System.Text.Json;
using Microsoft.Extensions.AI;
using Zeeq.AgentCmd.Providers;
using Zeeq.AgentCmd.Providers.Platforms;

namespace AgentCmd.LiveProviders.Tests;

internal sealed record CaptureRun(
    ChatResponse Response,
    ResponseSnapshot Snapshot,
    string NativeType,
    IReadOnlyList<string> ObserverFailures
)
{
    internal DateTimeOffset StartedAt { get; init; }
    internal DateTimeOffset EndedAt { get; init; }
    internal int Requests { get; init; }
}

internal static class ProviderCapture
{
    internal static async Task<CaptureRun> RunAsync(
        LiveSettings settings,
        HttpMessageHandler? transport = null
    )
    {
        // One short, non-streaming request, with both SDK retry loops disabled.
        // Retrying a paid request is an explicit decision, never a test-runner
        // retry hidden from the per-attempt capture.
        using var counter = new RequestCounter(
            transport ?? new SocketsHttpHandler { AllowAutoRedirect = false }
        );
        using var http = new HttpClient(counter) { Timeout = TimeSpan.FromSeconds(45) };
        var provider = LiveProviderFactory.Create(settings, http);

        var failures = new List<string>();
        var extractor = new ProviderExtractor(
            new ProviderAdapterRegistry(
                [
                    new OpenAiChatAdapter(),
                    new OpenAiChatAdapter("azureopenai"),
                    new AnthropicMessagesAdapter(),
                ],
                [new OpenAiSdkProjector(), new AnthropicSdkProjector()]
            ),
            new ProviderExtractionOptions { OnFailure = (stage, _) => failures.Add(stage) }
        );
        var sink = new RecordingSink();
        var registration = new ProviderIdentity(
            settings.Provider,
            "live-tests." + settings.Provider,
            settings.Provider is "openai" or "azureopenai"
                ? ApiKind.ChatCompletions
                : ApiKind.Messages
        );
        using var captured = new ProviderExtractionChatClient(
            provider,
            extractor,
            registration,
            sink
        );
        var started = DateTimeOffset.UtcNow;
        ChatResponse response;
        try
        {
            response = await captured.GetResponseAsync(
                [new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "Reply with OK.")],
                new ChatOptions
                {
                    MaxOutputTokens =
                        LiveProviderFactory.MinimumEffort(settings) == ReasoningEffort.None
                            ? 32
                            : LiveProviderFactory.OutputBudget(settings),
                    Reasoning = new ReasoningOptions
                    {
                        Effort = LiveProviderFactory.MinimumEffort(settings),
                    },
                }
            );
        }
        catch (Exception error)
        {
            // Status and attempt count are safe diagnostics. SDK messages,
            // response bodies and headers never cross this boundary.
            throw new InvalidOperationException(
                $"Provider request failed: {error.GetType().Name}; HTTP status {counter.Status}; requests {counter.Calls}."
            );
        }
        if (sink.Results.Count != 1)
        {
            throw new InvalidOperationException("Expected exactly one captured provider attempt");
        }

        return new(
            response,
            sink.Results[0].Snapshot,
            response.RawRepresentation?.GetType().FullName ?? "missing",
            failures
        )
        {
            StartedAt = started,
            EndedAt = DateTimeOffset.UtcNow,
            Requests = counter.Calls,
        };
    }

    internal static async Task RunLiveAsync(string provider)
    {
        var settings = LiveSettings.Load(provider);
        var destination = EvidencePaths.BeginRun(provider + ".capture.json");
        var run = await RunAsync(settings);
        await CaptureAssertions.CheckAsync(run);
        var directory = EvidencePaths.Directory;
        Directory.CreateDirectory(directory);
        var evidence = new
        {
            Live = true,
            CapturedAt = DateTimeOffset.UtcNow,
            RequestedModel = settings.Model,
            NativeType = run.NativeType,
            StartedAt = run.StartedAt,
            EndedAt = run.EndedAt,
            Requests = run.Requests,
            Snapshot = run.Snapshot,
            ObserverFailures = run.ObserverFailures,
        };
        await File.WriteAllTextAsync(
            destination,
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true })
        );
    }

    private sealed class RecordingSink : IExtractionSink
    {
        internal List<ExtractionResult> Results { get; } = [];

        public ValueTask OnExtractedAsync(
            ExtractionResult result,
            CancellationToken cancellationToken
        )
        {
            Results.Add(result);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RequestCounter(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        internal int Calls { get; private set; }
        internal int? Status { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Calls++;
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            Status = (int)response.StatusCode;
            return response;
        }
    }
}
