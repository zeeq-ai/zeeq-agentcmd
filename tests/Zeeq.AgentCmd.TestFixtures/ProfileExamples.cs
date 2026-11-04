using Microsoft.Extensions.AI;
using Zeeq.AgentCmd;

namespace CommandRoot;

public sealed partial class Profiles
{
    [ModelProfile("extraction.openai")]
    public partial ProfileKey OpenAI { get; }

    [ModelProfile("extraction.anthropic")]
    public partial ProfileKey Anthropic { get; }
}

public static class ProfileExamples
{
    // Host setup, once. Factories return new owned MEAI clients; model IDs can come from configuration.
    public static RouterBuildResult CreateRouter(
        Func<IChatClient> openAI,
        Func<IChatClient> anthropic,
        string openAIModel,
        string anthropicModel
    ) =>
        ProfileRouter.TryCreate(
            new[]
            {
                new ExecutionProfile(
                    Profiles.Instance.OpenAI,
                    "openai",
                    openAIModel,
                    Retry: new RetryPolicy(MaxRetries: 2),
                    Fallback: Profiles.Instance.Anthropic
                ),
                new ExecutionProfile(
                    Profiles.Instance.Anthropic,
                    "anthropic",
                    anthropicModel,
                    Retry: new RetryPolicy(MaxRetries: 1)
                ),
            },
            new Dictionary<string, ProviderRegistration>
            {
                ["openai"] = new(openAI),
                ["anthropic"] = new(anthropic),
            },
            new ExecutionBudget(MaxAttempts: 5, Timeout: TimeSpan.FromSeconds(30))
        );

    // The injected command, parameter record and output contract stay the same.
    public static Task<Outcome<Invoice>> ExtractWithOpenAI(
        ExtractInvoice extract,
        string document,
        Guid runId,
        CancellationToken ct
    ) =>
        extract
            .WithParameters(new InvoiceParameters(document, Locale: "en"))
            .WithProfile<Profiles>(p => p.OpenAI)
            .InRun(runId)
            .ExecuteAsync(ct);

    public static Task<Outcome<Invoice>> ExtractWithAnthropic(
        ExtractInvoice extract,
        string document,
        Guid runId,
        CancellationToken ct
    ) =>
        extract
            .WithParameters(new InvoiceParameters(document, Locale: "en"))
            .WithProfile<Profiles>(p => p.Anthropic)
            .InRun(runId)
            .ExecuteAsync(ct);
}
