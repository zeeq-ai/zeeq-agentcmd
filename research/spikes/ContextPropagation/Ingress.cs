using System.Diagnostics;

namespace LlmCmd.ContextPropagation;

public closed record ContextResolution
{
    public sealed record Resolved(OperationContext Context) : ContextResolution;
    public sealed record Rejected(string Code) : ContextResolution;
    public sealed record Unavailable : ContextResolution;
}
/// <summary>Host resolves the reference using its independently authenticated actor and work store.</summary>
public interface IOperationContextResolver
{
    ValueTask<ContextResolution> ResolveAsync(UntrustedIncoming incoming, CancellationToken ct);
}
public closed record IngressResult<T>
{
    public sealed record Handled(T Value) : IngressResult<T>;
    public sealed record Rejected(string Code) : IngressResult<T>;
    public sealed record Unavailable : IngressResult<T>;
    public sealed record Cancelled : IngressResult<T>;
    public sealed record Failed : IngressResult<T>;
}

public static class PropagationIngress
{
    public static readonly ActivitySource Activity = new("LlmCmd.ContextPropagation");

    /// <summary>Enters the scope in the same flow that invokes the handler. Does not acknowledge a broker.</summary>
    public static async ValueTask<IngressResult<T>> HandleAsync<T>(UntrustedIncoming incoming,
        IOperationContextResolver resolver, Func<OperationContext, WorkEnvelope?, CancellationToken, ValueTask<T>> handler,
        CancellationToken ct = default)
    {
        try
        {
            var resolved = await resolver.ResolveAsync(incoming, ct).ConfigureAwait(false);
            if (resolved is ContextResolution.Rejected rejection) return new IngressResult<T>.Rejected(rejection.Code);
            if (resolved is ContextResolution.Unavailable) return new IngressResult<T>.Unavailable();
            var context = ((ContextResolution.Resolved)resolved).Context;
            if (context is null || context.Reference != incoming.Reference || context.WorkId != incoming.Work?.WorkId)
                return new IngressResult<T>.Rejected("resolver_context_mismatch");
            ActivityLink[] links = incoming.Producer.TraceId == default ? [] : [new(incoming.Producer)];
            using var span = Activity.StartActivity("llmcmd.message.process", ActivityKind.Consumer,
                parentContext: System.Diagnostics.Activity.Current?.Context ?? default, links: links);
            span?.SetTag("llmcmd.output.draft_id", context.Reference.Draft?.DraftId.ToString());
            span?.SetTag("llmcmd.work.id", context.WorkId?.ToString());
            using var scope = LlmCmdContext.BeginScope(context);
            var value = await handler(context, incoming.Work, ct).ConfigureAwait(false);
            return new IngressResult<T>.Handled(value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new IngressResult<T>.Cancelled(); }
        catch { return new IngressResult<T>.Failed(); }
    }
}
