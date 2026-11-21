using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zeeq.AgentCmd;

namespace Zeeq.AgentCmd.Providers;

/// <summary>
/// One finished provider call: its normalized extraction, the command it belongs to and when it ran.
/// The record is the unit every downstream consumer shares (a collector, a database row, a pricing
/// job), so each sink sees the same canonical data and never a provider shape.
/// </summary>
/// <param name="Result">Closed extraction outcome; its case tells whether the evidence is complete.</param>
/// <param name="Started">When the call was sent, or when a stream was first enumerated.</param>
/// <param name="Ended">When the response, failure or stream disposal finished the call.</param>
public sealed record AttemptRecord(
    ExtractionResult Result,
    DateTimeOffset Started,
    DateTimeOffset Ended
)
{
    /// <summary>Normalized snapshot of the call; missing usage stays unknown.</summary>
    public ResponseSnapshot Snapshot => Result.Snapshot;

    /// <summary>Command and routed attempt the call ran under; null outside a command.</summary>
    public AttemptCorrelation? Correlation => Result.Snapshot.Correlation;
}

/// <summary>
/// Downstream consumer of finished provider calls, such as a trace emitter or an attempt table.
/// Register several through DI with <see cref="AttemptCaptureServiceCollectionExtensions.AddAttemptSink{TSink}"/>;
/// each runs in isolation, so one failing sink cannot hide a call from another or change the model result.
/// </summary>
public interface IAttemptSink
{
    /// <summary>Observes one finished call. It runs inline before the response returns, so keep it fast or queue the work.</summary>
    /// <param name="attempt">The finished call.</param>
    /// <param name="cancellationToken">The caller's token for a completed call; none for failures and stream disposal, which must still be recorded.</param>
    ValueTask OnAttemptAsync(AttemptRecord attempt, CancellationToken cancellationToken);
}

/// <summary>
/// Observes each provider call. Place it directly above the provider client, below retry, failover and
/// tool-loop middleware, so each model call is one record. The caller gets the inner response unchanged;
/// a failed adapter or sink never changes it.
/// </summary>
public sealed class AttemptCaptureChatClient : DelegatingChatClient
{
    readonly ProviderExtractor extractor;
    readonly ProviderIdentity registration;
    readonly IAttemptSink[] sinks;
    readonly ProviderExtractionOptions options;

    /// <summary>Raw-content policy, clock and failure observer come from <paramref name="extractor"/>'s options, so complete and streamed calls always agree.</summary>
    /// <param name="inner">Provider client.</param>
    /// <param name="extractor">Normalizes responses through the registered adapters.</param>
    /// <param name="registration">Explicit provider and protocol identity.</param>
    /// <param name="sinks">Receive each finished call in order, each isolated from the others.</param>
    public AttemptCaptureChatClient(
        IChatClient inner,
        ProviderExtractor extractor,
        ProviderIdentity registration,
        IEnumerable<IAttemptSink> sinks
    )
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(sinks);

