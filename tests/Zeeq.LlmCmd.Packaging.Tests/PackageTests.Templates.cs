using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Zeeq.LlmCmd.Packaging.Tests;

public partial class PackageTests
{
    private static async Task VerifyTemplateDeclarations(string consumer, string reports)
    {
        var path = Path.Combine(consumer, "TemplateDeclarationProbe.cs");
        try
        {
            // The consumer must receive both the generator error and analyzer
            // warning through NuGet, not only when tests instantiate them directly.
            foreach (
                var (name, id, user, declaration) in new[]
                {
                    (
                        "invalid-system",
                        "LLMCMD001",
                        "const string UserTemplate = \"{Text}\";",
                        "static readonly string SystemTemplate = \"system\";"
                    ),
                    ("missing-user", "LLMCMD001", "", "const string SystemTemplate = \"system\";"),
                    (
                        "system-typo",
                        "LLMCMD002",
                        "const string UserTemplate = \"{Text}\";",
                        "const string SystemTemplte = \"system\";"
                    ),
                }
            )
            {
                await File.WriteAllTextAsync(
                    path,
                    $$"""
                    using Zeeq.LlmCmd;
                    namespace TemplatePackageProbe;
                    public sealed record Input(string Text);
                    public sealed record Output(string Value);
                    public sealed partial class Command : PromptCommand<Input, Output>
                    {
                        const string Id = "package.templates";
                        {{user}}
                        {{declaration}}
                    }
                    """
                );
                var rejected = await Run(
                    consumer,
                    Path.Combine(reports, "consumer-templates-" + name + ".log"),
                    "build",
                    "Consumer.csproj",
                    "-p:WarningsAsErrors=LLMCMD002",
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
            File.Delete(path);
        }
    }
}
