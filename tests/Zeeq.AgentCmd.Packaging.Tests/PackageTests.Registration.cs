using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Zeeq.AgentCmd.Packaging.Tests;

public partial class PackageTests
{
    private static async Task VerifyClientRegistrationAnalyzer(string consumer, string reports)
    {
        var path = Path.Combine(consumer, "RegistrationProbe.cs");
        try
        {
            // A package-only build must discover the analyzer automatically.
            // Invoking it directly in unit tests cannot prove NuGet delivery.
            await WriteRegistrationProbe(
                path,
                """
                services.AddAgentCmd(b => b
                    .AddChatClient("claude")
                    .AddProfile("review", "claude", "claude-haiku-5-5"));
                services.AddAgentCmd(b => b
                    .AddChatClient(ClientFromConfiguration())
                    .AddProfile("review", "claude", "claude-haiku-5-5"));
                """
            );
            await RequireSuccess(
                consumer,
                Path.Combine(reports, "consumer-registration-valid.log"),
                "build",
                "Consumer.csproj",
                "--nologo"
            );

            (string Id, string Body)[] invalid =
            [
                (
                    "AGENTCLIENT001",
                    """
                    services.AddAgentCmd(b => b.AddChatClient("claude")
                        .AddProfile("review", "cluade", "claude-haiku-5-5"));
                    """
                ),
                (
                    "AGENTCLIENT002",
                    """
                    services.AddAgentCmd(b => b.AddChatClient("claude")
                        .AddChatClient("claude"));
                    """
                ),
                (
                    "AGENTCLIENT003",
                    """
                    services.AddAgentCmd(b => b.AddChatClient("azure", fixedModel: "deployment-a")
                        .AddProfile("review", "azure", "deployment-b"));
                    """
                ),
            ];
            foreach (var (id, body) in invalid)
            {
                await WriteRegistrationProbe(path, body);
                var rejected = await Run(
                    consumer,
                    Path.Combine(reports, "consumer-registration-" + id + ".log"),
                    "build",
                    "Consumer.csproj",
                    "--nologo"
                );
                await Assert.That(rejected.ExitCode != 0).IsTrue();
                await Assert
                    .That(
                        rejected
                            .Output.Split('\n')
                            .Any(line =>
                                line.Contains(path + "(", StringComparison.Ordinal)
                                && line.Contains("error " + id, StringComparison.Ordinal)
                            )
                    )
                    .IsTrue();
            }
        }
        finally
        {
            // The parent consumer is rebuilt for unrelated documentation and
            // contract tests, so no intentional registration error may leak out.
            File.Delete(path);
        }
    }

    private static Task WriteRegistrationProbe(string path, string body) =>
        File.WriteAllTextAsync(
            path,
            $$"""
            using Microsoft.Extensions.DependencyInjection;
            using Zeeq.AgentCmd;

            internal static class RegistrationProbe
            {
                private static string ClientFromConfiguration() => "claude";

                internal static void Configure(IServiceCollection services)
                {
                    {{body}}
                }
            }
            """
        );
}
