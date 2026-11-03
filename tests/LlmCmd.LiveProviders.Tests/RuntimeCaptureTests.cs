using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.LlmCmd;
using Zeeq.LlmCmd.Providers;
using Zeeq.LlmCmd.Providers.Platforms;

namespace LlmCmd.LiveProviders.Tests;

public sealed record RuntimeProbeInput(string Document);

public sealed record RuntimeProbeResult(string Answer);

public sealed partial class RuntimeProbe : PromptCommand<RuntimeProbeInput, RuntimeProbeResult>
{
    const string Id = "live.runtime.probe";
    const string SystemTemplate = "Return the requested JSON.";
    const string UserTemplate = "Return Answer as OK for {Document}.";
}

public class RuntimeCaptureTests
{
    [Test, Arguments("azureopenai"), Arguments("openai"), Arguments("anthropic")]
    public async Task Generated_command_runs_through_actual_sdk_and_stores_output(string provider)
    {
        using var handler = new RuntimeTransport(provider);
        using var http = new HttpClient(handler);
        var settings = new LiveSettings(
            provider,
            "synthetic-key",
            "synthetic-model",
            new Uri("https://synthetic.example/openai/v1/"),
            modelId: provider == "anthropic" ? "claude-haiku-5-5" : "gpt-6-luna"
        );
        var result = await ExecuteAsync(settings, http, throwFromListener: true);
        await Assert.That(result.Success.Value.Answer).IsEqualTo("OK");
        await Assert.That(result.Success.Delivery is OutputDelivery.Stored).IsTrue();
        var stored = ((OutputDelivery.Stored)result.Success.Delivery).Output;
        await Assert.That(stored.Value.GetProperty("Answer").GetString()).IsEqualTo("OK");
        await Assert.That(stored.Reference.ExecutionId).IsEqualTo(result.Success.ExecutionId);
        await Assert.That(result.ReadBackStored).IsTrue();
        await Assert
            .That(result.Events.Any(e => e.Kind == "attempt.started" && e.AttemptId != null))
            .IsTrue();
        await Assert
            .That(result.Events.Any(e => e.Kind == "attempt.finished" && e.Outcome == "success"))
            .IsTrue();
        await Assert
            .That(result.Events.All(e => e.Context.ExecutionId == result.Success.ExecutionId))
            .IsTrue();
        await Assert
            .That(result.Events.Single(e => e.Kind == "attempt.started").AttemptId)
            .IsEqualTo(result.Events.Single(e => e.Kind == "attempt.finished").AttemptId);
        await Assert.That(result.Snapshot.RawContent is null).IsTrue();
        var artifact = JsonSerializer.Serialize(result.Snapshot);
        await Assert.That(artifact.Contains("synthetic-key", StringComparison.Ordinal)).IsFalse();
        await Assert
            .That(artifact.Contains("synthetic-private-document", StringComparison.Ordinal))
            .IsFalse();
        await Assert.That(artifact.Contains("\"Answer\"", StringComparison.Ordinal)).IsFalse();
        await Assert.That(handler.ValidRequest).IsTrue();
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    internal sealed record RuntimeRun(
        Outcome<RuntimeProbeResult>.Success Success,
        ResponseSnapshot Snapshot,
        IReadOnlyList<ExecutionEvent> Events,
        bool ReadBackStored
    );

    internal static async Task<RuntimeRun> ExecuteAsync(
        LiveSettings settings,
        HttpClient http,
        bool throwFromListener = false
    )
    {
        var snapshots = new CaptureSink();
        var extractor = new ProviderExtractor(
            new ProviderAdapterRegistry(
                [
                    new OpenAiChatAdapter(),
                    new OpenAiChatAdapter("azureopenai"),
                    new AnthropicMessagesAdapter(),
                ],
                [new OpenAiSdkProjector(), new AnthropicSdkProjector()]
            )
        );
        var identity = new ProviderIdentity(
            settings.Provider,
            "live-tests." + settings.Provider,
            settings.Provider == "anthropic" ? ApiKind.Messages : ApiKind.ChatCompletions
        );
        var provider = new ProviderExtractionChatClient(
            LiveProviderFactory.Create(settings, http),
            extractor,
            identity,
            snapshots
        );
        var events = new EventSink(throwFromListener);
        var actor = new Actor("live-tests", "runtime-probe");
        var ledger = new InMemoryFeedbackLedger();
        using var runtime = CommandRuntime.Create(
            () => provider,
            new Dictionary<string, Profile> { ["probe"] = new(settings.Model) },
            ledger,
            () => actor,
            events: events
        );
        var outcome = await new RuntimeProbe(runtime)
            .WithParameters(new("synthetic-private-document"))
            .ExecuteAsync();
        if (outcome is Outcome<RuntimeProbeResult>.Failed failed)
        {
            throw new InvalidOperationException("Generated command failed: " + failed.Failure.Kind);
        }

        if (
            outcome is not Outcome<RuntimeProbeResult>.Success success
            || snapshots.Items.Count != 1
        )
        {
            throw new InvalidOperationException(
                "Runtime probe did not complete with one provider snapshot."
            );
        }

        var readBack =
            success.Delivery is OutputDelivery.Stored stored
            && ledger.ReadOutput(stored.Output.Reference, actor)
                is LedgerResult<OutputArtifact>.Accepted;
        return new(success, snapshots.Items[0], events.Items, readBack);
    }

    private sealed class CaptureSink : IExtractionSink
    {
        internal List<ResponseSnapshot> Items { get; } = [];

        public ValueTask OnExtractedAsync(
            ExtractionResult result,
            CancellationToken cancellationToken
        )
        {
            Items.Add(result.Snapshot);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EventSink(bool fail) : IExecutionEventSink
    {
        internal List<ExecutionEvent> Items { get; } = [];

        public ValueTask EmitAsync(LlmEvent record, CancellationToken cancellationToken)
        {
            if (record is ExecutionEvent e)
            {
                Items.Add(e);
            }

            if (fail)
            {
                throw new InvalidOperationException("synthetic listener fault");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class RuntimeTransport(string provider) : HttpMessageHandler
    {
        internal bool ValidRequest { get; private set; }
        internal int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Calls++;
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            var outputLimit = body[provider == "anthropic" ? "max_tokens" : "max_completion_tokens"]
                ?.GetValue<int>();
            ValidRequest =
                body["model"]?.GetValue<string>() == "synthetic-model"
                && outputLimit is > 0 and <= 64
                && (
                    provider == "anthropic"
                        ? body["thinking"]?["type"]?.GetValue<string>() == "disabled"
                        : body["reasoning_effort"]?.GetValue<string>() == "none"
                )
                && (
                    provider == "anthropic"
                        ? body["output_config"]?["format"]?["schema"]?["properties"]?["Answer"]
                            is not null
                        : body["response_format"]
                            ?["json_schema"]
                            ?["schema"]
                            ?["properties"]
                            ?["Answer"]
                            is not null
                );
            var response = provider == "anthropic" ? AnthropicResponse : OpenAiResponse;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }

    private const string OpenAiResponse = """
        {"id":"chatcmpl-test","object":"chat.completion","created":1700000000,"model":"synthetic-model","choices":[{"index":0,"message":{"role":"assistant","content":"{\"Answer\":\"OK\"}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":3,"total_tokens":13}}
        """;
    private const string AnthropicResponse = """
        {"id":"msg_test","type":"message","role":"assistant","model":"synthetic-model","content":[{"type":"text","text":"{\"Answer\":\"OK\"}"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":3}}
        """;
}

// Explicit selection and the independent LiveSettings opt-in both remain required.
[Explicit, Category("Live")]
public class AzureRuntimeCaptureTests
{
    [Test]
    public Task Captures_generated_command() => LiveRuntimeProof.RunAsync("azureopenai");
}

[Explicit, Category("Live")]
public class AnthropicRuntimeCaptureTests
{
    [Test]
    public Task Captures_generated_command() => LiveRuntimeProof.RunAsync("anthropic");
}

internal static class LiveRuntimeProof
{
    internal static async Task RunAsync(string provider)
    {
        var settings = LiveSettings.Load(provider);
        var destination = EvidencePaths.BeginRun(provider + ".runtime.capture.json");
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(45),
        };
        RuntimeCaptureTests.RuntimeRun run;
        var started = DateTimeOffset.UtcNow;
        try
        {
            run = await RuntimeCaptureTests.ExecuteAsync(settings, http);
        }
        catch (Exception error)
        {
            // Provider failures can carry headers or response content.
            throw new InvalidOperationException(
                $"{provider} generated runtime request failed ({error.GetType().Name})."
            );
        }
        if (
            run.Success.Value.Answer != "OK"
            || !run.ReadBackStored
            || run.Success.Delivery is not OutputDelivery.Stored
            || run.Snapshot.End != AttemptEnd.Completed
            || run.Snapshot.RawContent is not null
            || !run.Events.Any(e => e.Kind == "attempt.finished" && e.Outcome == "success")
        )
        {
            throw new InvalidOperationException(
                "Generated runtime proof did not meet capture requirements."
            );
        }

        await CaptureAssertions.CheckSnapshotAsync(run.Snapshot);
        var directory = EvidencePaths.Directory;
        Directory.CreateDirectory(directory);
        var evidence = new
        {
            Live = true,
            StartedAt = started,
            EndedAt = DateTimeOffset.UtcNow,
            RequestedModel = settings.Model,
            ExecutionId = run.Success.ExecutionId,
            Snapshot = run.Snapshot,
            Events = run.Events.Select(e => new
            {
                e.Kind,
                e.AttemptId,
                e.AttemptNumber,
                e.Outcome,
            }),
        };
        await File.WriteAllTextAsync(
            destination,
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true })
        );
    }
}
