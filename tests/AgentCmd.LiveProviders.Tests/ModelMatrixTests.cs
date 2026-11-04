using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd.Providers;
using Zeeq.AgentCmd.Providers.Pricing;

namespace AgentCmd.LiveProviders.Tests;

// This matrix is explicit even when keys are installed. One request per case,
// no retries, no tools, and a model-appropriate reasoning/output budget.
[Explicit, Category("Live"), NotInParallel]
public class AzureModelMatrixTests
{
    [
        Test,
        Arguments("gpt-5.6-sol"),
        Arguments("gpt-5.6-terra"),
        Arguments("gpt-6-astra"),
        Arguments("gpt-6-luna"),
        Arguments("gpt-6-sol"),
        Arguments("gpt-6.1-sol")
    ]
    public Task Captures_generated_command_for_configured_deployment(string model) =>
        ModelMatrixProof.RunLiveAsync("azureopenai", model);
}

[Explicit, Category("Live"), NotInParallel]
public class AnthropicModelMatrixTests
{
    [Test, Arguments("claude-haiku-5-5"), Arguments("claude-sonnet-5-5")]
    public Task Captures_generated_command_for_model(string model) =>
        ModelMatrixProof.RunLiveAsync("anthropic", model);
}

public class ModelMatrixBoundaryTests
{
    [
        Test,
        Arguments("azureopenai", "gpt-5.6-sol"),
        Arguments("azureopenai", "gpt-5.6-terra"),
        Arguments("azureopenai", "gpt-6-astra"),
        Arguments("azureopenai", "gpt-6-luna"),
        Arguments("azureopenai", "gpt-6-sol"),
        Arguments("azureopenai", "gpt-6.1-sol"),
        Arguments("anthropic", "claude-haiku-5-5"),
        Arguments("anthropic", "claude-sonnet-5-5"),
        Arguments("anthropic", "claude-opus-5-5")
    ]
    public async Task Actual_sdk_serializes_profile_specific_options_without_losing_the_schema(
        string provider,
        string model
    )
    {
        var settings = new LiveSettings(
            provider,
            "synthetic-key",
            "deployment-slot",
            new Uri("https://synthetic.example/openai/v1/"),
            model
        );
        using var handler = new MatrixTransport(settings);
        using var http = new HttpClient(handler);
        var result = await RuntimeCaptureTests.ExecuteAsync(settings, http);
        await Assert.That(handler.Valid).IsTrue();
        await Assert.That(handler.Calls).IsEqualTo(1);
        await Assert.That(result.Success.Value.Answer).IsEqualTo("OK");
        await Assert.That(result.ReadBackStored).IsTrue();
        await Assert.That(result.Snapshot.RequestedModelId).IsEqualTo("deployment-slot");
        await Assert.That(result.Snapshot.ModelId).IsEqualTo(model);
        await Assert
            .That(result.Snapshot.Usage.ValueOf(Dim.Reasoning))
            .IsEqualTo(provider == "anthropic" ? (long?)null : 1L);
    }

    sealed class MatrixTransport(LiveSettings settings) : HttpMessageHandler
    {
        internal int Calls;
        internal bool Valid;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Calls++;
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            var none = LiveProviderFactory.MinimumEffort(settings) == ReasoningEffort.None;
            Valid =
                body["model"]?.GetValue<string>() == settings.Model
                && body["temperature"] is null
                && body["top_p"] is null
                && body["top_k"] is null;
            object response;
            if (settings.Provider == "anthropic")
            {
                Valid &=
                    body["max_tokens"]?.GetValue<int>()
                        == LiveProviderFactory.OutputBudget(settings)
                    && body["thinking"]?["type"]?.GetValue<string>()
                        == (none ? "disabled" : "adaptive")
                    && body["output_config"]?["format"]?["schema"]?["properties"]?["Answer"]
                        is not null;
                response = new
                {
                    id = "msg_matrix",
                    type = "message",
                    role = "assistant",
                    model = settings.UnderlyingModel,
                    content = new[] { new { type = "text", text = "{\"Answer\":\"OK\"}" } },
                    stop_reason = "end_turn",
                    stop_sequence = (string?)null,
                    usage = new
                    {
                        input_tokens = 10,
                        output_tokens = 5,
                        cache_read_input_tokens = 0,
                        cache_creation_input_tokens = 0,
                        service_tier = "standard",
                        inference_geo = "global",
                        speed = "standard",
                    },
                };
            }
            else
            {
                Valid &=
                    body["max_completion_tokens"]?.GetValue<int>()
                        == LiveProviderFactory.OutputBudget(settings)
                    && body["reasoning_effort"]?.GetValue<string>() == (none ? "none" : "low")
                    && body["response_format"]?["json_schema"]?["schema"]?["properties"]?["Answer"]
                        is not null;
                response = new
                {
                    id = "chatcmpl-matrix",
                    @object = "chat.completion",
                    created = 1700000000,
                    model = settings.UnderlyingModel,
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            message = new { role = "assistant", content = "{\"Answer\":\"OK\"}" },
                            finish_reason = "stop",
                        },
                    },
                    usage = new
                    {
                        prompt_tokens = 10,
                        completion_tokens = 5,
                        total_tokens = 15,
                        prompt_tokens_details = new { cached_tokens = 0, cache_write_tokens = 0 },
                        completion_tokens_details = new { reasoning_tokens = 1 },
                    },
                };
            }
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(response),
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        }
    }
}

