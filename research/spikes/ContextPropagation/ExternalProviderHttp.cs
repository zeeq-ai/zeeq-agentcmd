using System.Diagnostics;
using OpenTelemetry;

namespace LlmCmd.ContextPropagation;

/// <summary>Owned external-provider stack. Redirects are returned to the caller, never followed.</summary>
public static class ExternalProviderHttp
{
    public static HttpClient CreateClient() => new(CreateHandler());
    public static HttpMessageHandler CreateHandler() => new BoundaryHandler
    {
        InnerHandler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            // OTel instrumentation is a separate path, filtered by BoundaryHandler below.
            ActivityHeadersPropagator = DistributedContextPropagator.CreateNoOutputPropagator()
        }
    };
    sealed class BoundaryHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            foreach (var name in request.Headers.Select(h => h.Key).Where(PrivateHeader).ToArray()) request.Headers.Remove(name);
            if (request.Content is { } content)
                foreach (var name in content.Headers.Select(h => h.Key).Where(PrivateHeader).ToArray()) content.Headers.Remove(name);
            var prior = Baggage.Current;
            Baggage.Current = Baggage.Create(Baggage.GetBaggage().Where(p => !PrivateKey(p.Key)).ToDictionary());
            try { return await base.SendAsync(request, ct).ConfigureAwait(false); }
            finally { Baggage.Current = prior; }
        }
    }
    static bool PrivateHeader(string name) => name.Equals("baggage", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("llmcmd-", StringComparison.OrdinalIgnoreCase);
    static bool PrivateKey(string name) => name.StartsWith("llmcmd.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("llmcmd-", StringComparison.OrdinalIgnoreCase);
}
