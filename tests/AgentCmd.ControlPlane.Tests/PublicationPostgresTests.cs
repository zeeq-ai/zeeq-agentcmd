using System.Globalization;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd;
using Zeeq.AgentCmd.ControlPlane;

namespace AgentCmd.ControlPlane.Tests;

/// <summary>Real adapter contracts over the same isolated fixture that qualifies the management store.</summary>
[ClassDataSource<PostgresQualificationFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel("postgres-qualification")]
public sealed class PublicationPostgresTests(PostgresQualificationFixture fixture)
{
    private static readonly WorkspaceScope Scope = PostgresStoreTests.Scope;
    private static readonly ManagementActor Actor = PostgresStoreTests.Actor;

    private PublicationPostgresTarget Target(string database) =>
        new(
            new("test-target", Scope, "test-binding", "v1", "build", true),
            "fixture-generated-credential",
            (reference, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (reference != "fixture-generated-credential")
                {
                    throw new InvalidOperationException("Unexpected credential reference.");
                }
                return ValueTask.FromResult(fixture.CreateConnection(database));
            }
        );

    [Test]
    [Explicit]
    public async Task RealTargetFencesConcurrentAndAbaPublishersAndRetainsPinsAcrossRestart()
    {
        var database = await fixture.NewDatabaseAsync();
        var first = Target(database);
        var second = Target(database);
        await Task.WhenAll(first.InitializeAsync(), second.InitializeAsync());
        var a = Content("revision-a", "first");
        var b = Content("revision-b", "second");
        await Task.WhenAll(first.PutAsync(a), second.PutAsync(b));
        var race = await Task.WhenAll(
            first.ActivateAsync(null, a, "operation-a"),
            second.ActivateAsync(null, b, "operation-b")
        );
        await Assert.That(race.Count(result => result.Applied)).IsEqualTo(1);
        var initial = (await first.ReadCurrentAsync())!;
        var initialContent = initial.Revision == a.Revision ? a : b;
        var alternate = initial.Revision == a.Revision ? b : a;
        await Assert
            .That((await first.ActivateAsync(initial, alternate, "operation-next")).Applied)
            .IsTrue();
        await Assert
            .That(
                (
                    await first.ActivateAsync(
                        await first.ReadCurrentAsync(),
                        initialContent,
                        "operation-back"
                    )
                ).Applied
            )
            .IsTrue();
        var stale = await second.ActivateAsync(initial, alternate, "operation-stale");
        await Assert.That(stale.Applied).IsFalse();
        await Assert.That(stale.Current!.Generation).IsEqualTo(3L);
        await PostgresStoreTests.ExpectCode(
            () => second.PutAsync(b with { Revision = a.Revision }),
            "publication_revision_conflict"
        );
        await fixture.RestartAsync();
        var reopened = Target(database);
        await Assert.That(await reopened.ReadRevisionAsync(a.Revision)).IsEqualTo(a);
        await Assert.That(await reopened.ReadRevisionAsync(b.Revision)).IsEqualTo(b);
        await Assert.That((await reopened.ReadCurrentAsync())!.Generation).IsEqualTo(3L);
        ExperimentRevision.TryCreate(alternate.Revision, out var pinned);
        ExperimentLookup.TryCreate(Scope.Partition, pinned, out var lookup);
        var result = await new PublicationSnapshotSource(reopened).GetAsync(
            lookup!,
            CancellationToken.None
        );
        await Assert.That(result is SourceResult<ExperimentSnapshot>.Found).IsTrue();
        await Assert
            .That(((SourceResult<ExperimentSnapshot>.Found)result).Release.Revision.Value)
            .IsEqualTo(alternate.Revision);
        await using var db = fixture.CreateConnection(database);
        await db.OpenAsync();
        await using var history = new NpgsqlCommand(
            "SELECT count(*) FROM agentcmd_publication_history",
            db
        );
        await Assert
            .That(Convert.ToInt64(await history.ExecuteScalarAsync(), CultureInfo.InvariantCulture))
            .IsEqualTo(3L);
    }

