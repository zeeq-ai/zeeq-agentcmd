using System.Security.Cryptography;
using System.Text;
using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace LlmCmd.SecretPropagation.Tests;

public class SecretTests
{
    static async Task WaitForExplicitDriver()
    {
        var gate = Environment.GetEnvironmentVariable("SPIKE_START_GATE")
            ?? throw new InvalidOperationException("Start this spike through its Aspire verification driver");
        // A console test can finish before the CLI observes Running. The gate
        // keeps it alive until `aspire wait --status up` has succeeded.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!File.Exists(gate)) await Task.Delay(50, deadline.Token);
    }

    [Test] public async Task Only_the_bound_process_receives_the_exact_placeholder()
    {
        await WaitForExplicitDriver();
        var resource = Environment.GetEnvironmentVariable("SPIKE_RESOURCE");
        var value = Environment.GetEnvironmentVariable("LLMCMD_SPIKE_SECRET");
        await Assert.That(resource is "receiving-tests" or "control-tests").IsTrue();
        if (resource == "control-tests")
        {
            await Assert.That(value is null).IsTrue();
            return;
        }

        await Assert.That(!string.IsNullOrEmpty(value)).IsTrue();
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value!)));
        // Compare digests, so even a failing assertion never prints a credential.
        // The expected digest is generated independently by the seeding driver.
        await Assert.That(digest == Environment.GetEnvironmentVariable("SPIKE_EXPECTED_SHA256")).IsTrue();
    }

    [Test] public async Task Placeholder_is_not_a_process_argument()
    {
        await WaitForExplicitDriver();
        var value = Environment.GetEnvironmentVariable("LLMCMD_SPIKE_SECRET");
        var exposed = value is not null && Environment.GetCommandLineArgs()
            .Any(argument => argument.Contains(value, StringComparison.Ordinal));
        await Assert.That(exposed).IsFalse();
    }
}
