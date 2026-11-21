using Microsoft.Extensions.AI;

namespace Zeeq.AgentCmd.Providers;

/// <summary>
/// Registers a named AgentCmd client with capture already placed at its provider boundary. This lives
/// in Providers because core never references adapters; the host gets one call instead of building the
/// pipeline, the identity and the extractor by hand.
/// </summary>
public static class AgentCmdBuilderCaptureExtensions
{
    /// <summary>
    /// Registers a runtime-owned client whose every provider call reaches the <see cref="IAttemptSink"/>s in DI.
    /// Capture is always innermost: middleware added by <paramref name="configure"/> (retry, tools, caching)
    /// wraps it, so each actual provider call is one record.
    /// </summary>
    /// <param name="builder">AgentCmd builder.</param>
    /// <param name="name">Case-sensitive client name referenced by profiles; it is also the capture registration ID.</param>
    /// <param name="provider">Creates a new provider client; the runtime owns and disposes the pipeline.</param>
    /// <param name="protocol">Explicit provider and wire protocol, such as <see cref="Protocols.AzureOpenAIChatCompletions"/>.</param>
    /// <param name="fixedModel">Explicit model or deployment binding; null declares per-request model selection.</param>
    /// <param name="classify">Optional provider exception classifier for retry and fallback.</param>
    /// <param name="configure">Adds host middleware outside capture.</param>
    public static AgentCmdBuilder AddChatClient(
        this AgentCmdBuilder builder,
        string name,
        Func<IServiceProvider, IChatClient> provider,
        ProviderProtocol protocol,
        string? fixedModel = null,
        Func<Exception, FailureKind>? classify = null,
        Action<ChatClientBuilder>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(protocol);

        return builder.AddChatClient(
            name,
            services =>
            {
                // The first Use is outermost, so host middleware goes on before capture.
                var pipeline = new ChatClientBuilder(provider(services));
                configure?.Invoke(pipeline);
                return pipeline.UseAttemptCapture(protocol, name).Build(services);
            },
            fixedModel,
            classify
        );
    }
}
