using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Zeeq.LlmCmd.Packaging.Tests;

public partial class PackageTests
{
    [Test]
    public async Task Nuget_only_consumer_generates_executes_and_rejects_wrong_part_types()
    {
        var root = FindRepository();
        var reports = Path.Combine(root, "artifacts", "package-proof");
        Directory.CreateDirectory(reports);
        await RequireSuccess(
            root,
            Path.Combine(reports, "pack.log"),
            "pack",
            "src/Zeeq.LlmCmd/Zeeq.LlmCmd.csproj",
            "-c",
            "Release",
            "-o",
            "artifacts/packages",
            "--nologo"
        );
        foreach (var name in new[] { "Zeeq.LlmCmd.Providers", "Zeeq.LlmCmd.OpenTelemetry" })
        {
            await RequireSuccess(
                root,
                Path.Combine(reports, name + ".pack.log"),
                "pack",
                $"src/{name}/{name}.csproj",
                "-c",
                "Release",
                "-o",
                "artifacts/packages",
                "--nologo"
            );
        }

        var version = XDocument
            .Load(Path.Combine(root, "Directory.Build.props"))
            .Descendants("Version")
            .Single()
            .Value;
        var package = Path.Combine(root, "artifacts", "packages", $"Zeeq.LlmCmd.{version}.nupkg");
        using (var archive = ZipFile.OpenRead(package))
        {
            var paths = archive
                .Entries.Select(entry => entry.FullName)
                .ToHashSet(StringComparer.Ordinal);
            await Assert.That(paths.Contains("lib/net11.0/Zeeq.LlmCmd.dll")).IsTrue();
            await Assert
                .That(paths.Contains("analyzers/dotnet/cs/Zeeq.LlmCmd.Generators.dll"))
                .IsTrue();
            await Assert
                .That(
                    paths.Any(path =>
                        path.Contains("Precompile", StringComparison.OrdinalIgnoreCase)
                    )
                )
                .IsFalse();
            await Assert
                .That(
                    paths.Any(path => path.StartsWith("tools/", StringComparison.OrdinalIgnoreCase))
                )
                .IsFalse();
            await Assert
                .That(
                    paths.Any(path =>
                        path.StartsWith("buildTransitive/", StringComparison.OrdinalIgnoreCase)
                    )
                )
                .IsFalse();
            using var manifestStream = archive
                .Entries.Single(entry =>
                    entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal)
                )
                .Open();
            var manifest = XDocument.Load(manifestStream);
            var dependencies = manifest
                .Descendants()
                .Where(element => element.Name.LocalName == "dependency")
                .Select(element => (string?)element.Attribute("id"))
                .ToArray();
            await Assert
                .That(
                    dependencies.Any(id =>
                        id is "Zeeq.LlmCmd.Generators" or "Zeeq.LlmCmd.Precompile"
                    )
                )
                .IsFalse();
        }

