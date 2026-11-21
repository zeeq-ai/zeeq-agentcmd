using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd;

namespace AgentCmd.CommandRoot.Tests;

public partial class StrictCandidateJournalTests
{
    [Test]
    public async Task PreflightRendersEffectiveTypedCandidateWithoutSendingOrJournaling()
    {
        using var harness = Routed(Release("r1"));
        var journal = new Journal();
        var preview = await harness
            .Command.WithParameters(new("document", "en"))
            .InRun(harness.Run())
            .WithJournal(journal)
            .RequireExperimentCandidate(Planned)
            .PreflightAsync();
        await Assert.That(preview.Ready).IsTrue();
        await Assert.That(preview.User).IsEqualTo("new document fr");
        await Assert.That(preview.Evidence!.VariantId).IsEqualTo("v");
        await Assert.That(harness.Sends).IsEqualTo(0);
        await Assert.That(journal.Events.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PreflightRequiresExplicitRunAuthorityAndStrictIdentity()
    {
        using var harness = Routed(Release("r1"));
        var invocation = harness.Command.WithParameters(new("document", "en"));
        var implicitContext = await invocation.RequireExperimentCandidate(Planned).PreflightAsync();
        var unrestricted = await invocation.InRun(harness.Run()).PreflightAsync();
        var inaccessible = await invocation
            .InRun(Guid.NewGuid())
            .RequireExperimentCandidate(Planned)
            .PreflightAsync();
        await Assert
            .That(implicitContext.FailureCode)
            .IsEqualTo("preflight_requires_explicit_context");
        await Assert
            .That(unrestricted.FailureCode)
            .IsEqualTo("preflight_requires_strict_candidate");
        await Assert.That(inaccessible.FailureCode).IsEqualTo("preflight_run_not_accessible");
        await Assert.That(harness.Source.Requested.Count).IsEqualTo(0);
        await Assert.That(harness.Sends).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidCandidatePreviewDoesNotFallBackOrSpend()
    {
        using var harness = Routed(Release("r1", signature: "other-shape"));
        var preview = await harness
            .Command.WithParameters(new("document", "en"))
            .InRun(harness.Run())
            .RequireExperimentCandidate(Planned)
            .PreflightAsync();
        await Assert.That(preview.Ready).IsFalse();
        await Assert.That(preview.FailureCode).IsNotNull();
        await Assert.That(harness.Sends).IsEqualTo(0);
    }
}
