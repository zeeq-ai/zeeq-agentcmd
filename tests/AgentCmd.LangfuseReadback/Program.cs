using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;
using Zeeq.AgentCmd.OpenTelemetry;
using Zeeq.AgentCmd.Providers;
using Zeeq.AgentCmd.Providers.Pricing;

if (args is ["start"] or ["stop"])
{
    return await LocalLangfuse.RunAsync(args[0]);
}

if (args is ["serve"])
{
    var startResult = await LocalLangfuse.RunAsync("start", waitForHealth: false);
    if (startResult != 0)
    {
        return startResult;
    }
    // Keep the Aspire resource alive while its health check observes the Compose stack.
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}
var crossCheck = args.Length == 2 && args[0] == "--cross-check";
if (crossCheck)
{
    args = [args[1]];
}
else if (args.Contains("--cross-check"))
{
    Console.Error.WriteLine("Provide --cross-check followed by one capture path.");
    return 1;
}
if (OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("This local test requires a Unix host.");
    return 1;
}

// Only this dedicated test instance's private generated state is read.
var state =
    Environment.GetEnvironmentVariable("AGENTCMD_LANGFUSE_STATE") ?? "/tmp/zeeq-llmcmd-langfuse";
var stateFile = Path.Combine(state, ".env");
if (
    !File.Exists(stateFile)
    || new FileInfo(stateFile).LinkTarget is not null
    || new DirectoryInfo(state).LinkTarget is not null
    || (
        File.GetUnixFileMode(stateFile)
        & (
            UnixFileMode.GroupRead
            | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherWrite
            | UnixFileMode.OtherExecute
        )
    ) != 0
)
{
    Console.Error.WriteLine("Private local test state is required.");
    return 1;
}
var entries = File.ReadLines(stateFile)
    .Where(x =>
        x.StartsWith("LANGFUSE_INIT_PROJECT_PUBLIC_KEY=", StringComparison.Ordinal)
        || x.StartsWith("LANGFUSE_INIT_PROJECT_SECRET_KEY=", StringComparison.Ordinal)
    )
    .Select(x => x.Split('=', 2))
    .ToDictionary(x => x[0], x => x[1]);
var key = entries.GetValueOrDefault("LANGFUSE_INIT_PROJECT_PUBLIC_KEY");
var secret = entries.GetValueOrDefault("LANGFUSE_INIT_PROJECT_SECRET_KEY");
if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(secret))
{
    Console.Error.WriteLine("Local test credentials are required.");
    return 1;
}
var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(key + ":" + secret));
var baseUrl =
    Environment.GetEnvironmentVariable("AGENTCMD_LANGFUSE_BASE_URL") ?? "http://127.0.0.1:38941";
if (
    !Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint)
    || endpoint.Scheme != Uri.UriSchemeHttp
    || !IPAddress.TryParse(endpoint.Host, out var address)
    || !IPAddress.IsLoopback(address)
    || endpoint.AbsolutePath != "/"
    || endpoint.UserInfo.Length != 0
    || endpoint.Query.Length != 0
    || endpoint.Fragment.Length != 0
)
{
    Console.Error.WriteLine("The test target must be a loopback HTTP origin.");
    return 1;
}
baseUrl = endpoint.GetLeftPart(UriPartial.Authority);
var resolved = await Profiles.ResolveAsync(
    new TargetOptions.Langfuse(new Uri(baseUrl + "/api/public/otel"), new("public"), new("secret")),
    new LocalSecrets(key, secret),
    Signal.Traces
);
if (resolved is not ConfiguredTarget target)
{
    Console.Error.WriteLine("Local target configuration failed.");
    return 1;
}
var handler = new StatusHandler();
using var provider = Sdk.CreateTracerProviderBuilder()
    .SetSampler(new AlwaysOnSampler())
    .AddAgentCmdInstrumentation()
    .AddOtlpExporter(o =>
    {
        if (target.Configure(Signal.Traces, o) is not null)
        {
            throw new InvalidOperationException("Target configuration failed.");
        }

        o.HttpClientFactory = () => new HttpClient(handler, false);
        o.ExportProcessorType = ExportProcessorType.Simple;
    })
    .Build();
