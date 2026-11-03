using Microsoft.Extensions.Configuration;

namespace LlmCmd.LiveProviders.Tests;

// UserSecretsId belongs to this test assembly. No AppHost or working-directory
// convention is needed; the same command works from an IDE, dotnet or mise.
internal sealed class LiveSettings(string provider, string apiKey, string model)
{
    internal string Provider { get; } = provider;
    internal string ApiKey { get; } = apiKey;
    internal string Model { get; } = model;

    internal static LiveSettings Load(string provider)
    {
        // [Explicit] protects ordinary discovery. This second gate also protects
        // broad IDE filters. Only the dedicated live tasks set this opt-in.
        if (Environment.GetEnvironmentVariable("LLMCMD_RUN_LIVE_TESTS") != "1")
            throw new InvalidOperationException("Use mise run test:live:openai or test:live:anthropic to opt into paid API calls.");

        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<LiveSettings>(optional: true)
            .AddEnvironmentVariables()
            .Build();
        using var lifetime = configuration as IDisposable;
        return From(configuration, provider);
    }

    internal static LiveSettings From(IConfiguration configuration, string provider)
    {
        var (section, environment, fallback) = provider switch
        {
            "openai" => ("OpenAI", "OPENAI", "gpt-4.1-mini"),
            "anthropic" => ("Anthropic", "ANTHROPIC", "claude-haiku-4-5-20251001"),
            _ => throw new ArgumentException("Unknown live-test provider", nameof(provider))
        };
        // Both forms support CI. Flat SDK environment variables win over the
        // developer secret; nested OPENAI__APIKEY also overrides via config.
        var key = configuration[$"{environment}_API_KEY"] ?? configuration[$"{section}:ApiKey"];
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"Missing {section}:ApiKey in this test project's user-secrets or {environment}_API_KEY.");
        var model = configuration[$"{environment}_MODEL"] ?? configuration[$"{section}:Model"] ?? fallback;
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException($"Missing {section}:Model");
        return new(provider, key, model);
    }

    // Test discovery, assertion diagnostics and debuggers must not format keys.
    public override string ToString() => $"LiveSettings({Provider}, {Model})";
}