        this.extractor = extractor;
        this.registration = registration;
        this.sinks = [.. sinks];
        options = extractor.Options;
    }

    static string NewAttemptId() => Guid.NewGuid().ToString("N");

    DateTimeOffset Now() => options.TimeProvider.GetUtcNow();

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default
    )
    {
        var id = NewAttemptId();
        var requestedModel = RequestedModel(chatOptions);

        // Read once, before the send: a queued sink or late completion keeps this attempt.
        var correlation = AttemptScope.Current;
        var started = Now();

        ChatResponse response;
        try
        {
            response = await base.GetResponseAsync(messages, chatOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            var end = Classify(e, cancellationToken);
            await Publish(
                    () => FinishEmpty(id, end),
                    new(started, Now(), requestedModel, correlation),
                    CancellationToken.None
                )
                .ConfigureAwait(false);
            throw;
        }

        await Publish(
                () => extractor.Extract(registration, response, id),
                new(started, Now(), requestedModel, correlation),
                cancellationToken
            )
            .ConfigureAwait(false);
        return response;
    }

    /// <summary>Cancelled only when the caller asked for it. An exception from a live token (an SDK timeout, for example) is a failure.</summary>
    static AttemptEnd Classify(Exception e, CancellationToken caller) =>
        e is OperationCanceledException && caller.IsCancellationRequested
            ? AttemptEnd.Cancelled
            : AttemptEnd.Failed;

    ExtractionResult FinishEmpty(string id, AttemptEnd end) =>
        extractor
            .Begin(registration, id, streaming: false)
            .Complete(
                null,
                null,
                null,
                null,
                null,
                null,
                new(0, 0, 0, 0),
                null,
                end,
                [
                    new(
                        Severity.Warning,
                        "attempt-" + end.ToString().ToLowerInvariant(),
                        "attempt",
                        $"Attempt ended as {end} before a response; usage is unknown"
                    ),
                ]
            );

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? chatOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var id = NewAttemptId();
        var requestedModel = RequestedModel(chatOptions);

        // Streaming starts on first enumeration; read the attempt and the clock there, once.
        var correlation = AttemptScope.Current;
        var started = Now();
        var attempt = extractor.Begin(registration, id, streaming: true);
        string? responseId = null,
            modelId = null,
            finish = null;
        int text = 0,
            reasoning = 0,
            tools = 0,
            other = 0;
        List<CapturedContent>? raw = options.RawContent.Capture ? [] : null;
        var end = AttemptEnd.Abandoned; // stays Abandoned if the consumer disposes early
        var inner = base.GetStreamingResponseAsync(messages, chatOptions, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool more;
                try
                {
                    more = await inner.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    end = Classify(e, cancellationToken);
                    throw;
                }
                if (!more)
                {
                    end = AttemptEnd.Completed;
                    break;
                }
                var update = inner.Current;
                responseId = update.ResponseId ?? responseId;
                modelId = update.ModelId ?? modelId;
                finish = update.FinishReason?.Value ?? finish;
                foreach (var c in update.Contents)
                {
                    if (c is UsageContent uc)
                    {
                        attempt.ObserveUsageFrame(uc.Details);
                    }
                }

                var (shape, captured) = ProviderExtractor.Shape(
                    update.Contents,
                    options.RawContent
                );
                text += shape.TextItems;
                reasoning += shape.ReasoningItems;
                tools += shape.ToolCalls;
                other += shape.OtherItems;
                if (captured is not null)
                {
                    raw!.AddRange(captured);
                }

                yield return update;
            }
        }
        finally
        {
            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                if (end is AttemptEnd.Completed or AttemptEnd.Abandoned)
                {
                    end = AttemptEnd.Failed;
                }

                throw;
            } // the provider's exception policy is preserved
            finally
            {
                // Publish without cancellation, even when dispose failed: finalization runs exactly once.
                await Publish(
                        () =>
                            attempt.FinishStream(
                                end,
                                responseId,
                                modelId,
                                finish,
                                new(text, reasoning, tools, other),
                                raw
                            ),
                        new(started, Now(), requestedModel, correlation),
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);
            }
        }
    }

    string? RequestedModel(ChatOptions? chatOptions)
    {
        try
        {
            return chatOptions?.ModelId
                ?? InnerClient.GetService<ChatClientMetadata>()?.DefaultModelId;
        }
        catch (Exception e)
        {
            options.ReportFailure("request-metadata", e);
            return null;
        }
    }

    /// <summary>Facts read at call start and end, kept apart from extraction so a failing adapter cannot lose them.</summary>
    readonly record struct CallFacts(
        DateTimeOffset Started,
        DateTimeOffset Ended,
        string? RequestedModel,
        AttemptCorrelation? Correlation
    );

    async ValueTask Publish(Func<ExtractionResult?> build, CallFacts facts, CancellationToken ct)
    {
        ExtractionResult? result;
        try
        {
            result = build();
        }
        catch (Exception e)
        {
            options.ReportFailure("extractor", e);
            return;
        }
        if (result is null)
        {
            return;
        }

        var record = new AttemptRecord(
            ExtractionResult.From(
                result.Snapshot with
                {
                    RequestedModelId = facts.RequestedModel,
                    Correlation = facts.Correlation,
                }
            ),
            facts.Started,
            facts.Ended
        );

        // Each sink is isolated: a collector outage must not drop the row another sink writes.
        foreach (var sink in sinks)
        {
            try
            {
                await sink.OnAttemptAsync(record, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                options.ReportFailure("sink", e);
            }
        }
    }
}

