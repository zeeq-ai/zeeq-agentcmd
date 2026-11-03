using global::OpenTelemetry.Metrics;
using global::OpenTelemetry.Trace;

namespace Zeeq.LlmCmd.OpenTelemetry;

/// <summary>Registers the library's sources without forcing an exporter on the host.</summary>
public static class InstrumentationExtensions
{
    /// <summary>Subscribes a tracer provider to command, routing, evaluation, propagation and Langfuse sources.</summary>
    public static TracerProviderBuilder AddLlmCmdInstrumentation(
        this TracerProviderBuilder builder
    ) =>
        builder.AddSource(
            "Zeeq.LlmCmd",
            ProfileTelemetry.SourceName,
            "Zeeq.LlmCmd.Evaluation",
            "Zeeq.LlmCmd.Propagation",
            LangfuseGeneration.SourceName
        );

    /// <summary>Subscribes a meter provider to profile-routing metrics.</summary>
    public static MeterProviderBuilder AddLlmCmdInstrumentation(
        this MeterProviderBuilder builder
    ) => builder.AddMeter(ProfileTelemetry.SourceName);
}
