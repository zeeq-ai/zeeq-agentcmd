using System.Net;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LlmCmd.LiveProviders.Tests;

public class BillingSdkBoundaryTests
{
    [Test, Arguments("azureopenai"), Arguments("anthropic")]
    public async Task Actual_sdk_projection_preserves_billing_categories(string provider)
    {
        using var handler = new ResponseHandler(provider);
        var settings = new LiveSettings(
            provider,
            "synthetic-key",
            "synthetic-model",
            new Uri("https://synthetic.example/openai/v1/"),
            modelId: provider == "anthropic" ? "claude-haiku-5-5" : "gpt-6-luna"
        );
        var run = await ProviderCapture.RunAsync(settings, handler);
        await Assert
            .That(run.Snapshot.BillingContext?.ServiceTier)
            .IsEqualTo(provider == "anthropic" ? "standard" : "priority");
        if (provider == "anthropic")
        {
            await Assert.That(run.Snapshot.BillingContext?.InferenceGeo).IsEqualTo("global");
            await Assert.That(run.Snapshot.BillingContext?.Speed).IsEqualTo("fast");
        }
    }

    private sealed class ResponseHandler(string provider) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var json = provider == "anthropic" ? Anthropic : OpenAi;
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                }
            );
        }
    }

    private const string OpenAi = """
        {"id":"chatcmpl-test","object":"chat.completion","created":1700000000,"model":"synthetic-model","service_tier":"priority","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":3,"total_tokens":13}}
        """;
    private const string Anthropic = """
        {"id":"msg_test","type":"message","role":"assistant","model":"synthetic-model","content":[{"type":"text","text":"OK"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":3,"service_tier":"standard","inference_geo":"global","speed":"fast"}}
        """;
}
