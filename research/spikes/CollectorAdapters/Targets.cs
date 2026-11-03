using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text;
using OpenTelemetry.Exporter;

namespace CollectorAdapters;
[Flags] public enum Signal { Traces = 1, Metrics = 2, Logs = 4 }
public enum AgentEnvironment { Local, Development, Staging, Production }
public enum BraintrustParentKind { ProjectId, ProjectName, ExperimentId }
public enum LogfireAuthorization { Raw, Bearer }
public sealed record SecretRef(string Name);
public interface ISecretSource { ValueTask<string?> GetAsync(SecretRef reference, CancellationToken ct = default); }
public closed record SmithAddress
{
    public sealed record Project(string Name) : SmithAddress;
    public sealed record Agent(string Id, AgentEnvironment Environment) : SmithAddress;
}
public closed record TargetOptions
{
    public sealed record Langfuse(Uri Base, SecretRef PublicKey, SecretRef SecretKey) : TargetOptions;
    public sealed record LangSmith(Uri Base, SecretRef ApiKey, SmithAddress Address) : TargetOptions;
    public sealed record Braintrust(Uri Base, SecretRef ApiKey, BraintrustParentKind Kind, string Parent) : TargetOptions;
    public sealed record Logfire(Uri Base, SecretRef Token, LogfireAuthorization Scheme = LogfireAuthorization.Raw) : TargetOptions;
    public sealed record Generic(Uri Base, IReadOnlyDictionary<string, SecretRef> HeaderSecrets) : TargetOptions;
}
public sealed record ConfigurationError(string Message);
public union TargetResult(ConfiguredTarget, ConfigurationError);
public sealed class ConfiguredTarget
{
    readonly IReadOnlyDictionary<string,string> headers;
    readonly IReadOnlyDictionary<Signal,Uri> endpoints;
    internal ConfiguredTarget(Signal signals, Dictionary<Signal,Uri> paths, Dictionary<string,string> values) {
        SupportedSignals = signals; endpoints = new ReadOnlyDictionary<Signal, Uri>(new Dictionary<Signal, Uri>(paths)); headers = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(values));
    }
    public Signal SupportedSignals { get; }
    public IReadOnlyDictionary<Signal,Uri> Endpoints => endpoints;
    public override string ToString() => $"ConfiguredTarget(Signals={SupportedSignals}, Headers=[redacted])";
    public ConfigurationError? Configure(Signal signal, OtlpExporterOptions options) {
        if (!endpoints.TryGetValue(signal, out var endpoint)) return new("Unsupported signal");
        options.Protocol = OtlpExportProtocol.HttpProtobuf;
        options.Endpoint = endpoint;
        options.Headers = string.Join(",", headers.Select(x => $"{x.Key}={Uri.EscapeDataString(x.Value)}"));
        return null;
    }
}
public static class Profiles
{
    public static readonly Uri LangfuseCloud = new("https://cloud.langfuse.com/api/public/otel");
    public static readonly Uri LangSmithCloud = new("https://api.smith.langchain.com/otel");
    public static readonly Uri BraintrustCloud = new("https://api.braintrust.dev/otel");
    public static readonly Uri LogfireUs = new("https://logfire-us.pydantic.dev");
    public static async ValueTask<TargetResult> ResolveAsync(TargetOptions target, ISecretSource secrets, Signal requested, CancellationToken ct = default)
    {
        if (target is null || secrets is null) return new ConfigurationError("Target and secret source are required");
        Uri uri; Signal supported; var h = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        async ValueTask<string?> Secret(SecretRef? reference) => reference is null || string.IsNullOrWhiteSpace(reference.Name) ? null : await secrets.GetAsync(reference, ct);
        try {
            switch (target) {
                case TargetOptions.Langfuse x:
                    uri=x.Base; supported=Signal.Traces;
                    var pk=await Secret(x.PublicKey); var sk=await Secret(x.SecretKey);
                    if (!SafeToken(pk) || !SafeToken(sk) || pk!.Contains(':')) return new ConfigurationError("Langfuse credentials unavailable or invalid");
                    h.Add("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(pk + ":" + sk)));
                    h.Add("x-langfuse-ingestion-version", "4"); break;
                case TargetOptions.LangSmith x:
                    uri=x.Base; supported=Signal.Traces;
                    var smith=await Secret(x.ApiKey); if (!SafeToken(smith)) return new ConfigurationError("LangSmith credential unavailable or invalid");
                    h.Add("x-api-key", smith!);
                    switch (x.Address) {
                        case SmithAddress.Project p when SafeHeaderValue(p.Name): h.Add("Langsmith-Project", p.Name); break;
                        case SmithAddress.Agent a when SafeHeaderValue(a.Id) && Enum.IsDefined(a.Environment): h.Add("Langsmith-Agent-Id", a.Id); h.Add("Langsmith-Agent-Environment", a.Environment.ToString().ToLowerInvariant()); break;
                        default: return new ConfigurationError("Invalid LangSmith address");
                    } break;
                case TargetOptions.Braintrust x:
                    uri=x.Base; supported=Signal.Traces|Signal.Logs;
                    var bt=await Secret(x.ApiKey); if (!SafeToken(bt)) return new ConfigurationError("Braintrust credential unavailable or invalid");
                    if (!SafeHeaderValue(x.Parent) || !Enum.IsDefined(x.Kind)) return new ConfigurationError("Invalid Braintrust parent");
                    h.Add("Authorization", "Bearer " + bt); h.Add("x-bt-parent", (x.Kind switch { BraintrustParentKind.ProjectId => "project_id", BraintrustParentKind.ProjectName => "project_name", BraintrustParentKind.ExperimentId => "experiment_id", _ => throw new InvalidOperationException() }) + ":" + x.Parent); break;
                case TargetOptions.Logfire x:
                    uri=x.Base; supported=Signal.Traces|Signal.Metrics|Signal.Logs;
                    var lf=await Secret(x.Token); if (!SafeToken(lf) || !Enum.IsDefined(x.Scheme)) return new ConfigurationError("Logfire credential or scheme invalid");
                    h.Add("Authorization", x.Scheme == LogfireAuthorization.Bearer ? "Bearer " + lf : lf!); break;
                case TargetOptions.Generic x:
                    uri=x.Base; supported=Signal.Traces|Signal.Metrics|Signal.Logs;
                    if (x.HeaderSecrets is null) return new ConfigurationError("Header map required");
                    foreach (var (name, reference) in x.HeaderSecrets) {
                        if (!SafeHeaderName(name) || h.ContainsKey(name)) return new ConfigurationError("Invalid or duplicate header name");
                        var value=await Secret(reference); if (!SafeToken(value)) return new ConfigurationError("Header secret unavailable or invalid"); h.Add(name,value!);
                    } break;
                default: return new ConfigurationError("Unknown target");
            }
            if (uri is null || !uri.IsAbsoluteUri || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) || uri.AbsolutePath.TrimEnd('/').EndsWith("/v1/traces", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.TrimEnd('/').EndsWith("/v1/metrics", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.TrimEnd('/').EndsWith("/v1/logs", StringComparison.OrdinalIgnoreCase)) return new ConfigurationError("Invalid OTLP base URI");
            if (requested == 0 || (requested & ~supported) != 0) return new ConfigurationError("Unsupported signal");
            var paths = new Dictionary<Signal, Uri>();
            foreach (var signal in new[] { Signal.Traces, Signal.Metrics, Signal.Logs }) if ((requested & signal) != 0) paths.Add(signal, new Uri(uri.AbsoluteUri.TrimEnd('/') + "/v1/" + signal.ToString().ToLowerInvariant()));
            return new ConfiguredTarget(supported, paths, h);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return new ConfigurationError("Unable to resolve target configuration"); }
    }
    static bool SafeToken(string? value) => !string.IsNullOrWhiteSpace(value) && value.All(c => c >= 0x20 && c <= 0x7e && c != ',');
    static bool SafeHeaderValue(string? value) => SafeToken(value);
    static bool SafeHeaderName(string? name) => !string.IsNullOrWhiteSpace(name) && !name.Equals("Host",StringComparison.OrdinalIgnoreCase) && !name.Equals("Content-Type",StringComparison.OrdinalIgnoreCase) && name.All(c => char.IsAsciiLetterOrDigit(c) || "!#$&'*+-.^_`|~".Contains(c));
    public static IReadOnlyDictionary<string, string> LangfusePrompt(string name, string? version) {
        var values = new Dictionary<string, string> { ["langfuse.observation.prompt.name"] = name }; if (version is not null) values["langfuse.observation.prompt.version"] = version; return new ReadOnlyDictionary<string,string>(values);
    }
    public static IReadOnlyDictionary<string, string> LangSmithKind(bool isModel) => new ReadOnlyDictionary<string, string>(new Dictionary<string, string> { ["langsmith.span.kind"] = isModel ? "llm" : "chain" });
}
