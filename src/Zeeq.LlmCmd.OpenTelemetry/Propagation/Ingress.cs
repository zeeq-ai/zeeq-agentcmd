using System.Diagnostics;
using Zeeq.LlmCmd;

namespace Zeeq.LlmCmd.OpenTelemetry;

/// <summary>Outcome of resolving an incoming reference against the host's trusted context store.</summary>
public closed record ContextResolution
{
    /// <summary>The host resolved and authorized the context.</summary>
    /// <param name="Context">The trusted operation context.</param>
    public sealed record Resolved(OperationContext Context) : ContextResolution;
    /// <summary>The host rejected the incoming reference.</summary>
    /// <param name="Code">A bounded reason for rejection.</param>
    public sealed record Rejected(string Code) : ContextResolution;
    /// <summary>The host could not access its context store.</summary>
    public sealed record Unavailable : ContextResolution;
}
/// <summary>Host resolves the reference using its independently authenticated actor and work store.</summary>
public interface IOperationContextResolver
{
    /// <summary>Resolves and authorizes an untrusted reference without treating propagation as authentication.</summary>
    ValueTask<ContextResolution> ResolveAsync(UntrustedIncoming incoming, CancellationToken ct);
}
/// <summary>Outcome of resolving context and invoking an ingress handler.</summary>
public closed record IngressResult<T>
{
    /// <summary>The handler completed with a value.</summary>
    /// <param name="Value">The handler's result.</param>
    public sealed record Handled(T Value) : IngressResult<T>;
    /// <summary>The reference or resolved context was rejected.</summary>
    /// <param name="Code">A bounded reason for rejection.</param>
    public sealed record Rejected(string Code) : IngressResult<T>;
    /// <summary>The resolver was unavailable.</summary>
    public sealed record Unavailable : IngressResult<T>;
    /// <summary>The supplied cancellation token was cancelled.</summary>
    public sealed record Cancelled : IngressResult<T>;
    /// <summary>Resolution or handling failed without exposing exception contents.</summary>
    public sealed record Failed : IngressResult<T>;
}

/// <summary>Resolves untrusted references and invokes handlers inside a trusted context scope.</summary>
public static class PropagationIngress
{
    /// <summary>Source of consumer spans linked to the producer trace.</summary>
    public static readonly ActivitySource Activity = new("Zeeq.LlmCmd.Propagation");

    /// <summary>Enters the scope in the same flow that invokes the handler. Does not acknowledge a broker.</summary>
    public static async ValueTask<IngressResult<T>> HandleAsync<T>(UntrustedIncoming incoming,
        IOperationContextResolver resolver, Func<OperationContext, WorkEnvelope?, CancellationToken, ValueTask<T>> handler,
        CancellationToken ct = default)
    {
        try
        {
            var resolved = await resolver.ResolveAsync(incoming, ct).ConfigureAwait(false);
            if (resolved is ContextResolution.Rejected rejection)
            {
                return new IngressResult<T>.Rejected(rejection.Code);
            }

            if (resolved is ContextResolution.Unavailable)
            {
                return new IngressResult<T>.Unavailable();
            }

            var context = ((ContextResolution.Resolved)resolved).Context;
            if (context is null || context.Reference != incoming.Reference || context.WorkId != incoming.Work?.WorkId)
            {
                return new IngressResult<T>.Rejected("resolver_context_mismatch");
            }

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