        // Outside the repository: neither Directory.Build.targets nor a source
        // ProjectReference can accidentally make a broken package look usable.
        var consumer = Path.Combine(
            Path.GetTempPath(),
            "zeeq-package-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(consumer);
        try
        {
            File.Copy(Path.Combine(root, "global.json"), Path.Combine(consumer, "global.json"));
            foreach (
                var file in Directory.GetFiles(
                    Path.Combine(root, "examples", "Zeeq.LlmCmd.CompositionExample"),
                    "*.cs"
                )
            )
            {
                File.Copy(file, Path.Combine(consumer, Path.GetFileName(file)));
            }

            var configuration = new XDocument(
                new XElement(
                    "configuration",
                    new XElement(
                        "packageSources",
                        new XElement("clear"),
                        new XElement(
                            "add",
                            new XAttribute("key", "local"),
                            new XAttribute("value", Path.Combine(root, "artifacts", "packages"))
                        ),
                        new XElement(
                            "add",
                            new XAttribute("key", "nuget.org"),
                            new XAttribute("value", "https://api.nuget.org/v3/index.json")
                        )
                    )
                )
            );
            configuration.Save(Path.Combine(consumer, "NuGet.Config"));
            await File.WriteAllTextAsync(
                Path.Combine(consumer, "Consumer.csproj"),
                $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><LangVersion>15</LangVersion>
                    <Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                    <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
                    <RestorePackagesPath>packages</RestorePackagesPath>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Zeeq.LlmCmd" Version="{{version}}" />
                    <PackageReference Include="Zeeq.LlmCmd.Providers" Version="{{version}}" />
                    <PackageReference Include="Zeeq.LlmCmd.OpenTelemetry" Version="{{version}}" />
                  </ItemGroup>
                </Project>
                """
            );
            await File.WriteAllTextAsync(
                Path.Combine(consumer, "ModuleProbe.cs"),
                """
                using System.Runtime.CompilerServices;
                using global::OpenTelemetry;
                using global::OpenTelemetry.Trace;
                using global::OpenTelemetry.Metrics;
                using Zeeq.LlmCmd;
                using Zeeq.LlmCmd.Providers;
                using Zeeq.LlmCmd.OpenTelemetry;
                internal static class ModuleProbe
                {
                    [ModuleInitializer]
                    internal static void Initialize()
                    {
                        // Both optional packages must coexist with the core
                        // without ambiguous public names or private ABI access.
                        _ = new ProviderIdentity("openai", "proof", ApiKind.ChatCompletions);
                        _ = new ProviderRegistration(() => new DemoClient());
                        if (!OperationContextValidation.IsValid(OperationContext.ForRun(Guid.NewGuid()).Reference))
                            throw new InvalidOperationException("Context validation differs across packages");
                        using var traces = Sdk.CreateTracerProviderBuilder().AddLlmCmdInstrumentation().Build();
                        using var metrics = Sdk.CreateMeterProviderBuilder().AddLlmCmdInstrumentation().Build();
                    }
                }
                """
            );
            // The package supplies source generators without a precompile tool.
            // The consumer declares no serializer context for its default DTOs.
            await RequireSuccess(
                consumer,
                Path.Combine(reports, "consumer-build.log"),
                "build",
                "Consumer.csproj",
                "--nologo"
            );
            await RequireSuccess(
                consumer,
                Path.Combine(reports, "consumer-run.log"),
                "run",
                "--no-build",
                "--project",
                "Consumer.csproj"
            );
            await RequireSuccess(
                consumer,
                Path.Combine(reports, "consumer-quick.log"),
                "run",
                "--no-build",
                "--project",
                "Consumer.csproj",
                "--",
                "--quick"
            );

            await VerifyClientRegistrationAnalyzer(consumer, reports);
            await VerifyNativeConsumer(root, consumer, reports, version);
            await VerifyNativeExperimentConsumer(root, consumer, reports, version);
            await VerifyUnsupportedMetadataIsDiagnosed(consumer, reports);

            var undocumented = Path.Combine(consumer, "UndocumentedApi.cs");
            await File.WriteAllTextAsync(undocumented, "public sealed class UndocumentedApi { }");

            // Enabling documentation warnings must still reject undocumented
            // application code while leaving generated members exempt. Rebuild
            // ensures this assertion does not depend on the previous build cache.
            var docs = await Run(
                consumer,
                Path.Combine(reports, "consumer-docs.log"),
                "build",
                "Consumer.csproj",
                "--nologo",
                "-t:Rebuild",
                "-p:GenerateDocumentationFile=true"
            );
            var diagnostics = docs.Output.Split('\n');
            await Assert.That(docs.ExitCode != 0).IsTrue();
            await Assert
                .That(
                    diagnostics.Any(line =>
                        line.Contains(undocumented + "(", StringComparison.Ordinal)
                        && line.Contains("error CS1591", StringComparison.Ordinal)
                    )
                )
                .IsTrue();
            await Assert
                .That(
                    diagnostics.Any(line =>
                        line.Contains(".g.cs", StringComparison.OrdinalIgnoreCase)
                        && line.Contains("CS1591", StringComparison.Ordinal)
                    )
                )
                .IsFalse();
            File.Delete(undocumented);

            // A separate documented consumer proves the generated API builds
            // with documentation enabled, rather than only checking the negative case.
            var documented = Path.Combine(consumer, "documented");
            Directory.CreateDirectory(documented);
            await File.WriteAllTextAsync(
                Path.Combine(documented, "Documented.csproj"),
                $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net11.0</TargetFramework><LangVersion>15</LangVersion>
                    <Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
                    <TreatWarningsAsErrors>true</TreatWarningsAsErrors><GenerateDocumentationFile>true</GenerateDocumentationFile>
                    <RestorePackagesPath>../packages</RestorePackagesPath>
                  </PropertyGroup>
                  <ItemGroup><PackageReference Include="Zeeq.LlmCmd" Version="{{version}}" /></ItemGroup>
                </Project>
                """
            );
            await File.WriteAllTextAsync(
                Path.Combine(documented, "DocumentedCommand.cs"),
                """
                using Zeeq.LlmCmd;
                namespace DocumentedConsumer;
                /// <summary>Input text for the documented command.</summary>
                /// <param name="Text">Text to process.</param>
                public sealed record Input(string Text);
                /// <summary>Result of the documented command.</summary>
                /// <param name="Text">Processed text.</param>
                public sealed record Result(string Text);
                /// <summary>Minimal source-declared command with generated members.</summary>
                public sealed partial class DocumentedCommand : PromptCommand<Input, Result>
                {
                    const string Id = "documented.proof";
                    const string UserTemplate = "Process {Text}";
                }
                /// <summary>Typed profile catalog for the documented command.</summary>
                public sealed partial class DocumentedProfiles
                {
                    /// <summary>Profile used for extraction.</summary>
                    [ModelProfile("documented.profile")]
                    public partial ProfileKey Extract { get; }
                }
                internal static class ProfileProbe
                {
                    internal static ProfileKey Selected => DocumentedProfiles.Instance.Extract;
                }
                """
            );
            await RequireSuccess(
                documented,
                Path.Combine(reports, "consumer-docs-positive.log"),
                "build",
                "Documented.csproj",
                "--nologo"
            );
            var documentation = Path.Combine(
                documented,
                "bin",
                "Debug",
                "net11.0",
                "Documented.xml"
            );
            await Assert.That(File.Exists(documentation)).IsTrue();
            var xml = await File.ReadAllTextAsync(documentation);
            await Assert
                .That(
                    xml.Contains("DocumentedConsumer.DocumentedCommand", StringComparison.Ordinal)
                )
                .IsTrue();
            await Assert
                .That(
                    xml.Contains("DocumentedConsumer.DocumentedProfiles", StringComparison.Ordinal)
                )
                .IsTrue();
            // Keep this fixture out of the parent's later default source glob.
            Directory.Delete(documented, recursive: true);

            await File.WriteAllTextAsync(
                Path.Combine(consumer, "WrongSlot.cs"),
                """
                using Zeeq.LlmCmd;
                using CompositionDemo;
                internal static class WrongSlot
                {
                    internal static void Wire(CompositionRoot<DocumentExtraction> root, ExtractTotals totals) =>
                        root.WithHeaderFrom(totals.WithParameters(new("document", 2)));
                }
                """
            );
            var rejected = await Run(
                consumer,
                Path.Combine(reports, "wrong-slot.log"),
                "build",
                "Consumer.csproj",
                "--nologo"
            );
            await Assert
                .That(
                    rejected.ExitCode != 0
                        && rejected.Output.Contains("WrongSlot.cs", StringComparison.Ordinal)
                        && (
                            rejected.Output.Contains("CS1503", StringComparison.Ordinal)
                            || rejected.Output.Contains("CS0411", StringComparison.Ordinal)
                        )
                )
                .IsTrue();
        }
        finally
        {
            Directory.Delete(consumer, recursive: true);
        }
    }

    private static string FindRepository()
    {
        for (
            var path = new DirectoryInfo(AppContext.BaseDirectory);
            path is not null;
            path = path.Parent
        )
        {
            if (File.Exists(Path.Combine(path.FullName, "Zeeq.LlmCmd.slnx")))
            {
                return path.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found");
    }

    private static async Task RequireSuccess(
        string directory,
        string log,
        params string[] arguments
    )
    {
        var result = await Run(directory, log, arguments);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("Package verification failed; see " + log);
        }
    }

    private static async Task<(int ExitCode, string Output)> Run(
        string directory,
        string log,
        params string[] arguments
    )
    {
        var start = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet"
        )
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var process =
            Process.Start(start) ?? throw new InvalidOperationException("Unable to start dotnet");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        var text = await output + await error;
        await File.WriteAllTextAsync(log, text);
        return (process.ExitCode, text);
    }
}
