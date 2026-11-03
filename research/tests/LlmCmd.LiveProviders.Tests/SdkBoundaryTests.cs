using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;
using ProviderAdapters;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LlmCmd.LiveProviders.Tests;

// These tests use the actual SDK-to-MEAI bridges, but the transport never opens
// a socket. They do not read developer credentials and are safe in normal CI.
[Category("Offline")]
public class SdkBoundaryTests
{
    [Test, Arguments("openai"), Arguments("anthropic")]
    public async Task Sdk_response_reaches_per_attempt_capture(string provider)
    {
        var handler = new ScriptedResponse(provider);
        var run = await ProviderCapture.RunAsync(new(provider, "offline-key-not-a-credential", "synthetic-model"), handler);
        await CaptureAssertions.CheckAsync(run);
        await Assert.That(handler.Calls).IsEqualTo(1);
        await Assert.That(handler.Path).IsEqualTo(provider == "openai" ? "/v1/chat/completions" : "/v1/messages");
        await Assert.That(run.Snapshot.Usage.Input).IsEqualTo(10L);
        await Assert.That(run.Snapshot.Usage.Output).IsEqualTo(3L);
        await Assert.That(run.Snapshot.Usage.Total).IsEqualTo(13L);
        await Assert.That(run.Snapshot.Usage.ValueOf(Dim.Cached)).IsEqualTo(2L);
    }

    [Test, Arguments("openai"), Arguments("anthropic")]
    public async Task Sdk_retries_are_disabled(string provider)
    {
        var handler = new ScriptedResponse(provider, HttpStatusCode.ServiceUnavailable);
        var failed = false;
        try { await ProviderCapture.RunAsync(new(provider, "offline-key-not-a-credential", "synthetic-model"), handler); }
        catch { failed = true; }
        await Assert.That(failed).IsTrue();
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    [Test, Arguments("openai"), Arguments("anthropic")]
    public async Task Missing_cache_fields_are_not_native_zero_evidence(string provider)
    {
        var handler = new ScriptedResponse(provider, omitCache: true);
        var run = await ProviderCapture.RunAsync(new(provider, "offline-key-not-a-credential", "synthetic-model"), handler);
        await Assert.That(run.ObserverFailures.Count).IsEqualTo(0);
        var nativeCache = run.Snapshot.Usage.Find(Dim.Cached)?.Observations
            .Any(observation => observation.Origin == Origin.Native) ?? false;
        await Assert.That(nativeCache).IsFalse();
    }

    [Test, Arguments("openai"), Arguments("anthropic")]
    public async Task Artifacts_exclude_credentials_and_response_text(string provider)
    {
        const string key = "offline-key-not-a-credential";
        var run = await ProviderCapture.RunAsync(new(provider, key, "synthetic-model"), new ScriptedResponse(provider));
        var json = JsonSerializer.Serialize(run.Snapshot);
        await Assert.That(json.Contains(key, StringComparison.Ordinal)).IsFalse();
        await Assert.That(json.Contains("OFFLINE_RESPONSE_TEXT", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task Test_assembly_owns_the_stable_user_secrets_store()
    {
        var attribute = typeof(LiveSettings).Assembly.GetCustomAttribute<UserSecretsIdAttribute>();
        await Assert.That(attribute?.UserSecretsId).IsEqualTo("zeeq-llmcmd-live-tests");
    }

    [Test]
    public async Task Configuration_accepts_user_secret_names_and_ci_overrides()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenAI:ApiKey"] = "offline-user-secret",
            ["OPENAI_API_KEY"] = "offline-ci-secret",
            ["OpenAI:Model"] = "from-store",
            ["OPENAI_MODEL"] = "from-ci"
        }).Build();
        var settings = LiveSettings.From(configuration, "openai");
        await Assert.That(settings.ApiKey == "offline-ci-secret").IsTrue();
        await Assert.That(settings.Model).IsEqualTo("from-ci");
        await Assert.That(!settings.ToString().Contains("offline-ci-secret", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Missing_key_is_a_configuration_failure()
    {
        var rejected = false;
        try { LiveSettings.From(new ConfigurationBuilder().Build(), "anthropic"); }
        catch (InvalidOperationException) { rejected = true; }
        await Assert.That(rejected).IsTrue();
    }

    private sealed class ScriptedResponse(string provider, HttpStatusCode status = HttpStatusCode.OK, bool omitCache = false) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal string? Path { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Path = request.RequestUri?.AbsolutePath;
            var body = JsonNode.Parse(provider == "openai" ? OpenAi : Anthropic)!;
            if (omitCache)
            {
                if (provider == "openai") body["usage"]!.AsObject().Remove("prompt_tokens_details");
                else
                {
                    body["usage"]!.AsObject().Remove("cache_read_input_tokens");
                    body["usage"]!.AsObject().Remove("cache_creation_input_tokens");
                }
            }
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(status != HttpStatusCode.OK ? "{}" : body.ToJsonString(),
                    Encoding.UTF8, "application/json")
            });
        }
    }

    // Hand-written responses, not live evidence. Unlike the older fixture
    // suite, these JSON bodies are deserialized by the real pinned SDKs first.
    private const string OpenAi = """
        {"id":"chatcmpl-offline","object":"chat.completion","created":1700000000,"model":"synthetic-model",
         "choices":[{"index":0,"message":{"role":"assistant","content":"OFFLINE_RESPONSE_TEXT"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":10,"completion_tokens":3,"total_tokens":13,
                  "prompt_tokens_details":{"cached_tokens":2},"completion_tokens_details":{"reasoning_tokens":0}}}
        """;
    private const string Anthropic = """
        {"id":"msg_offline","type":"message","role":"assistant","model":"synthetic-model",
         "content":[{"type":"text","text":"OFFLINE_RESPONSE_TEXT"}],"stop_reason":"end_turn","stop_sequence":null,
         "usage":{"input_tokens":7,"output_tokens":3,"cache_read_input_tokens":2,"cache_creation_input_tokens":1}}
        """;
}
