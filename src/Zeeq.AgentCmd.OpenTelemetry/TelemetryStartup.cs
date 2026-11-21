using System.Diagnostics;
using global::OpenTelemetry;
using global::OpenTelemetry.Trace;
using Zeeq.AgentCmd.Providers;

namespace Zeeq.AgentCmd.OpenTelemetry;

/// <summary>
/// Builds the usual trace export in one call: resolve a collector target from named secrets, subscribe to
/// AgentCmd sources and configure the OTLP exporter. The host still chooses the target and owns the
/// returned provider's lifetime, because a flush at shutdown is the host's decision.
/// </summary>
public static class TelemetryStartup
{
    /// <summary>Resolves the target and returns a running tracer provider that exports AgentCmd activities.</summary>
    /// <param name="target">Collector profile, such as <see cref="TargetOptions.Langfuse"/>.</param>
    /// <param name="secrets">Resolves the secret references in <paramref name="target"/>; credentials never appear in options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">The target is not configured or does not support traces.</exception>
    public static async Task<TracerProvider> CreateTracerAsync(
        TargetOptions target,
        ISecretSource secrets,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(secrets);

        var resolved = await Profiles
            .ResolveAsync(target, secrets, Signal.Traces, cancellationToken)
            .ConfigureAwait(false);
        if (resolved is not ConfiguredTarget configured)
        {
            throw new InvalidOperationException("The telemetry target is not configured.");
        }

        return Sdk.CreateTracerProviderBuilder()
            .AddAgentCmdInstrumentation()
            .AddOtlpExporter(options =>
            {
                if (configured.Configure(Signal.Traces, options) is not null)
                {
                    throw new InvalidOperationException("The trace exporter is not configured.");
                }
            })
            .Build();
    }
}

/// <summary>
/// An <see cref="IExtractionSink"/> that emits each provider attempt as a Langfuse generation. It runs
/// inline in the provider call, so the current activity is the command that made the call. It records
/// one timestamp for both ends; a host that needs real durations should emit from its own sink.
/// </summary>
/// <param name="promptName">Optional Langfuse prompt name to link the generation to.</param>
/// <param name="promptVersion">Optional integer prompt version.</param>
public sealed class LangfuseGenerationSink(string? promptName = null, int? promptVersion = null)
    : IExtractionSink
{
    /// <inheritdoc/>
    public ValueTask OnExtractedAsync(ExtractionResult result, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        LangfuseGeneration.EmitSafely(
            result.Snapshot,
            Activity.Current?.Context ?? default,
            now,
            now,
            promptName,
            promptVersion
        );

        return ValueTask.CompletedTask;
    }
}
