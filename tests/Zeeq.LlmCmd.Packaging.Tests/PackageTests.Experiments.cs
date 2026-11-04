using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Zeeq.LlmCmd.Packaging.Tests;

public partial class PackageTests
{
    private static async Task VerifyNativeExperimentConsumer(
        string root,
        string consumer,
        string reports,
        string version
    )
    {
        if (
            !OperatingSystem.IsMacOS()
            || RuntimeInformation.ProcessArchitecture != Architecture.Arm64
        )
        {
            Console.WriteLine("Native experiment proof not run: requires macOS ARM64.");
            return;
        }

        var directory = Path.Combine(consumer, "native-experiments");
        Directory.CreateDirectory(directory);
        try
        {
            var example = Path.Combine(root, "examples", "Zeeq.LlmCmd.ExperimentsExample");
            foreach (var file in Directory.GetFiles(example, "*.cs"))
            {
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            }

            var dependencyVersion = XDocument
                .Load(Path.Combine(root, "Directory.Packages.props"))
                .Descendants("PackageVersion")
                .Single(element =>
                    (string?)element.Attribute("Include")
                    == "Microsoft.Extensions.DependencyInjection"
                )
                .Attribute("Version")!
                .Value;
            await File.WriteAllTextAsync(
                Path.Combine(directory, "Consumer.csproj"),
                $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net11.0</TargetFramework>
                    <LangVersion>15</LangVersion>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <LlmCmdGenerateCommands>true</LlmCmdGenerateCommands>
                    <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Zeeq.LlmCmd" Version="{{version}}" />
                    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="{{dependencyVersion}}" />
                  </ItemGroup>
                </Project>
                """
            );
            var output = Path.Combine(directory, "published");
            await RequireSuccess(
                directory,
                Path.Combine(reports, "experiments-native-publish.log"),
                "publish",
                "Consumer.csproj",
                "-c",
                "Release",
                "-r",
                "osx-arm64",
                "--self-contained",
                "-p:PublishAot=true",
                "-p:RestorePackagesPath=" + Path.Combine(directory, "packages"),
                "-p:TreatWarningsAsErrors=true",
                "-p:ILLinkTreatWarningsAsErrors=true",
                "-o",
                output,
                "--nologo"
            );

            var executable = Path.Combine(output, "Consumer");
            using (var binary = new BinaryReader(File.OpenRead(executable)))
            {
                await Assert.That(binary.ReadUInt32()).IsEqualTo(0xfeedfacfu);
                await Assert.That(binary.ReadUInt32()).IsEqualTo(0x0100000cu);
            }
            await Assert.That(File.Exists(Path.Combine(output, "Consumer.dll"))).IsFalse();
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process =
                Process.Start(start)
                ?? throw new InvalidOperationException(
                    "Unable to start native experiment consumer."
                );
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
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
            var text = await stdout + await stderr;
            await File.WriteAllTextAsync(Path.Combine(reports, "experiments-native-run.log"), text);
            await Assert.That(process.ExitCode).IsEqualTo(0);
            await Assert
                .That(text.Contains("Second publication: primary=candidate, shadow=control"))
                .IsTrue();
            await Assert
                .That(
                    text.Contains("Both shadow outputs were read by their exact stored references.")
                )
                .IsTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