    [Test]
    [Explicit]
    public async Task RealCommitWithLostAcknowledgementReconcilesWithoutResendAndDetectsDivergence()
    {
        var managementDatabase = await fixture.NewDatabaseAsync();
        var targetDatabase = await fixture.NewDatabaseAsync();
        var store = fixture.Store(managementDatabase);
        await store.InitializeAsync();
        var scenario = await PostgresStoreTests.CreateJobAsync(store);
        var comparison = await PostgresStoreTests.CompleteAndCompareAsync(store, scenario.Job);
        var actualTarget = Target(targetDatabase);
        await actualTarget.InitializeAsync();
        var target = new LoseAcknowledgement(actualTarget);
        var publication = scenario.Publication;
        var decision = await store.SavePublicationDecisionAsync(
            Scope,
            Actor,
            target.Profile,
            new(
                target.Profile.Id,
                publication.Id,
                publication.Revision,
                publication.ContentHash,
                "build",
                "Activate",
                "Pass",
                comparison.Id,
                "candidate",
                "Explicit fixture decision"
            )
        );
        var coordinator = new PublicationCoordinator(store, new([target]));
        var operation = await coordinator.ReviewAsync(
            Scope,
            Actor,
            target.Profile.Id,
            new(decision.Id, decision.ContentHash, true)
        );
        var unknown = await coordinator.DeliverAsync(
            Scope,
            Actor,
            operation.Id,
            operation.Revision
        );
        await Assert.That(unknown.State).IsEqualTo("Unknown");
        await PostgresStoreTests.ExpectCode(
            () => coordinator.DeliverAsync(Scope, Actor, unknown.Id, unknown.Revision),
            "publication_operation_not_deliverable"
        );
        var restarted = new PublicationCoordinator(
            fixture.Store(managementDatabase),
            new([target])
        );
        var reconciled = await restarted.ReconcileAsync(Scope, Actor, unknown.Id, unknown.Revision);
        await Assert.That(reconciled.State).IsEqualTo("Activated");
        await Assert.That(target.ActivationCalls).IsEqualTo(1);
        var other = Content("another-publication", "changed");
        await actualTarget.PutAsync(other);
        await actualTarget.ActivateAsync(
            await actualTarget.ReadCurrentAsync(),
            other,
            "another-operation"
        );
        var diverged = await restarted.ReconcileAsync(
            Scope,
            Actor,
            reconciled.Id,
            reconciled.Revision
        );
        await Assert.That(diverged.State).IsEqualTo("TargetDiverged");
        await Assert.That(target.ActivationCalls).IsEqualTo(1);
        var pin = RelationalControlPlaneStore.Operation(operation).Content;
        await Assert.That(await actualTarget.ReadRevisionAsync(pin.Revision)).IsEqualTo(pin);
        await Assert
            .That((await store.ReadPublicationAdoptionAsync(Scope, operation.Id)).State)
            .IsEqualTo("Unknown");
    }

    [Test]
    [Explicit]
    public async Task TargetRejectsMissingOrCorruptContentWithoutChangingPointerOrHistory()
    {
        var database = await fixture.NewDatabaseAsync();
        var target = Target(database);
        await target.InitializeAsync();
        var content = Content("revision", "retained");
        await PostgresStoreTests.ExpectCode(
            () => target.ActivateAsync(null, content, "absent"),
            "publication_content_missing_or_changed"
        );
        await Assert.That(await target.ReadCurrentAsync()).IsNull();
        await target.PutAsync(content);
        var activated = await target.ActivateAsync(null, content, "initial");
        await using var db = fixture.CreateConnection(database);
        await db.OpenAsync();
        await using (
            var corrupt = new NpgsqlCommand(
                "UPDATE agentcmd_publication_content SET body='{}' WHERE revision=@revision",
                db
            )
        )
        {
            corrupt.Parameters.AddWithValue("revision", content.Revision);
            await corrupt.ExecuteNonQueryAsync();
        }
        ExperimentLookup.TryCreate(Scope.Partition, null, out var lookup);
        var result = await new PublicationSnapshotSource(target).GetAsync(
            lookup!,
            CancellationToken.None
        );
        await Assert
            .That(
                result
                    is SourceResult<ExperimentSnapshot>.Invalid
                    {
                        Failure: SourceFailure.InvalidRelease
                    }
            )
            .IsTrue();
        await PostgresStoreTests.ExpectCode(
            () => target.ActivateAsync(activated.Current, content, "corrupt"),
            "publication_content_invalid"
        );
        await Assert.That(await target.ReadCurrentAsync()).IsEqualTo(activated.Current);
        await using var history = new NpgsqlCommand(
            "SELECT count(*) FROM agentcmd_publication_history",
            db
        );
        await Assert
            .That(Convert.ToInt64(await history.ExecuteScalarAsync(), CultureInfo.InvariantCulture))
            .IsEqualTo(1L);
    }

    private sealed class LoseAcknowledgement(IPublicationTarget inner) : IPublicationTarget
    {
        public int ActivationCalls { get; private set; }
        public PublicationTargetProfile Profile => inner.Profile;
        public PublicationTargetCapabilities Capabilities => inner.Capabilities;

        public Task<PublicationPointer?> ReadCurrentAsync(
            CancellationToken cancellationToken = default
        ) => inner.ReadCurrentAsync(cancellationToken);

        public Task<PublicationContent?> ReadRevisionAsync(
            string revision,
            CancellationToken cancellationToken = default
        ) => inner.ReadRevisionAsync(revision, cancellationToken);

        public Task PutAsync(
            PublicationContent content,
            CancellationToken cancellationToken = default
        ) => inner.PutAsync(content, cancellationToken);

        public async Task<PublicationActivation> ActivateAsync(
            PublicationPointer? expected,
            PublicationContent content,
            string operationId,
            CancellationToken cancellationToken = default
        )
        {
            ActivationCalls++;
            var result = await inner.ActivateAsync(
                expected,
                content,
                operationId,
                cancellationToken
            );
            if (result.Applied)
            {
                throw new IOException(
                    "Injected acknowledgement loss after a real PostgreSQL commit."
                );
            }
            return result;
        }
    }

    private static PublicationContent Content(string revision, string prompt)
    {
        var body = PostgresStoreTests.Element(
            new
            {
                buildId = "build",
                commands = new[]
                {
                    new
                    {
                        commandId = "c",
                        commandRevision = 1,
                        contractSignature = "sig",
                        experimentId = "e",
                        controlVariantId = "v",
                        variants = new[]
                        {
                            new
                            {
                                id = "v",
                                weight = 100,
                                userTemplate = prompt,
                            },
                        },
                    },
                },
            }
        );
        var json = ContentIdentity.Normalize(body);
        return new(revision, ContentIdentity.Hash(json), json);
    }
}
