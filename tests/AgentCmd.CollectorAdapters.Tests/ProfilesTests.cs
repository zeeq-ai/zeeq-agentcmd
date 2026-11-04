using System.Diagnostics;
using System.Net;
using System.Text;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd.OpenTelemetry;

namespace AgentCmd.CollectorAdapters.Tests;

public class ProfilesTests
{
    static readonly SecretRef Key = new("key"),
        Pub = new("public"),
        Missing = new("missing");
    static readonly ISecretSource Secrets = new FakeSecrets();

    static Uri Local(string prefix) => new("http://localhost:8087/" + prefix);

    static async Task<ConfiguredTarget> Resolve(
        TargetOptions options,
        Signal signals = Signal.Traces
    )
    {
        var result = await Profiles.ResolveAsync(options, Secrets, signals);
        return result switch
        {
            ConfiguredTarget c => c,
            ConfigurationError e => throw new Exception(e.Message),
            null => throw new Exception("Uninitialized result"),
        };
    }

    static async Task<(
        Uri Uri,
        string MediaType,
        byte[] Body,
        IReadOnlyDictionary<string, string> Headers
    )> Send(ConfiguredTarget target)
    {
        var handler = new CaptureHandler();
        using var provider = Sdk.CreateTracerProviderBuilder()
            .SetSampler(new AlwaysOnSampler())
            .AddSource("adapter.tests")
            .AddOtlpExporter(o =>
            {
                var error = target.Configure(Signal.Traces, o);
                if (error is not null)
                {
                    throw new Exception(error.Message);
                }

                o.HttpClientFactory = () => new HttpClient(handler, disposeHandler: false);
                o.ExportProcessorType = ExportProcessorType.Simple;
            })
            .Build();
        using var source = new ActivitySource("adapter.tests");
        using (var activity = source.StartActivity("test"))
        {
            activity?.SetTag("test", "value");
        }

        provider.ForceFlush();
        return await handler.Result.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task Langfuse_real_exporter_request()
    {
        var target = await Resolve(new TargetOptions.Langfuse(Local("api/public/otel"), Pub, Key));
        var request = await Send(target);
        await Assert
            .That(request.Uri.AbsoluteUri)
            .IsEqualTo("http://localhost:8087/api/public/otel/v1/traces");
        await Assert
            .That(request.Headers["Authorization"])
            .IsEqualTo(
                "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("fake-public:fake-key"))
            );
        await Assert.That(request.Headers["x-langfuse-ingestion-version"]).IsEqualTo("4");
        await Assert.That(request.MediaType).IsEqualTo("application/x-protobuf");
        await Assert.That(request.Body.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task LangSmith_project_real_exporter_request()
    {
        var request = await Send(
            await Resolve(
                new TargetOptions.LangSmith(
                    Local("otel"),
                    Key,
                    new SmithAddress.Project("eval project")
                )
            )
        );
        await Assert.That(request.Uri.AbsolutePath).IsEqualTo("/otel/v1/traces");
        await Assert.That(request.Headers["x-api-key"]).IsEqualTo("fake-key");
        await Assert.That(request.Headers["Langsmith-Project"]).IsEqualTo("eval project");
        await Assert.That(request.Headers.ContainsKey("Langsmith-Agent-Id")).IsFalse();
        await Assert.That(request.MediaType).IsEqualTo("application/x-protobuf");
        await Assert.That(request.Body.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task LangSmith_agent_self_hosted_real_exporter_request()
    {
        var request = await Send(
            await Resolve(
                new TargetOptions.LangSmith(
                    Local("api/v1/otel"),
                    Key,
                    new SmithAddress.Agent("agent-1", AgentEnvironment.Staging)
                )
            )
        );
        await Assert.That(request.Uri.AbsolutePath).IsEqualTo("/api/v1/otel/v1/traces");
        await Assert.That(request.Headers["Langsmith-Agent-Id"]).IsEqualTo("agent-1");
        await Assert.That(request.Headers["Langsmith-Agent-Environment"]).IsEqualTo("staging");
        await Assert.That(request.Headers.ContainsKey("Langsmith-Project")).IsFalse();
        await Assert.That(request.MediaType).IsEqualTo("application/x-protobuf");
        await Assert.That(request.Body.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Braintrust_real_exporter_request()
    {
        var target = await Resolve(
            new TargetOptions.Braintrust(
                Local("otel"),
                Key,
                BraintrustParentKind.ProjectName,
                "project"
            ),
            Signal.Traces | Signal.Logs
        );
        var request = await Send(target);
        await Assert.That(request.Uri.AbsolutePath).IsEqualTo("/otel/v1/traces");
        await Assert.That(request.Headers["Authorization"]).IsEqualTo("Bearer fake-key");
        await Assert.That(request.Headers["x-bt-parent"]).IsEqualTo("project_name:project");
        await Assert.That(target.Endpoints[Signal.Logs].AbsolutePath).IsEqualTo("/otel/v1/logs");
        await Assert.That(request.MediaType).IsEqualTo("application/x-protobuf");
        await Assert.That(request.Body.Length).IsGreaterThan(0);
    }

    [Test]
    [Arguments(LogfireAuthorization.Raw, "fake-key")]
    [Arguments(LogfireAuthorization.Bearer, "Bearer fake-key")]
    public async Task Logfire_real_exporter_request(LogfireAuthorization scheme, string expected)
    {
        var target = await Resolve(
            new TargetOptions.Logfire(Local(""), Key, scheme),
            Signal.Traces | Signal.Metrics | Signal.Logs
        );
        var request = await Send(target);
        await Assert.That(request.Uri.AbsolutePath).IsEqualTo("/v1/traces");
        await Assert.That(request.Headers["Authorization"]).IsEqualTo(expected);
        await Assert.That(target.Endpoints[Signal.Metrics].AbsolutePath).IsEqualTo("/v1/metrics");
        await Assert.That(target.Endpoints[Signal.Logs].AbsolutePath).IsEqualTo("/v1/logs");
        await Assert.That(request.MediaType).IsEqualTo("application/x-protobuf");
        await Assert.That(request.Body.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Generic_prefix_and_escaped_header_real_exporter_request()
    {
        var request = await Send(
            await Resolve(
                new TargetOptions.Generic(
                    Local("proxy/prefix"),
                    new Dictionary<string, SecretRef> { ["x-custom"] = Key }
                )
            )
        );
        await Assert.That(request.Uri.AbsolutePath).IsEqualTo("/proxy/prefix/v1/traces");
        await Assert.That(request.Headers["x-custom"]).IsEqualTo("fake-key");
        await Assert.That(request.MediaType).IsEqualTo("application/x-protobuf");
        await Assert.That(request.Body.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Escaped_secret_header_round_trips_through_actual_exporter()
    {
        var request = await Send(
            await Resolve(
                new TargetOptions.Generic(
                    Local("prefix"),
                    new Dictionary<string, SecretRef> { ["x-custom"] = new("special") }
                )
            )
        );
        await Assert.That(request.Headers["x-custom"]).IsEqualTo("fake=percent% space");
    }

    [Test]
    public async Task Comma_in_secret_is_rejected_before_export()
    {
        var result = await Profiles.ResolveAsync(
            new TargetOptions.Generic(
                Local("prefix"),
                new Dictionary<string, SecretRef> { ["x-custom"] = new("injection") }
            ),
            Secrets,
            Signal.Traces
        );
        var sent = false;
        if (result is ConfiguredTarget c)
        {
            await Send(c);
            sent = true;
        }
        await Assert.That(result is ConfigurationError).IsTrue();
        await Assert.That(sent).IsFalse();
    }

    [Test]
    public async Task Escaped_header_name_is_rejected_before_export()
    {
        var result = await Profiles.ResolveAsync(
            new TargetOptions.Generic(
                Local("prefix"),
                new Dictionary<string, SecretRef> { ["x%3dfoo%2cInjected"] = Key }
            ),
            Secrets,
            Signal.Traces
        );
        var sent = false;
        if (result is ConfiguredTarget c)
        {
            await Send(c);
            sent = true;
        }
        await Assert.That(result is ConfigurationError).IsTrue();
        await Assert.That(sent).IsFalse();
    }

    [Test]
    public async Task Comma_in_routing_values_is_rejected_before_export()
    {
        var cases = new TargetOptions[]
        {
            new TargetOptions.LangSmith(
                Local("otel"),
                Key,
                new SmithAddress.Project("safe,Injected=evil")
            ),
            new TargetOptions.LangSmith(
                Local("otel"),
                Key,
                new SmithAddress.Agent("safe,Injected=evil", AgentEnvironment.Local)
            ),
            new TargetOptions.Braintrust(
                Local("otel"),
                Key,
                BraintrustParentKind.ProjectName,
                "safe,Injected=evil"
            ),
        };
        foreach (var target in cases)
        {
            var result = await Profiles.ResolveAsync(target, Secrets, Signal.Traces);
            var sent = false;
            if (result is ConfiguredTarget c)
            {
                await Send(c);
                sent = true;
            }
            await Assert.That(result is ConfigurationError).IsTrue();
            await Assert.That(sent).IsFalse();
        }
    }

    [Test]
    public async Task Rejects_unsupported_missing_and_invalid_configuration()
    {
        var denied = await Profiles.ResolveAsync(
            new TargetOptions.Langfuse(Local("otel"), Pub, Key),
            Secrets,
            Signal.Metrics
        );
        await Assert.That(denied is ConfigurationError).IsTrue();
        var missing = await Profiles.ResolveAsync(
            new TargetOptions.LangSmith(
                Local("otel"),
                Missing,
                new SmithAddress.Project("project")
            ),
            Secrets,
            Signal.Traces
        );
        await Assert.That(missing is ConfigurationError).IsTrue();
        var invalidAddress = await Profiles.ResolveAsync(
            new TargetOptions.LangSmith(
                Local("otel"),
                Key,
                new SmithAddress.Agent("agent", (AgentEnvironment)42)
            ),
            Secrets,
            Signal.Traces
        );
        await Assert.That(invalidAddress is ConfigurationError).IsTrue();
        var badParent = await Profiles.ResolveAsync(
            new TargetOptions.Braintrust(Local("otel"), Key, BraintrustParentKind.ProjectName, ""),
            Secrets,
            Signal.Traces
        );
        await Assert.That(badParent is ConfigurationError).IsTrue();
        var badUri = await Profiles.ResolveAsync(
            new TargetOptions.Logfire(new Uri("http://example.com"), Key),
            Secrets,
            Signal.Traces
        );
        await Assert.That(badUri is ConfigurationError).IsTrue();
        var badHeader = await Profiles.ResolveAsync(
            new TargetOptions.Generic(
                Local("otel"),
                new Dictionary<string, SecretRef> { ["evil\r\nheader"] = Key }
            ),
            Secrets,
            Signal.Traces
        );
        await Assert.That(badHeader is ConfigurationError).IsTrue();
        var duplicatePath = await Profiles.ResolveAsync(
            new TargetOptions.Logfire(Local("v1/traces"), Key),
            Secrets,
            Signal.Traces
        );
        await Assert.That(duplicatePath is ConfigurationError).IsTrue();
    }

    [Test]
    public async Task No_secret_leaks_through_result_diagnostics_and_default_union_is_explicit()
    {
        var target = await Resolve(new TargetOptions.Langfuse(Local("otel"), Pub, Key));
        await Assert.That(target.ToString().Contains("fake-key")).IsFalse();
        TargetResult missing = default;
        var description = missing switch
        {
            ConfiguredTarget => "configured",
            ConfigurationError => "error",
            null => "uninitialized",
        };
        await Assert.That(description).IsEqualTo("uninitialized");
        var error = target.Configure(Signal.Metrics, new OtlpExporterOptions());
        await Assert.That(error!.Message).IsEqualTo("Unsupported signal");
    }

    [Test]
    public async Task Vendor_mappings_use_explicit_names()
    {
        var mapping = Profiles.LangfusePrompt("prompt", "rev-1");
        await Assert.That(mapping["langfuse.observation.prompt.name"]).IsEqualTo("prompt");
        await Assert.That(mapping["langfuse.observation.prompt.version"]).IsEqualTo("rev-1");
        await Assert.That(Profiles.LangSmithKind(true)["langsmith.span.kind"]).IsEqualTo("llm");
        await Assert.That(Profiles.LangSmithKind(false)["langsmith.span.kind"]).IsEqualTo("chain");
    }
}

sealed class FakeSecrets : ISecretSource
{
    public ValueTask<string?> GetAsync(SecretRef reference, CancellationToken ct = default) =>
        ValueTask.FromResult(
            reference.Name switch
            {
                "key" => "fake-key",
                "public" => "fake-public",
                "special" => "fake=percent% space",
                "injection" => "fake,Injected=evil",
                _ => null,
            }
        );
}

sealed class CaptureHandler : HttpMessageHandler
{
    public TaskCompletionSource<(Uri, string, byte[], IReadOnlyDictionary<string, string>)> Result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct) =>
        Capture(request, request.Content!.ReadAsByteArrayAsync(ct).GetAwaiter().GetResult());

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken ct
    ) => Capture(request, await request.Content!.ReadAsByteArrayAsync(ct));

    HttpResponseMessage Capture(HttpRequestMessage request, byte[] body)
    {
        var h = request.Headers.ToDictionary(
            x => x.Key,
            x => string.Join(",", x.Value),
            StringComparer.OrdinalIgnoreCase
        );
        Result.TrySetResult(
            (request.RequestUri!, request.Content!.Headers.ContentType!.MediaType!, body, h)
        );
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };
    }
}
