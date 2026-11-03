using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text;
using OpenTelemetry.Exporter;
using Zeeq.LlmCmd;

namespace Zeeq.LlmCmd.OpenTelemetry;

/// <summary>OTLP signal types requested from a collector target.</summary>
[Flags]
public enum Signal
{
    /// <summary>Distributed trace spans.</summary>
    Traces = 1,
    /// <summary>Metric measurements.</summary>
    Metrics = 2,
    /// <summary>Log records.</summary>
    Logs = 4
}
/// <summary>Deployment environment used to address a LangSmith agent.</summary>
public enum AgentEnvironment
{
    /// <summary>Local development environment.</summary>
    Local,
    /// <summary>Shared development environment.</summary>
    Development,
    /// <summary>Preproduction environment.</summary>
    Staging,
    /// <summary>Production environment.</summary>
    Production
}
/// <summary>Interpretation of the Braintrust parent identifier.</summary>
public enum BraintrustParentKind
{
    /// <summary>Parent is a project ID.</summary>
    ProjectId,
    /// <summary>Parent is a project name.</summary>
    ProjectName,
    /// <summary>Parent is an experiment ID.</summary>
    ExperimentId
}
/// <summary>Authorization header format expected by a Logfire endpoint.</summary>
public enum LogfireAuthorization
{
    /// <summary>Send the token without a scheme prefix.</summary>
    Raw,
    /// <summary>Prefix the token with the Bearer scheme.</summary>
    Bearer
}
/// <summary>A named reference resolved by the host rather than a secret value.</summary>
/// <param name="Name">The host-defined credential reference.</param>
public sealed record SecretRef(string Name);
/// <summary>Resolves collector credentials without placing values in target options.</summary>
public interface ISecretSource
{
    /// <summary>Returns the secret for a reference, or null when it is unavailable.</summary>
    ValueTask<string?> GetAsync(SecretRef reference, CancellationToken ct = default);
}
/// <summary>LangSmith routing address, either a project or a deployed agent.</summary>
public closed record SmithAddress
{
    /// <summary>Routes telemetry to a named project.</summary>
    /// <param name="Name">The LangSmith project name.</param>
    public sealed record Project(string Name) : SmithAddress;
    /// <summary>Routes telemetry to a deployed agent and environment.</summary>
    /// <param name="Id">The agent identifier.</param>
    /// <param name="Environment">The agent deployment environment.</param>
    public sealed record Agent(string Id, AgentEnvironment Environment) : SmithAddress;
}
/// <summary>Collector endpoint and credential references resolved before configuring an exporter.</summary>
public closed record TargetOptions
{
    /// <summary>Langfuse OTLP endpoint with public and secret project keys.</summary>
    /// <param name="Base">The OTLP base URI, before the signal path.</param>
    /// <param name="PublicKey">Reference to the Langfuse public key.</param>
    /// <param name="SecretKey">Reference to the Langfuse secret key.</param>
    public sealed record Langfuse(Uri Base, SecretRef PublicKey, SecretRef SecretKey) : TargetOptions;
    /// <summary>LangSmith endpoint, API key and destination.</summary>
    /// <param name="Base">The OTLP base URI, before the signal path.</param>
    /// <param name="ApiKey">Reference to the API key.</param>
    /// <param name="Address">Project or agent destination.</param>
    public sealed record LangSmith(Uri Base, SecretRef ApiKey, SmithAddress Address) : TargetOptions;
    /// <summary>Braintrust endpoint and project or experiment parent.</summary>
    /// <param name="Base">The OTLP base URI, before the signal path.</param>
    /// <param name="ApiKey">Reference to the API key.</param>
    /// <param name="Kind">How to interpret the parent value.</param>
    /// <param name="Parent">Project or experiment identifier.</param>
    public sealed record Braintrust(Uri Base, SecretRef ApiKey, BraintrustParentKind Kind, string Parent) : TargetOptions;
    /// <summary>Logfire endpoint and token authorization scheme.</summary>
    /// <param name="Base">The OTLP base URI, before the signal path.</param>
    /// <param name="Token">Reference to the write token.</param>
    /// <param name="Scheme">Whether the authorization value uses a Bearer prefix.</param>
    public sealed record Logfire(Uri Base, SecretRef Token, LogfireAuthorization Scheme = LogfireAuthorization.Raw) : TargetOptions;
    /// <summary>Generic OTLP endpoint with host-resolved secret headers.</summary>
    /// <param name="Base">The OTLP base URI, before the signal path.</param>
    /// <param name="HeaderSecrets">Header names mapped to credential references.</param>
    public sealed record Generic(Uri Base, IReadOnlyDictionary<string, SecretRef> HeaderSecrets) : TargetOptions;
}
/// <summary>A safe collector configuration failure without secret values.</summary>
/// <param name="Message">A bounded explanation of the failure.</param>
public sealed record ConfigurationError(string Message);
/// <summary>Either a configured collector target or a configuration error.</summary>
public union TargetResult(ConfiguredTarget, ConfigurationError);
/// <summary>Resolved signal endpoints and private authorization headers for an OTLP exporter.</summary>
public sealed class ConfiguredTarget
{
    readonly IReadOnlyDictionary<string, string> headers;
    readonly IReadOnlyDictionary<Signal, Uri> endpoints;
    internal ConfiguredTarget(Signal signals, Dictionary<Signal, Uri> paths, Dictionary<string, string> values)
    {
        SupportedSignals = signals;
        endpoints = new ReadOnlyDictionary<Signal, Uri>(new Dictionary<Signal, Uri>(paths));
        headers = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(values));
    }
    /// <summary>Signals that the target profile supports.</summary>
    public Signal SupportedSignals
    {
        get;
    }
    /// <summary>Resolved endpoints for the signals requested during configuration.</summary>
    public IReadOnlyDictionary<Signal, Uri> Endpoints => endpoints;
    /// <inheritdoc />
    public override string ToString() => $"ConfiguredTarget(Signals={SupportedSignals}, Headers=[redacted])";
    /// <summary>Configures HTTP protobuf export for a requested signal, or returns an unsupported-signal error.</summary>
    public ConfigurationError? Configure(Signal signal, OtlpExporterOptions options)
    {
        if (!endpoints.TryGetValue(signal, out var endpoint))
        {
            return new("Unsupported signal");
        }

        options.Protocol = OtlpExportProtocol.HttpProtobuf;
        options.Endpoint = endpoint;
        options.Headers = string.Join(",", headers.Select(x => $"{x.Key}={Uri.EscapeDataString(x.Value)}"));
        return null;
    }
}
/// <summary>Validates and resolves vendor OTLP destinations and their explicit attribute mappings.</summary>
public static class Profiles
{
    /// <summary>Langfuse cloud OTLP base URI.</summary>
    public static readonly Uri LangfuseCloud = new("https://cloud.langfuse.com/api/public/otel");
    /// <summary>LangSmith cloud OTLP base URI.</summary>
    public static readonly Uri LangSmithCloud = new("https://api.smith.langchain.com/otel");
    /// <summary>Braintrust cloud OTLP base URI.</summary>
    public static readonly Uri BraintrustCloud = new("https://api.braintrust.dev/otel");
    /// <summary>Logfire US OTLP base URI.</summary>
    public static readonly Uri LogfireUs = new("https://logfire-us.pydantic.dev");
    /// <summary>Resolves credentials and validates signal support, safe headers and the base URI before adding signal paths. HTTPS is required except for loopback HTTP.</summary>
    public static async ValueTask<TargetResult> ResolveAsync(TargetOptions target, ISecretSource secrets, Signal requested, CancellationToken ct = default)
    {
        if (target is null || secrets is null)
        {
            return new ConfigurationError("Target and secret source are required");
        }

        Uri uri;
        Signal supported;
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        async ValueTask<string?> Secret(SecretRef? reference) => reference is null || string.IsNullOrWhiteSpace(reference.Name) ? null : await secrets.GetAsync(reference, ct);
        try
        {
            switch (target)
            {
                case TargetOptions.Langfuse x:
                    uri = x.Base;
                    supported = Signal.Traces;
                    var pk = await Secret(x.PublicKey);
                    var sk = await Secret(x.SecretKey);
                    if (!SafeToken(pk) || !SafeToken(sk) || pk!.Contains(':'))
                    {
                        return new ConfigurationError("Langfuse credentials unavailable or invalid");
                    }

                    h.Add("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(pk + ":" + sk)));
                    h.Add("x-langfuse-ingestion-version", "4");
                    break;
                case TargetOptions.LangSmith x:
                    uri = x.Base;
                    supported = Signal.Traces;
                    var smith = await Secret(x.ApiKey);
                    if (!SafeToken(smith))
                    {
                        return new ConfigurationError("LangSmith credential unavailable or invalid");
                    }

                    h.Add("x-api-key", smith!);
                    switch (x.Address)
                    {
                        case SmithAddress.Project p when SafeHeaderValue(p.Name):
                            h.Add("Langsmith-Project", p.Name);
                            break;
                        case SmithAddress.Agent a when SafeHeaderValue(a.Id) && Enum.IsDefined(a.Environment):
                            h.Add("Langsmith-Agent-Id", a.Id);
                            h.Add("Langsmith-Agent-Environment", a.Environment.ToString().ToLowerInvariant());
                            break;
                        default:
                            return new ConfigurationError("Invalid LangSmith address");
                    }
                    break;
                case TargetOptions.Braintrust x:
                    uri = x.Base;
                    supported = Signal.Traces | Signal.Logs;
                    var bt = await Secret(x.ApiKey);
                    if (!SafeToken(bt))
                    {
                        return new ConfigurationError("Braintrust credential unavailable or invalid");
                    }

                    if (!SafeHeaderValue(x.Parent) || !Enum.IsDefined(x.Kind))
                    {
                        return new ConfigurationError("Invalid Braintrust parent");
                    }

                    h.Add("Authorization", "Bearer " + bt);
                    h.Add("x-bt-parent", (x.Kind switch
                    {
                        BraintrustParentKind.ProjectId => "project_id",
                        BraintrustParentKind.ProjectName => "project_name",
                        BraintrustParentKind.ExperimentId => "experiment_id",
                        _ => throw new InvalidOperationException()
                    }) + ":" + x.Parent);
                    break;
                case TargetOptions.Logfire x:
                    uri = x.Base;
                    supported = Signal.Traces | Signal.Metrics | Signal.Logs;
                    var lf = await Secret(x.Token);
                    if (!SafeToken(lf) || !Enum.IsDefined(x.Scheme))
                    {
                        return new ConfigurationError("Logfire credential or scheme invalid");
                    }

                    h.Add("Authorization", x.Scheme == LogfireAuthorization.Bearer ? "Bearer " + lf : lf!);
                    break;
                case TargetOptions.Generic x:
                    uri = x.Base;
                    supported = Signal.Traces | Signal.Metrics | Signal.Logs;
                    if (x.HeaderSecrets is null)
                    {
                        return new ConfigurationError("Header map required");
                    }

                    foreach (var (name, reference) in x.HeaderSecrets)
                    {
                        if (!SafeHeaderName(name) || h.ContainsKey(name))
                        {
                            return new ConfigurationError("Invalid or duplicate header name");
                        }

                        var value = await Secret(reference);
                        if (!SafeToken(value))
                        {
                            return new ConfigurationError("Header secret unavailable or invalid");
                        }

                        h.Add(name, value!);
                    }
                    break;
                default:
                    return new ConfigurationError("Unknown target");
            }
            if (uri is null || !uri.IsAbsoluteUri || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) || uri.AbsolutePath.TrimEnd('/').EndsWith("/v1/traces", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.TrimEnd('/').EndsWith("/v1/metrics", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.TrimEnd('/').EndsWith("/v1/logs", StringComparison.OrdinalIgnoreCase))
            {
                return new ConfigurationError("Invalid OTLP base URI");
            }

            if (requested == 0 || (requested & ~supported) != 0)
            {
                return new ConfigurationError("Unsupported signal");
            }

            var paths = new Dictionary<Signal, Uri>();
            foreach (var signal in new[] { Signal.Traces, Signal.Metrics, Signal.Logs })
            {
                if ((requested & signal) != 0)
                {
                    paths.Add(signal, new Uri(uri.AbsoluteUri.TrimEnd('/') + "/v1/" + signal.ToString().ToLowerInvariant()));
                }
            }

            return new ConfiguredTarget(supported, paths, h);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return new ConfigurationError("Unable to resolve target configuration"); }
    }
    static bool SafeToken(string? value) => !string.IsNullOrWhiteSpace(value) && value.All(c => c >= 0x20 && c <= 0x7e && c != ',');
    static bool SafeHeaderValue(string? value) => SafeToken(value);
    static bool SafeHeaderName(string? name) => !string.IsNullOrWhiteSpace(name) && !name.Equals("Host", StringComparison.OrdinalIgnoreCase) && !name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && name.All(c => char.IsAsciiLetterOrDigit(c) || "!#$&'*+-.^_`|~".Contains(c));
    /// <summary>Creates legacy string-valued Langfuse prompt attributes; use the typed mapping for integer OTLP versions.</summary>
    public static IReadOnlyDictionary<string, string> LangfusePrompt(string name, string? version)
    {
        var values = new Dictionary<string, string> { ["langfuse.observation.prompt.name"] = name };
        if (version is not null)
        {
            values["langfuse.observation.prompt.version"] = version;
        }

        return new ReadOnlyDictionary<string, string>(values);
    }
    /// <summary>Typed prompt attributes for OTLP, where Langfuse requires an integer version.</summary>
    public static IReadOnlyDictionary<string, object> LangfusePromptAttributes(string name, int? version = null)
    {
        var values = new Dictionary<string, object> { ["langfuse.observation.prompt.name"] = name };
        if (version is not null)
        {
            values["langfuse.observation.prompt.version"] = version.Value;
        }

        return new ReadOnlyDictionary<string, object>(values);
    }
    /// <summary>Maps a model operation to the LangSmith llm kind, or another operation to chain.</summary>
    public static IReadOnlyDictionary<string, string> LangSmithKind(bool isModel) => new ReadOnlyDictionary<string, string>(new Dictionary<string, string> { ["langsmith.span.kind"] = isModel ? "llm" : "chain" });
}
