using System.Diagnostics;
using OpenTelemetry;
using Zeeq.LlmCmd;

namespace Zeeq.LlmCmd.OpenTelemetry;

/// <summary>Creates an owned external-provider stack that filters private headers and baggage immediately before transport.</summary>
/// <remarks>Redirects are returned to the caller, never followed. Private context is not sent as authorization or baggage.</remarks>
public static class ExternalProviderHttp
{
    /// <summary>Creates a client that removes private propagation data at the final outbound boundary.</summary>
    public static HttpClient CreateClient() => new(CreateHandler());

    /// <summary>Creates the filtering handler and its transport; redirects and automatic trace propagation are disabled.</summary>
    public static HttpMessageHandler CreateHandler() =>
        new BoundaryHandler
        {
            InnerHandler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                // OTel instrumentation is a separate path, filtered by BoundaryHandler below.
                ActivityHeadersPropagator = DistributedContextPropagator.CreateNoOutputPropagator(),
            },
        };

    sealed class BoundaryHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            foreach (var name in request.Headers.Select(h => h.Key).Where(PrivateHeader).ToArray())
            {
                request.Headers.Remove(name);
            }

            if (request.Content is { } content)
            {
                foreach (
                    var name in content.Headers.Select(h => h.Key).Where(PrivateHeader).ToArray()
                )
                {
                    content.Headers.Remove(name);
                }
            }

            var prior = Baggage.Current;
            Baggage.Current = Baggage.Create(
                Baggage.GetBaggage().Where(p => !PrivateKey(p.Key)).ToDictionary()
            );
            try
            {
                return await base.SendAsync(request, ct).ConfigureAwait(false);
            }
            finally
            {
                Baggage.Current = prior;
            }
        }
    }

    static bool PrivateHeader(string name) =>
        name.Equals("baggage", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("llmcmd-", StringComparison.OrdinalIgnoreCase);

    static bool PrivateKey(string name) =>
        name.StartsWith("llmcmd.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("llmcmd-", StringComparison.OrdinalIgnoreCase);
}
