using Microsoft.Extensions.Configuration;

namespace AgentCmd.LiveProviders.Tests;

// UserSecretsId belongs to this test assembly. No AppHost or working-directory
// convention is needed; the same command works from an IDE, dotnet or mise.
internal sealed class LiveSettings(
    string provider,
    string apiKey,
    string model,
    Uri? endpoint = null,
    string? modelId = null
)
{
    internal string Provider { get; } = provider;
    internal string ApiKey { get; } = apiKey;
    internal string Model { get; } = model;
    internal Uri? Endpoint { get; } = endpoint;

    // On Azure, Model is a deployment target. This separate value is the host's
    // explicit claim about the underlying model, not a response-name guess.
    internal string UnderlyingModel { get; } = modelId ?? model;

    internal LiveSettings WithTarget(string target, string underlyingModel) =>
        new(Provider, ApiKey, target, Endpoint, underlyingModel);

    internal static LiveSettings Load(string provider)
    {
        // [Explicit] protects ordinary discovery. This second gate also protects
        // broad IDE filters. Only the dedicated live tasks set this opt-in.
        RequireLiveOptIn(Environment.GetEnvironmentVariable("AGENTCMD_RUN_LIVE_TESTS"));

        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<LiveSettings>(optional: true)
            .AddEnvironmentVariables()
            .Build();
        using var lifetime = configuration as IDisposable;
        return From(configuration, provider);
    }

    // Test the gate without touching process-wide environment or user secrets.
    internal static void RequireLiveOptIn(string? value)
    {
        if (value != "1")
        {
            throw new InvalidOperationException(
                "Set AGENTCMD_RUN_LIVE_TESTS=1 and explicitly select a live test to opt into paid API calls."
            );
        }
    }

    internal static LiveSettings From(IConfiguration configuration, string provider)
    {
        var (section, environment, fallback) = provider switch
        {
            "openai" => ("OpenAI", "OPENAI", "gpt-6-luna"),
            "azureopenai" => ("AzureOpenAI", "AZURE_OPENAI", "gpt-6-luna"),
            "anthropic" => ("Anthropic", "ANTHROPIC", "claude-haiku-5-5"),
            _ => throw new ArgumentException("Unknown live-test provider", nameof(provider)),
        };
        // Both forms support CI. Flat SDK environment variables win over the
        // developer secret; nested OPENAI__APIKEY also overrides via config.
        var key = configuration[$"{environment}_API_KEY"] ?? configuration[$"{section}:ApiKey"];
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                $"Missing {section}:ApiKey in this test project's user-secrets or {environment}_API_KEY."
            );
        }

        var model =
            configuration[$"{environment}_MODEL"] ?? configuration[$"{section}:Model"] ?? fallback;
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException($"Missing {section}:Model");
        }

        Uri? endpoint = null;
        if (provider == "azureopenai")
        {
            var value =
                configuration["AZURE_OPENAI_ENDPOINT"] ?? configuration["AzureOpenAI:Endpoint"];
            if (
                !Uri.TryCreate(value, UriKind.Absolute, out var resource)
                || resource.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(resource.UserInfo)
                || !string.IsNullOrEmpty(resource.Query)
                || !string.IsNullOrEmpty(resource.Fragment)
                || resource.AbsolutePath.Trim('/') is not ("" or "openai/v1")
            )
            {
                throw new InvalidOperationException(
                    "AzureOpenAI:Endpoint must be an HTTPS resource endpoint or its /openai/v1/ URL."
                );
            }

            endpoint = new Uri(resource.GetLeftPart(UriPartial.Authority) + "/openai/v1/");
        }
        return new(provider, key, model, endpoint, configuration[$"{section}:ModelId"]);
    }

    // Test discovery, assertion diagnostics and debuggers must not format keys.
    public override string ToString() => $"LiveSettings({Provider}, {Model})";
}

internal static class SettingsPresence
{
    internal static IReadOnlyDictionary<string, bool> Read(IConfiguration config) =>
        new Dictionary<string, bool>
        {
            ["Azure key"] = Present(config["AZURE_OPENAI_API_KEY"] ?? config["AzureOpenAI:ApiKey"]),
            ["Azure endpoint"] = Present(
                config["AZURE_OPENAI_ENDPOINT"] ?? config["AzureOpenAI:Endpoint"]
            ),
            ["Azure deployment"] = Present(
                config["AZURE_OPENAI_MODEL"] ?? config["AzureOpenAI:Model"]
            ),
            ["Anthropic key"] = Present(config["ANTHROPIC_API_KEY"] ?? config["Anthropic:ApiKey"]),
            ["Anthropic model"] = Present(config["ANTHROPIC_MODEL"] ?? config["Anthropic:Model"]),
        };

    private static bool Present(string? value) => !string.IsNullOrWhiteSpace(value);
}
