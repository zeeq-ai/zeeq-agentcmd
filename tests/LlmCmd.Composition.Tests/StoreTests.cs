using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.LlmCmd;

namespace LlmCmd.Composition.Tests;

// The reference store, tested directly. A durable store must pass the same behavior.
public class StoreTests
{
    static readonly string[] Ids = ["a", "b"];

    static async Task<(InMemoryCompositionStore Store, DraftRecord Draft)> Open(
        string key = "k",
        string tenant = "t"
    )
    {
        var store = new InMemoryCompositionStore();
        var opened = (OpenDraftResult.Opened)
            await store.OpenDraftAsync(
                new(new(tenant, Guid.NewGuid(), "def", "r1", key, null), 1, "h", Ids),
                default
            );
        return (store, opened.Draft);
    }

    static ClaimSlot Claim(DraftRecord d, string slot, Guid? step = null) =>
        new(
            d.DraftId,
            slot,
            step ?? Guid.NewGuid(),
            MemberProvenance.Executed,
            Guid.NewGuid(),
            "fp"
        );

    static OutputRef Ref() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);

    [Test]
    public async Task Open_is_idempotent_and_scoped_by_tenant_and_definition()
    {
        var (store, draft) = await Open();
        var again = (OpenDraftResult.Opened)
            await store.OpenDraftAsync(new(draft.Key, 1, "h", Ids), default);
        await Assert.That(again.Created).IsFalse();
        await Assert.That(again.Draft.DraftId).IsEqualTo(draft.DraftId);
        await Assert
            .That(await store.OpenDraftAsync(new(draft.Key, 1, "other-hash", Ids), default))
            .IsTypeOf<OpenDraftResult.DefinitionMismatch>();
        await Assert
            .That(await store.OpenDraftAsync(new(draft.Key, 2, "h", Ids), default))
            .IsTypeOf<OpenDraftResult.DefinitionMismatch>();
        var otherTenant = (OpenDraftResult.Opened)
            await store.OpenDraftAsync(new(draft.Key with { Tenant = "t2" }, 1, "h", Ids), default);
        await Assert.That(otherTenant.Draft.DraftId).IsNotEqualTo(draft.DraftId);
    }

    [Test]
    public async Task Exactly_one_of_many_concurrent_claims_wins()
    {
        var (store, draft) = await Open();
        var results = new System.Collections.Concurrent.ConcurrentBag<ClaimResult>();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, 64),
            async (_, ct) => results.Add(await store.ClaimSlotAsync(Claim(draft, "a"), ct))
        );
        await Assert.That(results.Count(r => r is ClaimResult.Claimed)).IsEqualTo(1);
        await Assert.That(results.Count(r => r is ClaimResult.InFlight)).IsEqualTo(63);
    }

    [Test]
    public async Task Attach_and_release_are_fenced_by_the_step_id()
    {
        var (store, draft) = await Open();
        var step = Guid.NewGuid();
        await store.ClaimSlotAsync(Claim(draft, "a", step), default);
        var stale = (TransitionResult.Rejected)
            await store.AttachSlotAsync(
                new(draft.DraftId, "a", Guid.NewGuid(), Ref(), null),
                default
            );
        await Assert.That(stale.Code).IsEqualTo("not_claimed_by_step");
        await Assert
            .That(
                await store.AppendEventAsync(
                    new(draft.DraftId, "a", Guid.NewGuid(), "x", null, null),
                    default
                )
            )
            .IsTypeOf<TransitionResult.Rejected>();
        await Assert
            .That(await store.AttachSlotAsync(new(draft.DraftId, "a", step, Ref(), null), default))
            .IsTypeOf<TransitionResult.Applied>();
        // Attached is terminal.
        await Assert
            .That(
                await store.ReleaseSlotAsync(
                    new(draft.DraftId, "a", step, SlotStatus.Failed, "x"),
                    default
                )
            )
            .IsTypeOf<TransitionResult.Rejected>();
        await Assert
            .That(await store.ClaimSlotAsync(Claim(draft, "a"), default))
            .IsTypeOf<ClaimResult.AlreadyAttached>();
    }

    [Test]
    public async Task A_released_slot_can_be_claimed_again_and_counts_attempts()
    {
        var (store, draft) = await Open();
        var step = Guid.NewGuid();
        await store.ClaimSlotAsync(Claim(draft, "a", step), default);
        await store.ReleaseSlotAsync(
            new(draft.DraftId, "a", step, SlotStatus.Failed, "provider_failure"),
            default
        );
        var second = (ClaimResult.Claimed)await store.ClaimSlotAsync(Claim(draft, "a"), default);
        await Assert.That(second.Slot.Attempts).IsEqualTo(2);
        await Assert.That(second.Slot.Code).IsNull();
        await Assert
            .That(
                Check.Throws<ArgumentException>(() =>
                    store.ReleaseSlotAsync(
                        new(draft.DraftId, "a", step, SlotStatus.Attached, "x"),
                        default
                    )
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task Seal_is_a_membership_compare_and_set_and_is_idempotent()
    {
        var (store, draft) = await Open();
        var refs = new Dictionary<string, OutputRef> { ["a"] = Ref(), ["b"] = Ref() };
        foreach (var id in Ids)
        {
            var step = Guid.NewGuid();
            await store.ClaimSlotAsync(Claim(draft, id, step), default);
            await store.AttachSlotAsync(new(draft.DraftId, id, step, refs[id], null), default);
        }
        AggregateArtifact Artifact(params string[] slots) =>
            new(
                new(draft.DraftId, Guid.NewGuid(), Ref(), "def", 1, "h"),
                "{}",
                slots
                    .Select(s => new CompositeMember(
                        s,
                        s,
                        refs[s],
                        MemberProvenance.Executed,
                        null,
                        null
                    ))
                    .ToArray(),
                AggregateProjection.ExactComponentWire,
                DateTimeOffset.UnixEpoch
            );

        await Assert
            .That(await store.SealAsync(new(draft.DraftId, Artifact("a")), default))
            .IsTypeOf<SealResult.Rejected>();
        var wrongVersion = Artifact("a", "b") with
        {
            Members =
            [
                new("a", "a", Ref(), MemberProvenance.Executed, null, null),
                new("b", "b", refs["b"], MemberProvenance.Executed, null, null),
            ],
        };
        await Assert
            .That(
                (
                    (SealResult.Rejected)
                        await store.SealAsync(new(draft.DraftId, wrongVersion), default)
                ).Code
            )
            .IsEqualTo("membership_changed");

        var winner = Artifact("a", "b");
        var sealedDraft = (
            (SealResult.Sealed)await store.SealAsync(new(draft.DraftId, winner), default)
        ).Draft;
        await Assert.That(sealedDraft.Status).IsEqualTo(DraftStatus.Sealed);
        await Assert.That(sealedDraft.Aggregate).IsEqualTo(winner);
        var loser = (SealResult.AlreadySealed)
            await store.SealAsync(new(draft.DraftId, Artifact("a", "b")), default);
        await Assert.That(loser.Draft.Aggregate).IsEqualTo(winner);
    }

    [Test]
    public async Task A_sealed_draft_accepts_no_new_claims()
    {
        var (store, draft) = await Open();
        var step = Guid.NewGuid();
        await store.ClaimSlotAsync(Claim(draft, "a", step), default);
        var output = Ref();
        await store.AttachSlotAsync(new(draft.DraftId, "a", step, output, null), default);
        var solo = await store.OpenDraftAsync(
            new(draft.Key with { Key = "solo" }, 1, "h", ["a"]),
            default
        );
        // "b" is Empty in the first draft, which is never sealed: sealing needs every attached slot to match, so seal a one-slot draft.
        var one = ((OpenDraftResult.Opened)solo).Draft;
        var s2 = Guid.NewGuid();
        var o2 = Ref();
        await store.ClaimSlotAsync(Claim(one, "a", s2), default);
        await store.AttachSlotAsync(new(one.DraftId, "a", s2, o2, null), default);
        await store.SealAsync(
            new(
                one.DraftId,
                new(
                    new(one.DraftId, Guid.NewGuid(), Ref(), "def", 1, "h"),
                    "{}",
                    [new("a", "a", o2, MemberProvenance.Executed, null, null)],
                    AggregateProjection.ExactComponentWire,
                    DateTimeOffset.UnixEpoch
                )
            ),
            default
        );
        var sealedWithEmpty = await Open("sealed");
        await Assert
            .That(await store.ClaimSlotAsync(Claim(one, "a"), default))
            .IsTypeOf<ClaimResult.AlreadyAttached>();
        _ = sealedWithEmpty;
    }

    [Test]
    public async Task Records_handed_out_are_snapshots()
    {
        var (store, draft) = await Open();
        await store.ClaimSlotAsync(Claim(draft, "a"), default);
        await Assert.That(draft.Slots["a"].Status).IsEqualTo(SlotStatus.Empty);
        await Assert.That(draft.Events.Count).IsEqualTo(1);
        var now = (await store.GetDraftAsync(draft.DraftId, default))!;
        await Assert.That(now.Slots["a"].Status).IsEqualTo(SlotStatus.Claimed);
        await Assert.That(now.Version).IsGreaterThan(draft.Version);
        await Assert.That(await store.GetDraftAsync(Guid.NewGuid(), default)).IsNull();
    }

    [Test]
    public async Task Unknown_draft_and_slot_are_rejected()
    {
        var (store, draft) = await Open();
        await Assert
            .That(
                (
                    (ClaimResult.Rejected)await store.ClaimSlotAsync(Claim(draft, "zzz"), default)
                ).Code
            )
            .IsEqualTo("slot_unknown");
        await Assert
            .That(
                (
                    (ClaimResult.Rejected)
                        await store.ClaimSlotAsync(
                            new(Guid.NewGuid(), "a", Guid.NewGuid(), MemberProvenance.Executed),
                            default
                        )
                ).Code
            )
            .IsEqualTo("draft_missing");
    }
}
