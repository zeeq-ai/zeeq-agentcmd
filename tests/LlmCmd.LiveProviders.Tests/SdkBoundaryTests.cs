using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.LlmCmd.Providers;

namespace LlmCmd.LiveProviders.Tests;

// These tests use the actual SDK-to-MEAI bridges, but the transport never opens
// a socket. They do not read developer credentials and are safe in normal CI.
[Category("Offline")]
public class SdkBoundaryTests
{
    static LiveSettings OfflineSettings(
        string provider,
        string key = "offline-key-not-a-credential"
    ) =>
        new(
            provider,
            key,
            "synthetic-model",
            modelId: provider == "anthropic" ? "claude-haiku-5-5" : "gpt-6-luna"
        );

    [Test, Arguments("openai"), Arguments("anthropic")]
    public async Task Sdk_response_reaches_per_attempt_capture(string provider)
    {
        var handler = new ScriptedResponse(provider);
        var run = await ProviderCapture.RunAsync(OfflineSettings(provider), handler);
        await CaptureAssertions.CheckAsync(run);
        await Assert.That(handler.Calls).IsEqualTo(1);
        await Assert.That(handler.HasMinimalThinking).IsTrue();
        await Assert.That(handler.OmitsSamplingControls).IsTrue();
        await Assert
            .That(handler.Path)
            .IsEqualTo(provider == "openai" ? "/v1/chat/completions" : "/v1/messages");
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
        try
        {
            await ProviderCapture.RunAsync(OfflineSettings(provider), handler);
        }
        catch
        {
            failed = true;
        }
        await Assert.That(failed).IsTrue();
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    [Test, Arguments("openai"), Arguments("anthropic")]
    public async Task Missing_cache_fields_are_not_native_zero_evidence(string provider)
    {
        var handler = new ScriptedResponse(provider, omitCache: true);
        var run = await ProviderCapture.RunAsync(OfflineSettings(provider), handler);
        await Assert.That(run.ObserverFailures.Count).IsEqualTo(0);
        var nativeCache =
            run.Snapshot.Usage.Find(Dim.Cached)
                ?.Observations.Any(observation => observation.Origin == Origin.Native)
            ?? false;
        await Assert.That(nativeCache).IsFalse();
    }

    [Test, Arguments("openai"), Arguments("anthropic")]
    public async Task Artifacts_exclude_credentials_and_response_text(string provider)
    {
        const string key = "offline-key-not-a-credential";
        var run = await ProviderCapture.RunAsync(
            OfflineSettings(provider, key),
            new ScriptedResponse(provider)
        );
        var json = JsonSerializer.Serialize(run.Snapshot);
        await Assert.That(json.Contains(key, StringComparison.Ordinal)).IsFalse();
        await Assert
            .That(json.Contains("OFFLINE_RESPONSE_TEXT", StringComparison.Ordinal))
            .IsFalse();
    }

    [Test]
    public async Task Azure_v1_uses_configured_host_path_and_bearer_auth()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AzureOpenAI:ApiKey"] = "offline-key-not-a-credential",
                    ["AzureOpenAI:Endpoint"] = "https://example.openai.azure.com",
                    ["AzureOpenAI:Model"] = "deployment-name",
                    ["AzureOpenAI:ModelId"] = "gpt-6-luna",
                }
            )
            .Build();
        var handler = new ScriptedResponse("openai");
        var run = await ProviderCapture.RunAsync(LiveSettings.From(config, "azureopenai"), handler);
        await CaptureAssertions.CheckAsync(run);
        await Assert.That(handler.Host).IsEqualTo("example.openai.azure.com");
        await Assert.That(handler.Path).IsEqualTo("/openai/v1/chat/completions");
        await Assert.That(handler.HasExpectedBearer).IsTrue();
        await Assert.That(handler.HasMinimalThinking).IsTrue();
        await Assert.That(handler.OmitsSamplingControls).IsTrue();
        await Assert.That(run.Snapshot.Usage.Total).IsEqualTo(13L);
        await Assert.That(run.Snapshot.Usage.ValueOf(Dim.Cached)).IsEqualTo(2L);
    }

    [Test]
    public async Task Presence_probe_reports_only_configuration_flags()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AzureOpenAI:ApiKey"] = "synthetic-key",
                    ["AzureOpenAI:Endpoint"] = "https://synthetic.example",
                    ["Anthropic:Model"] = "synthetic-model",
                }
            )
            .Build();
        var flags = SettingsPresence.Read(config);
        await Assert.That(flags["Azure key"]).IsTrue();
        await Assert.That(flags["Azure endpoint"]).IsTrue();
        await Assert.That(flags["Azure deployment"]).IsFalse();
        await Assert.That(flags["Anthropic key"]).IsFalse();
        await Assert.That(flags["Anthropic model"]).IsTrue();
        await Assert.That(flags.Count).IsEqualTo(5);
    }

    [Test]
    public async Task Azure_flat_environment_settings_override_nested_settings()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AzureOpenAI:ApiKey"] = "offline-store-key",
                    ["AZURE_OPENAI_API_KEY"] = "offline-ci-key",
                    ["AzureOpenAI:Endpoint"] = "https://store.example",
                    ["AZURE_OPENAI_ENDPOINT"] = "https://ci.example/openai/v1/",
                    ["AzureOpenAI:Model"] = "store-deployment",
                    ["AZURE_OPENAI_MODEL"] = "ci-deployment",
                }
            )
            .Build();
        var settings = LiveSettings.From(config, "azureopenai");
        await Assert.That(settings.ApiKey == "offline-ci-key").IsTrue();
        await Assert.That(settings.Endpoint?.Host).IsEqualTo("ci.example");
        await Assert.That(settings.Model).IsEqualTo("ci-deployment");
        await Assert
            .That(settings.ToString().Contains("offline-ci-key", StringComparison.Ordinal))
            .IsFalse();
    }

    [Test]
    public async Task Azure_requires_endpoint_before_request()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AzureOpenAI:ApiKey"] = "offline-key-not-a-credential",
                }
            )
            .Build();
        var rejected = false;
        try
        {
            LiveSettings.From(config, "azureopenai");
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        await Assert.That(rejected).IsTrue();
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
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["OpenAI:ApiKey"] = "offline-user-secret",
                    ["OPENAI_API_KEY"] = "offline-ci-secret",
                    ["OpenAI:Model"] = "from-store",
                    ["OPENAI_MODEL"] = "from-ci",
                }
            )
            .Build();
        var settings = LiveSettings.From(configuration, "openai");
        await Assert.That(settings.ApiKey == "offline-ci-secret").IsTrue();
        await Assert.That(settings.Model).IsEqualTo("from-ci");
        await Assert
            .That(!settings.ToString().Contains("offline-ci-secret", StringComparison.Ordinal))
            .IsTrue();
    }

    [Test]
    public async Task Missing_key_is_a_configuration_failure()
    {
        var rejected = false;
        try
        {
            LiveSettings.From(new ConfigurationBuilder().Build(), "anthropic");
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        await Assert.That(rejected).IsTrue();
    }

    private sealed class ScriptedResponse(
        string provider,
        HttpStatusCode status = HttpStatusCode.OK,
        bool omitCache = false
    ) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal string? Path { get; private set; }
        internal string? Host { get; private set; }
        internal bool HasExpectedBearer { get; private set; }
        internal bool HasMinimalThinking { get; private set; }
        internal bool OmitsSamplingControls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Calls++;
            Path = request.RequestUri?.AbsolutePath;
            Host = request.RequestUri?.Host;
            HasExpectedBearer =
                request.Headers.Authorization?.Scheme == "Bearer"
                && request.Headers.Authorization.Parameter == "offline-key-not-a-credential";
            var sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            HasMinimalThinking =
                provider == "openai"
                    ? sent["reasoning_effort"]?.GetValue<string>() == "none"
                    : sent["thinking"]?["type"]?.GetValue<string>() == "disabled";
            OmitsSamplingControls =
                sent["temperature"] is null && sent["top_p"] is null && sent["top_k"] is null;
            var body = JsonNode.Parse(provider == "openai" ? OpenAi : Anthropic)!;
            if (omitCache)
            {
                if (provider == "openai")
                {
                    body["usage"]!.AsObject().Remove("prompt_tokens_details");
                }
                else
                {
                    body["usage"]!.AsObject().Remove("cache_read_input_tokens");
                    body["usage"]!.AsObject().Remove("cache_creation_input_tokens");
                }
            }
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    status != HttpStatusCode.OK ? "{}" : body.ToJsonString(),
                    Encoding.UTF8,
                    "application/json"
                ),
            };
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