/// <summary>Adds per-call capture at the provider boundary of a MEAI pipeline.</summary>
public static class AttemptCaptureBuilderExtensions
{
    /// <summary>
    /// Adds capture for a protocol preset and delivers each call to every <see cref="IAttemptSink"/> in DI.
    /// Call it last (innermost) so it sits directly above the provider. Sinks, extraction options and the
    /// clock are resolved when the pipeline is built, so sinks registered after this call still apply.
    /// </summary>
    /// <param name="builder">Client builder; call this last so it sits directly above the provider.</param>
    /// <param name="protocol">Provider key, wire protocol and adapter, chosen explicitly by the host.</param>
    /// <param name="registrationId">Client registration name, such as the DI key or the AgentCmd client name.</param>
    public static ChatClientBuilder UseAttemptCapture(
        this ChatClientBuilder builder,
        ProviderProtocol protocol,
        string registrationId
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(protocol);
        var registration = protocol.Identify(registrationId);

        return builder.Use(
            (inner, services) =>
                new AttemptCaptureChatClient(
                    inner,
                    new ProviderExtractor(
                        new ProviderAdapterRegistry(protocol.Adapters),
                        services.GetService<ProviderExtractionOptions>()
                    ),
                    registration,
                    services.GetServices<IAttemptSink>()
                )
        );
    }

    /// <summary>Adds capture with an explicit extractor and sinks, for hosts that build the pipeline without DI.</summary>
    /// <param name="builder">Client builder; call this last so it sits directly above the provider.</param>
    /// <param name="extractor">Normalizes responses through the registered adapters.</param>
    /// <param name="registration">Explicit provider and protocol identity.</param>
    /// <param name="sinks">Receive each finished call.</param>
    public static ChatClientBuilder UseAttemptCapture(
        this ChatClientBuilder builder,
        ProviderExtractor extractor,
        ProviderIdentity registration,
        params IAttemptSink[] sinks
    ) => builder.Use(inner => new AttemptCaptureChatClient(inner, extractor, registration, sinks));
}

/// <summary>Registers attempt sinks that every capture pipeline built from this container shares.</summary>
public static class AttemptCaptureServiceCollectionExtensions
{
    /// <summary>
    /// Adds a singleton sink. Capture runs in a singleton client pipeline, so a sink must be safe to share;
    /// registering the same type twice is ignored rather than recording each call twice.
    /// </summary>
    /// <typeparam name="TSink">Sink implementation, constructed by DI.</typeparam>
    /// <param name="services">Service collection to extend.</param>
    public static IServiceCollection AddAttemptSink<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSink
    >(this IServiceCollection services)
        where TSink : class, IAttemptSink
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAttemptSink, TSink>());
        return services;
    }

    /// <summary>Adds an existing sink instance, such as an in-memory collector in a test.</summary>
    /// <param name="services">Service collection to extend.</param>
    /// <param name="sink">Shared sink instance; the host keeps ownership.</param>
    public static IServiceCollection AddAttemptSink(
        this IServiceCollection services,
        IAttemptSink sink
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(sink);
        services.AddSingleton(sink);
        return services;
    }
}
