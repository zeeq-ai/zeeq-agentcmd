using System.Diagnostics;
using Zeeq.AgentCmd.Providers;
using Zeeq.AgentCmd.Providers.Pricing;

namespace Zeeq.AgentCmd.OpenTelemetry;

/// <summary>Optional behavior for <see cref="LangfuseGenerationSink"/>. Register one instance in DI to change the defaults.</summary>
public sealed record LangfuseGenerationOptions
{
    /// <summary>
    /// Links each generation to a Langfuse prompt named by the command ID, with the command revision as
    /// its version. Off by default: Langfuse links only to prompts that exist there, and a host that does
    /// not manage its prompts in Langfuse would see failed links instead of plain generations.
    /// </summary>
    public bool LinkPrompts { get; init; }

    /// <summary>
    /// Returns the billing scope the host asserts for a call, or null to leave its cost unknown. Scope is a
    /// host fact (SKU, tier, geography), never inferred from the response, so there is no default. An
    /// incomplete estimate is omitted and Langfuse may infer its own.
    /// </summary>
    public Func<AttemptRecord, PricingScope?>? PricingScope { get; init; }
}

/// <summary>
/// Emits each provider call as a Langfuse generation with its real start and end. The parent is the
/// routed attempt's span captured when the call started, so a late or queued emission still lands under
/// the right command. Register it with <c>services.AddAttemptSink&lt;LangfuseGenerationSink&gt;()</c>;
/// a registered <see cref="TokenPricingCatalog"/> and <see cref="LangfuseGenerationOptions"/> are used when present.
/// </summary>
/// <param name="options">Optional prompt linking and pricing scope; null keeps the defaults.</param>
/// <param name="pricing">Optional rate catalog; without it, or without a scope, cost stays unknown.</param>
public sealed class LangfuseGenerationSink(
    LangfuseGenerationOptions? options = null,
    TokenPricingCatalog? pricing = null
) : IAttemptSink
{
    readonly LangfuseGenerationOptions options = options ?? new();

    /// <inheritdoc/>
    public ValueTask OnAttemptAsync(AttemptRecord attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var correlation = attempt.Correlation;

        // Outside a command there is no captured span; the current activity is the best parent left.
        var parent =
            correlation is { Parent: var captured } && captured != default
                ? captured
                : Activity.Current?.Context ?? default;

        LangfuseGeneration.EmitSafely(
            attempt.Snapshot,
            parent,
            attempt.Started,
            attempt.Ended,
            options.LinkPrompts ? correlation?.Command.CommandId : null,
            options.LinkPrompts ? correlation?.Command.Revision : null,
            Estimate(attempt)
        );

        return ValueTask.CompletedTask;
    }

    PricingEstimate? Estimate(AttemptRecord attempt)
    {
        if (pricing is null || options.PricingScope is null)
        {
            return null;
        }

        try
        {
            return options.PricingScope(attempt) is { } scope
                ? pricing.Estimate(attempt.Snapshot, scope)
                : null;
        }
        catch (Exception)
        {
            // A faulty scope callback leaves cost unknown; telemetry never changes the result.
            return null;
        }
    }
}
