using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace AgentCmd.ProviderAdapters.Tests;

public class FixtureGateTests
{
    [Test]
    public async Task Every_fixture_is_labelled_synthetic_and_not_live()
    {
        var files = Directory.GetFiles(
            Path.Combine(AppContext.BaseDirectory, "Fixtures"),
            "*.json"
        );
        await Assert.That(files.Length).IsEqualTo(9);
        foreach (var file in files)
        {
            var meta = Fx.Json(File.ReadAllText(file)).GetProperty("fixture");
            await Assert.That(meta.GetProperty("synthetic").GetBoolean()).IsTrue();
            await Assert.That(meta.GetProperty("live").GetBoolean()).IsFalse();
            await Assert
                .That(
                    meta.GetProperty("note")
                        .GetString()!
                        .Contains("NOT captured from a live provider call")
                )
                .IsTrue();
            await Assert
                .That(meta.GetProperty("rulesSource").GetString()!.Contains("provider-adapters.md"))
                .IsTrue();
        }
    }

    [Test]
    public async Task Core_assembly_references_no_vendor_sdk_or_sink()
    {
        var names = typeof(global::Zeeq.AgentCmd.Providers.ProviderExtractor)
            .Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ToArray();
        string[] forbidden =
        [
            "OpenAI",
            "Anthropic",
            "Azure",
            "OpenTelemetry",
            "CollectorAdapters",
            "Langfuse",
            "LangSmith",
        ];
        foreach (var n in names)
        {
            await Assert
                .That(forbidden.Any(f => n.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
                .IsFalse();
        }

        await Assert.That(names.Contains("Microsoft.Extensions.AI.Abstractions")).IsTrue();
    }
}
