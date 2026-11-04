using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd;

namespace AgentCmd.Composition.Tests;

/// <summary>Exercises the authoritative, first-writer-wins composition experiment pin.</summary>
public sealed class ExperimentPinTests
{
    private sealed class LegacyStore : ICompositionStore
    {
        public ValueTask<OpenDraftResult> OpenDraftAsync(OpenDraft request, CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask<DraftRecord?> GetDraftAsync(Guid draftId, CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask<ClaimResult> ClaimSlotAsync(ClaimSlot request, CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask<TransitionResult> AttachSlotAsync(
            AttachSlot request,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public ValueTask<TransitionResult> ReleaseSlotAsync(
            ReleaseSlot request,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public ValueTask<TransitionResult> AppendEventAsync(
            AppendEvent request,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public ValueTask<SealResult> SealAsync(SealDraft request, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    [Test]
    public async Task CustomStoreWithoutPinSupportRejectsIt()
    {
        ICompositionStore store = new LegacyStore();
        var result = (ExperimentPinResult.Rejected)
            await store.PinExperimentAsync(
                Guid.NewGuid(),
                ExperimentDraftPin.ForDefault(
                    ExperimentFallbackReason.NoOverride,
                    "tenant",
                    "default-v1"
                ),
                default
            );
        await Assert.That(result.Code).IsEqualTo("experiment_pin_unsupported");
    }

    private static async Task<(InMemoryCompositionStore Store, DraftRecord Draft)> Open()
    {
        var store = new InMemoryCompositionStore();
        var result = (OpenDraftResult.Opened)
            await store.OpenDraftAsync(
                new(
                    new("tenant", Guid.NewGuid(), "whole", "input", "key", null),
                    1,
                    "hash",
                    ["slot"]
                ),
                default
            );
        return (store, result.Draft);
    }

    private static ExperimentSnapshot Snapshot(
        string revision,
        IReadOnlyDictionary<string, JsonElement> parameters,
        IDictionary<string, ExperimentVariant>? variants = null,
        IDictionary<string, ExperimentCatalogView>? commands = null
    )
    {
        ExperimentRevision.TryCreate(revision, out var id);
        variants ??= new Dictionary<string, ExperimentVariant>();
        variants["control"] = new(
            "control",
            1,
            new(new TemplateChange.Inherited(), new TemplateChange.Replaced("hello {Name}")),
            parameters,
            new(ProfileKey.Declare("route"), "client", "model", 0.5f)
        );
        commands ??= new Dictionary<string, ExperimentCatalogView>();
        commands["command"] = new(
            "command",
            1,
            "contract",
            id!,
            "experiment",
            "control",
            (IReadOnlyDictionary<string, ExperimentVariant>)variants
        );
        return new(id!, (IReadOnlyDictionary<string, ExperimentCatalogView>)commands);
    }

    [Test]
    public async Task ConcurrentCandidatesReturnOneAuthoritativePin()
    {
        var (store, draft) = await Open();
        var candidates = Enumerable
            .Range(0, 32)
            .Select(i =>
                ExperimentDraftPin.ForDefault(
                    i % 2 == 0
                        ? ExperimentFallbackReason.Timeout
                        : ExperimentFallbackReason.NoOverride,
                    "tenant",
                    "default-v1",
                    assignmentId: i.ToString()
                )
            )
            .ToArray();
        var results = new System.Collections.Concurrent.ConcurrentBag<ExperimentPinResult>();
        await Parallel.ForEachAsync(
            candidates,
            async (candidate, ct) =>
                results.Add(await store.PinExperimentAsync(draft.DraftId, candidate, ct))
        );
        await Assert.That(results.OfType<ExperimentPinResult.Applied>().Count()).IsEqualTo(1);
        var winner = results.OfType<ExperimentPinResult.Applied>().Single().Pin;
        await Assert.That(results.OfType<ExperimentPinResult.Existing>().Count()).IsEqualTo(31);
        await Assert
            .That(
                results
                    .OfType<ExperimentPinResult.Existing>()
                    .All(r => r.Pin.Fingerprint == winner.Fingerprint)
            )
            .IsTrue();
        await Assert
            .That((await store.GetDraftAsync(draft.DraftId, default))!.ExperimentPin!.Fingerprint)
            .IsEqualTo(winner.Fingerprint);
    }

    [Test]
    public async Task DefaultWinsAgainstLaterTreatment()
    {
        var (store, draft) = await Open();
        var fallback = ExperimentDraftPin.ForDefault(
            ExperimentFallbackReason.Unavailable,
            "tenant",
            "default-v1"
        );
        await store.PinExperimentAsync(draft.DraftId, fallback, default);
        var treatment = ExperimentDraftPin.ForSnapshot(
            Snapshot("v1", new Dictionary<string, JsonElement>()),
            "tenant"
        );
        var existing = (ExperimentPinResult.Existing)
            await store.PinExperimentAsync(draft.DraftId, treatment, default);
        await Assert
            .That(existing.Pin.DefaultReason)
            .IsEqualTo(ExperimentFallbackReason.Unavailable);
        await Assert.That(existing.Pin.Snapshot is null).IsTrue();
    }

    [Test]
    public async Task DefaultIdentityIsRequiredAndChangesThePinFingerprint()
    {
        var first = ExperimentDraftPin.ForDefault(
            ExperimentFallbackReason.Unavailable,
            "tenant",
            "default-v1"
        );
        var second = ExperimentDraftPin.ForDefault(
            ExperimentFallbackReason.Unavailable,
            "tenant",
            "default-v2"
        );
        await Assert.That(first.DefaultConfigurationFingerprint).IsEqualTo("default-v1");
        await Assert.That(first.Fingerprint == second.Fingerprint).IsFalse();
        var rejected = false;
        try
        {
            ExperimentDraftPin.ForDefault(ExperimentFallbackReason.Unavailable, "tenant", " ");
        }
        catch (ArgumentException)
        {
            rejected = true;
        }
        await Assert.That(rejected).IsTrue();
    }

    [Test]
    public async Task SnapshotAndAssignmentsAreDetachedAndFingerprintCoversContents()
    {
        using var document = JsonDocument.Parse("{\"nested\":[1,2]}");
        var parameters = new Dictionary<string, JsonElement> { ["Name"] = document.RootElement };
        var variants = new Dictionary<string, ExperimentVariant>();
        var commands = new Dictionary<string, ExperimentCatalogView>();
        var assignments = new Dictionary<string, string> { ["command"] = "control" };
        var snapshot = Snapshot("v1", parameters, variants, commands);
        var pin = ExperimentDraftPin.ForSnapshot(
            snapshot,
            "tenant",
            "unit",
            "selection",
            assignments
        );
        var same = ExperimentDraftPin.ForSnapshot(
            snapshot,
            "tenant",
            "unit",
            "selection",
            assignments
        );
        await Assert.That(pin.Fingerprint).IsEqualTo(same.Fingerprint);
        var changedAssignment = ExperimentDraftPin.ForSnapshot(
            snapshot,
            "tenant",
            "unit",
            "selection",
            new Dictionary<string, string> { ["command"] = "other" }
        );
        await Assert.That(pin.Fingerprint == changedAssignment.Fingerprint).IsFalse();
        var changedJson = ExperimentDraftPin.ForSnapshot(
            Snapshot(
                "v1",
                new Dictionary<string, JsonElement>
                {
                    ["Name"] = JsonSerializer.SerializeToElement("other"),
                }
            ),
            "tenant",
            "unit",
            "selection",
            assignments
        );
        await Assert.That(pin.Fingerprint == changedJson.Fingerprint).IsFalse();
        assignments["command"] = "other";
        parameters.Clear();
        variants.Clear();
        commands.Clear();
        document.Dispose();
        await Assert.That(pin.Assignments["command"]).IsEqualTo("control");
        await Assert
            .That(
                pin.Snapshot!.Commands["command"]
                    .Variants["control"]
                    .ParameterValues["Name"]
                    .GetProperty("nested")[1]
                    .GetInt32()
            )
            .IsEqualTo(2);
        await Assert.That(pin.Fingerprint).IsEqualTo(same.Fingerprint);
    }

    [Test]
    public async Task MissingSealedStartedAndCancelledDraftsRejectPinning()
    {
        var (store, draft) = await Open();
        var pin = ExperimentDraftPin.ForDefault(
            ExperimentFallbackReason.NoOverride,
            "tenant",
            "default-v1"
        );
        var missing = (ExperimentPinResult.Rejected)
            await store.PinExperimentAsync(Guid.NewGuid(), pin, default);
        await Assert.That(missing.Code).IsEqualTo("draft_missing");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = false;
        try
        {
            await store.PinExperimentAsync(draft.DraftId, pin, cts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        await Assert.That(cancelled).IsTrue();
        await Assert
            .That((await store.GetDraftAsync(draft.DraftId, default))!.ExperimentPin is null)
            .IsTrue();
        await store.ClaimSlotAsync(
            new(draft.DraftId, "slot", Guid.NewGuid(), MemberProvenance.Executed),
            default
        );
        var started = (ExperimentPinResult.Rejected)
            await store.PinExperimentAsync(draft.DraftId, pin, default);
        await Assert.That(started.Code).IsEqualTo("draft_already_started");
        var empty = (OpenDraftResult.Opened)
            await store.OpenDraftAsync(new(draft.Key with { Key = "empty" }, 1, "h", []), default);
        var output = new OutputRef(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1
        );
        var aggregate = new AggregateArtifact(
            new(empty.Draft.DraftId, Guid.NewGuid(), output, "whole", 1, "h"),
            "{}",
            [],
            AggregateProjection.ExactComponentWire,
            DateTimeOffset.UnixEpoch
        );
        await store.SealAsync(new(empty.Draft.DraftId, aggregate), default);
        var sealedResult = (ExperimentPinResult.Rejected)
            await store.PinExperimentAsync(empty.Draft.DraftId, pin, default);
        await Assert.That(sealedResult.Code).IsEqualTo("draft_sealed");
    }
}
