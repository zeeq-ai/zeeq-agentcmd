using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Zeeq.AgentCmd.Packaging.Tests;

public partial class PackageTests
{
    private static async Task VerifyPackagedManifestExport(string consumer, string reports)
    {
        var projectPath = Path.Combine(consumer, "Consumer.csproj");
        var project = XDocument.Load(projectPath);
        project.Root!.Add(
            new XElement(
                "Import",
                new XAttribute("Project", "$(PkgZeeq_AgentCmd)/tools/AgentCmd.Manifest.targets")
            )
        );
        project.Save(projectPath);
        try
        {
            var output = Path.Combine(reports, "consumer.manifest.json");
            await RequireSuccess(
                consumer,
                Path.Combine(reports, "manifest-export.log"),
                "msbuild",
                "Consumer.csproj",
                "-restore",
                "-t:AgentCmdExportManifest",
                "-p:AgentCmdManifestOutput=" + output,
                "-nologo"
            );
            var bytes = await File.ReadAllBytesAsync(output);
            using var manifest = JsonDocument.Parse(bytes);
            await Assert
                .That(manifest.RootElement.GetProperty("formatVersion").GetInt32())
                .IsEqualTo(1);
            await Assert
                .That(manifest.RootElement.GetProperty("baselineTemplatesIncluded").GetBoolean())
                .IsFalse();
            await Assert
                .That(manifest.RootElement.GetProperty("commands").GetArrayLength() > 0)
                .IsTrue();
            var expected = Convert.ToHexStringLower(SHA256.HashData(bytes));
            await Assert
                .That(
                    (await File.ReadAllTextAsync(output + ".sha256", Encoding.UTF8))
                        .Trim()
                        .StartsWith(expected, StringComparison.Ordinal)
                )
                .IsTrue();
        }
        finally
        {
            project.Root!.Elements("Import").Remove();
            project.Save(projectPath);
        }
    }
}