internal static class ModelMatrixProof
{
    internal static async Task RunLiveAsync(string provider, string model)
    {
        var configured = LiveSettings.Load(provider);
        // The screenshot's deployment names are explicit test data. Hosts with
        // other names map them separately; never infer a deployment's model.
        var settings = configured.WithTarget(model, model);
        var path = EvidencePaths.BeginRun(provider + "." + model + ".matrix.capture.json");
        var started = DateTimeOffset.UtcNow;
        using var transport = new OneRequestTransport(
            new SocketsHttpHandler { AllowAutoRedirect = false }
        );
        using var http = new HttpClient(transport) { Timeout = TimeSpan.FromSeconds(90) };
        try
        {
            var run = await RuntimeCaptureTests.ExecuteAsync(settings, http);
            await CaptureAssertions.CheckSnapshotAsync(run.Snapshot);
            var profile = ModelRequestCatalog.Default.Find(
                provider,
                provider == "anthropic" ? ApiKind.Messages : ApiKind.ChatCompletions,
                model
            )!;
            if (
                run.Success.Value.Answer != "OK"
                || !run.ReadBackStored
                || !profile.Model.Matches(run.Snapshot.ModelId)
                || run.Snapshot.RequestedModelId != settings.Model
                || transport.Calls != 1
                || run.Snapshot.NativeStopReason is not ("stop" or "end_turn")
            )
            {
                throw new InvalidOperationException(
                    "The matrix proof did not meet its typed-output and identity requirements."
                );
            }

            var scope = new PricingScope(
                provider == "anthropic" ? "anthropic-direct" : "azure-openai-global-standard",
                "standard",
                "global",
                provider == "anthropic" ? null : "short",
                LiveProviderFactory.Binding(settings),
                DateOnly.FromDateTime(started.UtcDateTime)
            );
            var pricing = PricingEstimator.Estimate(run.Snapshot, scope);
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(
                    new
                    {
                        Status = "Passed",
                        StartedAt = started,
                        EndedAt = DateTimeOffset.UtcNow,
                        RequestedTarget = settings.Model,
                        ExpectedModel = model,
                        Requests = transport.Calls,
                        OutputBudget = LiveProviderFactory.OutputBudget(settings),
                        ScopeIsTestAssertion = true,
                        PricingScope = scope,
                        Pricing = pricing,
                        run.Success.ExecutionId,
                        Snapshot = run.Snapshot,
                    },
                    new JsonSerializerOptions { WriteIndented = true }
                )
            );
        }
        catch (Exception error)
        {
            // No SDK exception text, request/response bodies, headers or keys in
            // runner diagnostics. Failure receipts cannot masquerade as success.
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(
                    new
                    {
                        Status = "Failed",
                        StartedAt = started,
                        EndedAt = DateTimeOffset.UtcNow,
                        RequestedTarget = settings.Model,
                        ErrorType = error.GetType().Name,
                        HttpStatus = transport.Status,
                        Requests = transport.Calls,
                    }
                )
            );
            throw new InvalidOperationException(
                $"Model matrix failed: {error.GetType().Name}; HTTP {transport.Status}; requests {transport.Calls}."
            );
        }
    }

    sealed class OneRequestTransport(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        internal int Calls;
        internal int? Status;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (Interlocked.Increment(ref Calls) > 1)
            {
                throw new InvalidOperationException("Matrix request budget exceeded.");
            }
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            Status = (int)response.StatusCode;
            return response;
        }
    }
}
