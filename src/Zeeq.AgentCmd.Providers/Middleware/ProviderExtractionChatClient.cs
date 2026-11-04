using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Zeeq.AgentCmd.Providers;

/// <summary>Downstream consumer of per-attempt snapshots (a collector, a sink converter). It sees canonical data only, never a provider shape.</summary>
public interface IExtractionSink
{
    /// <summary>Observes a completed attempt; sink failures are isolated from the provider result.</summary>
    ValueTask OnExtractedAsync(ExtractionResult result, CancellationToken cancellationToken);
}

/// <summary>
/// Observes one provider attempt. Place it directly above the provider client, below retry, failover and tool-loop middleware, so each model call is one attempt.
/// The caller gets the inner response unchanged. A failed adapter or sink never changes it.
/// </summary>
public sealed class ProviderExtractionChatClient : DelegatingChatClient
{
    readonly ProviderExtractor extractor;
    readonly ProviderIdentity registration;
    readonly IExtractionSink sink;
    readonly ProviderExtractionOptions options;

    /// <summary>Raw-content policy and the failure observer come from <paramref name="extractor"/>'s options, so complete and streamed attempts always agree.</summary>
    public ProviderExtractionChatClient(
        IChatClient inner,
        ProviderExtractor extractor,
        ProviderIdentity registration,
        IExtractionSink sink
    )
        : base(inner)
    {
        this.extractor = extractor;
        this.registration = registration;
        this.sink = sink;
        options = extractor.Options;
    }

    static string NewAttemptId() => Guid.NewGuid().ToString("N");

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default
    )
    {
        var id = NewAttemptId();
        var requestedModel = RequestedModel(chatOptions);
        ChatResponse response;
        try
        {
            response = await base.GetResponseAsync(messages, chatOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            var end = Classify(e, cancellationToken);
            await Publish(() => FinishEmpty(id, end), CancellationToken.None, requestedModel)
                .ConfigureAwait(false);
            throw;
        }
        await Publish(
                () => extractor.Extract(registration, response, id),
                cancellationToken,
                requestedModel
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
                        CancellationToken.None,
                        requestedModel
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

    async ValueTask Publish(
        Func<ExtractionResult?> build,
        CancellationToken ct,
        string? requestedModel
    )
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

        try
        {
            await sink.OnExtractedAsync(
                    ExtractionResult.From(
                        result.Snapshot with
                        {
                            RequestedModelId = requestedModel,
                        }
                    ),
                    ct
                )
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            options.ReportFailure("sink", e);
        }
    }
}

/// <summary>Registers extraction middleware at the provider boundary.</summary>
public static class ProviderExtractionBuilderExtensions
{
    /// <summary>Adds provider extraction. Call it last (innermost) so it sits directly above the provider client.</summary>
    public static ChatClientBuilder UseProviderExtraction(
        this ChatClientBuilder builder,
        ProviderExtractor extractor,
        ProviderIdentity registration,
        IExtractionSink sink
    ) =>
        builder.Use(inner => new ProviderExtractionChatClient(
            inner,
            extractor,
            registration,
            sink
        ));
}
