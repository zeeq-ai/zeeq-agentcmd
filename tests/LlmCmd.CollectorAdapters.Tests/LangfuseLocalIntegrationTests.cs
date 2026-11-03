using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LlmCmd.CollectorAdapters.Tests;

// These checks contact a separately started local stack. Ordinary test runs must not
// start containers or perform network operations; Aspire and the proof tool own that.
[Explicit, Category("LocalLangfuse")]
public class LangfuseLocalIntegrationTests
{
    [Test]
    public async Task Configured_target_exports_and_reads_back() => await RunProof();

    [Test]
    public async Task Independent_model_cost_cross_check()
    {
        var capture = Environment.GetEnvironmentVariable("LLMCMD_LANGFUSE_CAPTURE");
        if (string.IsNullOrWhiteSpace(capture))
        {
            throw new InvalidOperationException(
                "Set LLMCMD_LANGFUSE_CAPTURE to a sanitized capture artifact."
            );
        }

        await RunProof("--cross-check", Path.GetFullPath(capture));
    }

    private static async Task RunProof(params string[] arguments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Repository root unavailable.");
        }

        var process = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = dir.FullName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (
            var arg in new[]
            {
                "run",
                "--no-build",
                "--project",
                "tests/LlmCmd.LangfuseReadback",
                "--",
            }.Concat(arguments)
        )
        {
            process.ArgumentList.Add(arg);
        }

        using var child =
            Process.Start(process) ?? throw new InvalidOperationException("Unable to start proof.");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await child.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            child.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Local proof timed out.");
        }
        await Task.WhenAll(stdout, stderr); // Do not expose process diagnostics or credentials in test output.
        await Assert.That(child.ExitCode).IsEqualTo(0);
    }
}
