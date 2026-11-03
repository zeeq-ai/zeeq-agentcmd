using System.Diagnostics;
using System.Runtime.InteropServices;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Zeeq.LlmCmd.Packaging.Tests;

public partial class PackageTests
{
    private static async Task VerifyNativeConsumer(
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
            // This fixture's RID is intentional. A different host must not be
            // reported as having executed the macOS ARM64 proof.
            Console.WriteLine("Native package proof not run: requires macOS ARM64.");
            return;
        }

        var fixture = Path.Combine(root, "tests", "fixtures", "Zeeq.LlmCmd.NativeAotConsumer");
        var directory = Path.Combine(consumer, "native-proof");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (
                var path in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories)
            )
            {
                var relative = Path.GetRelativePath(fixture, path);
                if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
                {
                    continue;
                }
                var target = Path.Combine(directory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(path, target);
            }

            // No source project reference or precompile target is available in
            // this outside-repository consumer. Its only library input is NuGet.
            var output = Path.Combine(directory, "published");
            await RequireSuccess(
                directory,
                Path.Combine(reports, "consumer-native-publish.log"),
                "publish",
                "NativeAotConsumer.csproj",
                "-c",
                "Release",
                "-r",
                "osx-arm64",
                "-p:LlmCmdPackageVersion=" + version,
                "-p:RestorePackagesPath=" + Path.Combine(directory, "packages"),
                "-p:TreatWarningsAsErrors=true",
                "-p:ILLinkTreatWarningsAsErrors=true",
                "-p:TrimmerSingleWarn=false",
                "-o",
                output,
                "--nologo"
            );

            var executable = Path.Combine(output, "NativeAotConsumer");
            using (var binary = new BinaryReader(File.OpenRead(executable)))
            {
                // The expected executable must be 64-bit ARM Mach-O. The
                // managed assembly check below excludes an ordinary apphost.
                await Assert.That(binary.ReadUInt32()).IsEqualTo(0xfeedfacfu);
                await Assert.That(binary.ReadUInt32()).IsEqualTo(0x0100000cu);
            }
            await Assert.That(File.Exists(Path.Combine(output, "NativeAotConsumer.dll"))).IsFalse();
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process =
                Process.Start(start)
                ?? throw new InvalidOperationException("Unable to start native consumer.");
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
            await File.WriteAllTextAsync(Path.Combine(reports, "consumer-native-run.log"), text);
            await Assert.That(process.ExitCode).IsEqualTo(0);
            await Assert
                .That(
                    text.Contains(
                        "Native package metadata, explicit context, segmented/stream JSON and MEAI typed response passed.",
                        StringComparison.Ordinal
                    )
                )
                .IsTrue();
            await Assert
                .That(
                    text.Contains(
                        "Native provider extraction, telemetry generation and propagation passed.",
                        StringComparison.Ordinal
                    )
                )
                .IsTrue();
            // Retain the exact executable whose header and behavior were checked.
            var retained = Path.Combine(reports, "native");
            Directory.CreateDirectory(retained);
            File.Copy(executable, Path.Combine(retained, "NativeAotConsumer"), overwrite: true);
        }
        finally
        {
            // A later parent build must not pick up this second Program.cs.
            Directory.Delete(directory, recursive: true);
        }
    }
}
