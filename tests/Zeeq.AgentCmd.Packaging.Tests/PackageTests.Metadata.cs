using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Zeeq.AgentCmd.Packaging.Tests;

public partial class PackageTests
{
    private static async Task VerifyUnsupportedMetadataIsDiagnosed(string consumer, string reports)
    {
        var path = Path.Combine(consumer, "UnsupportedMetadata.cs");
        try
        {
            // Unsupported shapes must fail at compile time rather than fall
            // through to reflection or emit a partial wire contract.
            await File.WriteAllTextAsync(
                path,
                """
                using System.Text.Json;
                using System.Text.Json.Serialization;
                using CompositionDemo;
                using Zeeq.AgentCmd;
                internal sealed record UnsupportedResult([property: JsonExtensionData] Dictionary<string, JsonElement> Extra);
                internal sealed partial class UnsupportedCommand : PromptCommand<SourceText, UnsupportedResult>
                {
                    public const string Id = "package.unsupported";
                    public const string UserTemplate = "{Document}";
                }
                """
            );
            var result = await Run(
                consumer,
                Path.Combine(reports, "consumer-unsupported-metadata.log"),
                "build",
                "Consumer.csproj",
                "--nologo"
            );
            await Assert.That(result.ExitCode != 0).IsTrue();
            await Assert
                .That(
                    result
                        .Output.Split('\n')
                        .Any(line =>
                            line.Contains("UnsupportedMetadata.cs(", StringComparison.Ordinal)
                            && line.Contains("error AGENTMETA001", StringComparison.Ordinal)
                        )
                )
                .IsTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