var usage = new UsageSnapshot(
    [
        new(Dim.Input, 12, Unit.Tokens, Relation.Total, null, MeasurementStatus.Agreed, []),
        new(Dim.Output, 3, Unit.Tokens, Relation.Total, null, MeasurementStatus.Agreed, []),
    ],
    []
);
var snapshot = new ResponseSnapshot(
    "readback-test",
    new("synthetic", "test", ApiKind.ChatCompletions),
    "test",
    null,
    null,
    "test",
    false,
    AttemptEnd.Completed,
    null,
    "zeeq-synthetic-model",
    null,
    null,
    usage,
    new(0, 0, 0, 0),
    null,
    []
);
var now = DateTimeOffset.UtcNow;
var started = now.AddSeconds(-1);
PricingEstimate? estimate = new(PricingStatus.Complete, 0.001m, 0.001m, "synthetic", null);
if (args.Length == 1)
{
    using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(args[0]));
    var root = capture.RootElement;
    var s = root.GetProperty("Snapshot");
    var r = s.GetProperty("Registration");
    string? Optional(string name) =>
        s.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.String
            ? x.GetString()
            : null;
    usage =
        s.GetProperty("Usage").Deserialize<UsageSnapshot>()
        ?? throw new InvalidDataException("Missing usage.");
    var diagnostics = s.GetProperty("Diagnostics").Deserialize<List<Diagnostic>>() ?? [];
    snapshot = new(
        s.GetProperty("AttemptId").GetString()!,
        new(
            r.GetProperty("ProviderKey").GetString()!,
            r.GetProperty("ClientRegistrationId").GetString()!,
            (ApiKind)r.GetProperty("Api").GetInt32()
        ),
        s.GetProperty("AdapterId").GetString()!,
        Optional("AdapterVersion"),
        Optional("RulesRevision"),
        s.GetProperty("MeaiVersion").GetString()!,
        s.GetProperty("IsStreaming").GetBoolean(),
        (AttemptEnd)s.GetProperty("End").GetInt32(),
        Optional("ResponseId"),
        Optional("ModelId"),
        Optional("FinishReason"),
        Optional("NativeStopReason"),
        usage,
        new(0, 0, 0, 0),
        null,
        diagnostics
    )
    {
        RequestedModelId = Optional("RequestedModelId"),
    };
    if (
        s.TryGetProperty("BillingContext", out var billing)
        && billing.ValueKind == JsonValueKind.Object
    )
    {
        snapshot = snapshot with { BillingContext = billing.Deserialize<ProviderBillingContext>() };
    }

    started = root.GetProperty("StartedAt").GetDateTimeOffset();
    now = root.GetProperty("EndedAt").GetDateTimeOffset();
    var scope =
        root.TryGetProperty("PricingScope", out var recordedScope)
        && recordedScope.ValueKind == JsonValueKind.Object
            ? recordedScope.Deserialize<PricingScope>()
            : null;
    scope ??= snapshot.Registration.ProviderKey switch
    {
        "azureopenai" => new("azure-openai-global-standard", "standard", "global", "short"),
        "anthropic" => new("anthropic-direct", "standard", "global"),
        _ => null,
    };
    if (scope is not null)
    {
        // Reprice at the stored request date. Older matrix receipts declared the
        // same binding in these two fields, before it became a shared value type.
        if (
            scope.TargetBinding is null
            && snapshot.RequestedModelId is not null
            && root.TryGetProperty("ExpectedModel", out var expected)
            && expected.ValueKind == JsonValueKind.String
            && root.TryGetProperty("RequestedTarget", out var capturedTarget)
            && capturedTarget.ValueKind == JsonValueKind.String
        )
        {
            scope = scope with
            {
                TargetBinding = ModelRequestCatalog.Default.Bind(
                    snapshot.Registration.ProviderKey,
                    snapshot.Registration.Api,
                    capturedTarget.GetString()!,
                    expected.GetString()!
                ),
            };
        }
        estimate = PricingEstimator.Estimate(
            snapshot,
            scope with
            {
                AsOf = DateOnly.FromDateTime(started.UtcDateTime),
            }
        );
    }
    else
    {
        estimate = null;
    }
}
else if (args.Length != 0)
{
    Console.Error.WriteLine("Provide one capture path, or no path for the synthetic case.");
    return 1;
}
var traceId = ActivityTraceId.CreateRandom();
var parent = new ActivityContext(
    traceId,
    ActivitySpanId.CreateRandom(),
    ActivityTraceFlags.Recorded
);
if (crossCheck)
{
    using var comparisonClient = new HttpClient(
        new SocketsHttpHandler { AllowAutoRedirect = false }
    )
    {
        BaseAddress = new Uri(baseUrl),
        Timeout = TimeSpan.FromSeconds(5),
    };
    comparisonClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
        "Basic",
        auth
    );
    return await PricingCrossCheck.RunAsync(
        comparisonClient,
        snapshot,
        estimate,
        parent,
        started,
        now
    );
}
LangfuseGeneration.Emit(snapshot, parent, started, now, cost: estimate);
provider.ForceFlush();
if (!handler.Accepted)
{
    Console.Error.WriteLine("OTLP request was not accepted.");
    return 2;
}
using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
{
    Timeout = TimeSpan.FromSeconds(5),
};
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
for (var i = 0; i < 60; i++)
{
    using var response = await client.GetAsync(
        baseUrl + "/api/public/v2/observations?fields=model,usage,io&traceId=" + traceId
    );
    if (response.IsSuccessStatusCode)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (
            document.RootElement.TryGetProperty("data", out var data)
            && data.EnumerateArray()
                .Any(o =>
                    o.TryGetProperty("type", out var type)
                    && type.GetString()?.Equals("GENERATION", StringComparison.OrdinalIgnoreCase)
                        == true
                    && o.TryGetProperty("traceId", out var observedTrace)
                    && observedTrace.GetString() == traceId.ToString()
                    && (
                        !o.TryGetProperty("input", out var obsInput)
                        || obsInput.ValueKind == JsonValueKind.Null
                    )
                    && (
                        !o.TryGetProperty("output", out var obsOutput)
                        || obsOutput.ValueKind == JsonValueKind.Null
                    )
                    && o.TryGetProperty("model", out var model)
                    && model.GetString() == snapshot.ModelId
                    && o.TryGetProperty("usageDetails", out var u)
                    && new[] { Dim.Input, Dim.Output, Dim.Total }.All(d =>
                    {
                        var m = usage.Find(d);
                        return m is null
                            || m.Status != MeasurementStatus.Agreed
                            || usage.HasConflict(d)
                            || u.TryGetProperty(d, out var n) && n.GetInt64() == m.Value;
                    })
                    && (
                        estimate is not { Status: PricingStatus.Complete, TotalUsd: not null }
                        || o.TryGetProperty("costDetails", out var c)
                            && c.TryGetProperty("total", out var total)
                            && total.GetDecimal() == estimate.TotalUsd
                    )
                )
        )
        {
            Console.WriteLine(
                estimate is { Status: PricingStatus.Complete, TotalUsd: not null }
                    ? "OTLP accepted; generation model, usage and explicit cost read back."
                    : "OTLP accepted; generation model and usage read back; no complete cost estimate supplied."
            );
            return 0;
        }
    }
    await Task.Delay(2000);
}
Console.Error.WriteLine("Generation readback did not match within the timeout.");
return 3;

sealed class StatusHandler : DelegatingHandler
{
    public bool Accepted { get; private set; }

    public StatusHandler()
        : base(new HttpClientHandler { AllowAutoRedirect = false }) { }

    protected override HttpResponseMessage Send(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var r = base.Send(request, cancellationToken);
        Accepted = r.IsSuccessStatusCode;
        return r;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var r = await base.SendAsync(request, cancellationToken);
        Accepted = r.IsSuccessStatusCode;
        return r;
    }
}

sealed class LocalSecrets(string publicKey, string secretKey) : ISecretSource
{
    public ValueTask<string?> GetAsync(SecretRef reference, CancellationToken ct = default) =>
        ValueTask.FromResult<string?>(
            reference.Name switch
            {
                "public" => publicKey,
                "secret" => secretKey,
                _ => null,
            }
        );
}
