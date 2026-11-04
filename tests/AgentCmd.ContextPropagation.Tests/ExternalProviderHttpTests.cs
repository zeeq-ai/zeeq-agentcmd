using System.Net;
using OpenTelemetry;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd.OpenTelemetry;

namespace AgentCmd.ContextPropagation.Tests;

public sealed class ExternalProviderHttpTests
{
    [Test]
    public async Task OutboundBoundaryRemovesCurrentAndFormerPrivatePropagation()
    {
        var capture = new CaptureHandler();
        var boundary = (DelegatingHandler)ExternalProviderHttp.CreateHandler();
        boundary.InnerHandler!.Dispose();
        boundary.InnerHandler = capture;
        using var client = new HttpClient(boundary);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid")
        {
            Content = new StringContent("synthetic"),
        };
        request.Headers.TryAddWithoutValidation("AgentCmd-Context", "private");
        request.Headers.TryAddWithoutValidation("LlmCmd-Context", "private");
        request.Headers.TryAddWithoutValidation("baggage", "untrusted=value");
        request.Headers.TryAddWithoutValidation("x-safe", "ok");
        request.Content.Headers.TryAddWithoutValidation("LlmCmd-Private", "private");

        var prior = Baggage.Current;
        Baggage.Current = Baggage.Create(
            new Dictionary<string, string>
            {
                ["agentcmd.run.id"] = "private",
                ["llmcmd.run.id"] = "private",
                ["llmcmd-private"] = "private",
                ["safe"] = "ok",
            }
        );
        try
        {
            using var response = await client.SendAsync(request);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(capture.Headers.Contains("AgentCmd-Context")).IsFalse();
            await Assert.That(capture.Headers.Contains("LlmCmd-Context")).IsFalse();
            await Assert.That(capture.Headers.Contains("LlmCmd-Private")).IsFalse();
            await Assert.That(capture.Headers.Contains("baggage")).IsFalse();
            await Assert.That(capture.Headers.Contains("x-safe")).IsTrue();
            await Assert.That(capture.BaggageKeys.Contains("agentcmd.run.id")).IsFalse();
            await Assert.That(capture.BaggageKeys.Contains("llmcmd.run.id")).IsFalse();
            await Assert.That(capture.BaggageKeys.Contains("llmcmd-private")).IsFalse();
            await Assert.That(capture.BaggageKeys.Contains("safe")).IsTrue();
            await Assert
                .That(Baggage.GetBaggage().Any(item => item.Key == "llmcmd.run.id"))
                .IsTrue();
        }
        finally
        {
            Baggage.Current = prior;
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HashSet<string> Headers { get; private set; } = [];
        public HashSet<string> BaggageKeys { get; private set; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Headers = request
                .Headers.Select(header => header.Key)
                .Concat(request.Content!.Headers.Select(header => header.Key))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            BaggageKeys = Baggage
                .GetBaggage()
                .Select(item => item.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
