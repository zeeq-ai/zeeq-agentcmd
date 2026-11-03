using ProviderAdapters;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LlmCmd.LiveProviders.Tests;

// Explicit selection AND LLMCMD_RUN_LIVE_TESTS=1 are required. A plain dotnet
// run/test must remain free of paid API calls even when real keys are installed.
[Explicit, Category("Live")]
public class OpenAiCaptureTests
{
    [Test]
    public Task Captures_real_chat_completions_response() => ProviderCapture.RunLiveAsync("openai");
}

[Explicit, Category("Live")]
public class AnthropicCaptureTests
{
    [Test]
    public Task Captures_real_messages_response() => ProviderCapture.RunLiveAsync("anthropic");
}

internal static class CaptureAssertions
{
    internal static async Task CheckAsync(CaptureRun run)
    {
        var snapshot = run.Snapshot;
        await Assert.That(run.ObserverFailures.Count).IsEqualTo(0);
        await Assert.That(snapshot.End).IsEqualTo(AttemptEnd.Completed);
        await Assert.That(snapshot.IsStreaming).IsFalse();
        await Assert.That(!string.IsNullOrEmpty(snapshot.ResponseId)).IsTrue();
        await Assert.That(!string.IsNullOrEmpty(snapshot.ModelId)).IsTrue();
        await Assert.That(snapshot.ResponseId).IsEqualTo(run.Response.ResponseId);
        await Assert.That(snapshot.ModelId).IsEqualTo(run.Response.ModelId);
        await Assert.That(!string.IsNullOrEmpty(snapshot.NativeStopReason)).IsTrue();
        await Assert.That(snapshot.Usage.Input > 0).IsTrue();
        await Assert.That(snapshot.Usage.Output > 0).IsTrue();
        await Assert.That(snapshot.Usage.Input).IsEqualTo(run.Response.Usage?.InputTokenCount);
        await Assert.That(snapshot.Usage.Output).IsEqualTo(run.Response.Usage?.OutputTokenCount);
        await Assert.That(snapshot.Usage.Measurements.Any(measurement =>
            measurement.Observations.Any(observation => observation.Origin == Origin.Native))).IsTrue();
        await Assert.That(snapshot.Usage.Conflicts.Count).IsEqualTo(0);
        await Assert.That(snapshot.Diagnostics.Any(diagnostic => diagnostic.Severity == Severity.Error
            || diagnostic.Code is "unsupported-native" or "native-unavailable" or "native-projection-failed" or "adapter-failed")).IsFalse();
        await Assert.That(snapshot.RawContent is null).IsTrue();
    }
}
