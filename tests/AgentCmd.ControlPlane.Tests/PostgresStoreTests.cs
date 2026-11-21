using System.Globalization;
using System.Text.Json;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd.ControlPlane;

namespace AgentCmd.ControlPlane.Tests;

[ClassDataSource<PostgresQualificationFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel("postgres-qualification")]
public sealed class PostgresStoreTests(PostgresQualificationFixture fixture)
{
    internal static readonly WorkspaceScope Scope = new("qualification", "test", "one");
    internal static readonly ManagementActor Actor = new("fixture-operator");
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } =
            DateTimeOffset.Parse("2026-11-05T00:00:00Z", CultureInfo.InvariantCulture);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal static JsonElement Element(object value) =>
        JsonSerializer.SerializeToElement(value, Json);

    [Test]
    [Explicit]
    public async Task RestartRetainsFrozenContentAndImmutableBuildRegistration()
    {
        var database = await fixture.NewDatabaseAsync();
        var store = fixture.Store(database);
        await Task.WhenAll(store.InitializeAsync(), fixture.Store(database).InitializeAsync());
        var draft = await store.SaveDraftAsync(
            Scope,
            Actor,
            "datasets",
            new("Cases", Element(new { cases = Array.Empty<object>() }))
        );
        var changed = await store.SaveDraftAsync(
            Scope,
            Actor,
            "datasets",
            new("Changed", Element(new { purpose = "Regression", cases = Array.Empty<object>() })),
            draft.Id,
            draft.Revision
        );
        var frozen = await store.FreezeAsync(Scope, Actor, "datasets", draft.Id, changed.Revision);
        var buildRequest = new SaveResource(
            "Build",
            Element(new { buildId = "immutable-build", commands = Array.Empty<object>() })
        );
        var build = await store.RegisterBuildAsync(Scope, Actor, buildRequest);
        await fixture.RestartAsync();
        var reopened = fixture.Store(database);
        await reopened.InitializeAsync();
        var retained = await reopened.ReadAsync(Scope, "datasets", draft.Id);
        await Assert.That(retained!.State).IsEqualTo("Frozen");
        await Assert.That(retained.Revision).IsEqualTo(3L);
        await Assert.That(retained.ContentHash).IsEqualTo(frozen.ContentHash);
        await Assert
            .That(ContentIdentity.Normalize(retained.Body))
            .IsEqualTo(ContentIdentity.Normalize(frozen.Body));
        await Assert
            .That(
                await reopened.ReadAsync(Scope with { Partition = "other" }, "datasets", draft.Id)
            )
            .IsNull();
        await ExpectCode(
            () =>
                reopened.SaveDraftAsync(
                    Scope,
                    Actor,
                    "datasets",
                    new("Wrong", retained.Body),
                    retained.Id,
                    retained.Revision
                ),
            "immutable_resource"
        );
        await Assert
            .That((await reopened.RegisterBuildAsync(Scope, Actor, buildRequest)).Id)
            .IsEqualTo(build.Id);
        await ExpectCode(
            () =>
                reopened.RegisterBuildAsync(
                    Scope,
                    Actor,
                    buildRequest with
                    {
                        Body = Element(
                            new
                            {
                                buildId = "immutable-build",
                                commands = new[] { new { id = "different" } },
                            }
                        ),
                    }
                ),
            "build_identity_conflict"
        );
        await Assert.That(await CountAsync(fixture, database, "cp_documents")).IsEqualTo(2L);
        await Assert.That(await CountAsync(fixture, database, "cp_events")).IsEqualTo(4L);
        await Assert.That(await CountAsync(fixture, database, "cp_outbox")).IsEqualTo(4L);
    }

    [Test]
    [Explicit]
    public async Task RacingRevisionsCommitOneMutationAndOneOutboxIntent()
    {
        var database = await fixture.NewDatabaseAsync();
        var store = fixture.Store(database);
        await store.InitializeAsync();
        var draft = await store.SaveDraftAsync(
            Scope,
            Actor,
            "datasets",
            new("Cases", Element(new { cases = Array.Empty<object>() }))
        );
        async Task<bool> WriteAsync(string label)
        {
            try
            {
                await fixture
                    .Store(database)
                    .SaveDraftAsync(
                        Scope,
                        Actor,
                        "datasets",
                        new(label, Element(new { label })),
                        draft.Id,
                        draft.Revision
                    );
                return true;
            }
            catch (ManagementException error) when (error.Code == "revision_conflict")
            {
                return false;
            }
        }
        var race = await Task.WhenAll(WriteAsync("one"), WriteAsync("two"));
        await Assert.That(race.Count(value => value)).IsEqualTo(1);
        await Assert
            .That((await store.ReadAsync(Scope, "datasets", draft.Id))!.Revision)
            .IsEqualTo(2L);
        await Assert.That(await CountAsync(fixture, database, "cp_events")).IsEqualTo(2L);
        await Assert.That(await CountAsync(fixture, database, "cp_outbox")).IsEqualTo(2L);
        await using var db = fixture.CreateConnection(database);
        await db.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM cp_events e JOIN cp_outbox o ON e.id=o.id WHERE e.scope=o.scope AND e.resource_id=o.resource_id AND e.kind=o.kind",
            db
        );
        await Assert
            .That(Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture))
            .IsEqualTo(2L);
    }

    [Test]
    [Explicit]
    public async Task RejectedOutboxInsertRollsBackDocumentAndAuditAtomically()
    {
        var database = await fixture.NewDatabaseAsync();
        var store = fixture.Store(database);
        await store.InitializeAsync();
        await using var db = fixture.CreateConnection(database);
        await db.OpenAsync();
        await using (
            var inject = new NpgsqlCommand(
                """
                CREATE FUNCTION reject_qualification_outbox() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION USING ERRCODE='P0001', MESSAGE='qualification fault'; END $$;
                CREATE TRIGGER reject_delivery BEFORE INSERT ON cp_outbox FOR EACH ROW EXECUTE FUNCTION reject_qualification_outbox();
                """,
                db
            )
        )
        {
            await inject.ExecuteNonQueryAsync();
        }
        var failed = false;
        try
        {
            await store.SaveDraftAsync(
                Scope,
                Actor,
                "datasets",
                new("Rejected", Element(new { cases = Array.Empty<object>() }))
            );
        }
        catch (PostgresException error) when (error.SqlState == "P0001")
        {
            failed = true;
        }
        await Assert.That(failed).IsTrue();
        await Assert.That(await CountAsync(fixture, database, "cp_documents")).IsEqualTo(0L);
        await Assert.That(await CountAsync(fixture, database, "cp_events")).IsEqualTo(0L);
        await Assert.That(await CountAsync(fixture, database, "cp_outbox")).IsEqualTo(0L);
        await using (
            var restore = new NpgsqlCommand(
                "DROP TRIGGER reject_delivery ON cp_outbox; DROP FUNCTION reject_qualification_outbox()",
                db
            )
        )
        {
            await restore.ExecuteNonQueryAsync();
        }
        await store.SaveDraftAsync(
            Scope,
            Actor,
            "datasets",
            new("Accepted", Element(new { cases = Array.Empty<object>() }))
        );
        await Assert.That(await CountAsync(fixture, database, "cp_documents")).IsEqualTo(1L);
        await Assert.That(await CountAsync(fixture, database, "cp_events")).IsEqualTo(1L);
        await Assert.That(await CountAsync(fixture, database, "cp_outbox")).IsEqualTo(1L);
    }

    [Test]
    [Explicit]
    public async Task ClaimsFenceStaleWorkersAndAcceptExactLateReceiptsOnce()
    {
        var database = await fixture.NewDatabaseAsync();
        var clock = new Clock();
        var store = fixture.Store(database, clock);
        await store.InitializeAsync();
        var scenario = await CreateJobAsync(store);
        var race = await Task.WhenAll(
            store.ClaimWorkAsync(Scope, Actor, "build", "worker-one", TimeSpan.FromSeconds(1)),
            fixture
                .Store(database, clock)
                .ClaimWorkAsync(Scope, Actor, "build", "worker-two", TimeSpan.FromSeconds(1))
        );
        await Assert.That(race.Count(claim => claim is not null)).IsEqualTo(1);
        var old = race.Single(claim => claim is not null)!;
        clock.Now += TimeSpan.FromSeconds(2);
        var current = (
            await store.ClaimWorkAsync(Scope, Actor, "build", "worker-new", TimeSpan.FromSeconds(5))
        )!;
        await Assert.That(current.Epoch).IsGreaterThan(old.Epoch);
        await ExpectCode(() => PreflightAsync(store, old), "claim_stale");
        await ExpectCode(
            () =>
                store.StartAttemptAsync(
                    Scope,
                    Actor,
                    old.WorkId,
                    old.Owner,
                    old.Epoch,
                    "old-attempt"
                ),
            "claim_stale"
        );
        await PreflightAsync(store, current);
        async Task<string?> StartAsync(string id)
        {
            try
            {
                await fixture
                    .Store(database, clock)
                    .StartAttemptAsync(
                        Scope,
                        Actor,
                        current.WorkId,
                        current.Owner,
                        current.Epoch,
                        id
                    );
                return id;
            }
            catch (ManagementException error) when (error.Code == "claim_stale")
            {
                return null;
            }
        }
        var attempts = await Task.WhenAll(StartAsync("attempt-one"), StartAsync("attempt-two"));
        await Assert.That(attempts.Count(id => id is not null)).IsEqualTo(1);
        var attempt = attempts.Single(id => id is not null)!;
        clock.Now += TimeSpan.FromMinutes(1);
        var restartedStore = fixture.Store(database, clock);
        await Assert
            .That(
                await restartedStore.ClaimWorkAsync(
                    Scope,
                    Actor,
                    "build",
                    "retry",
                    TimeSpan.FromMinutes(1)
                )
            )
            .IsNull();
        var attemptReceipt = Element(
            new { outcome = "Success", providerRequestId = "fixture-receipt" }
        );
        await restartedStore.FinishAttemptAsync(
            Scope,
            Actor,
            current.WorkId,
            current.Owner,
            current.Epoch,
            attempt,
            attemptReceipt
        );
        await restartedStore.FinishAttemptAsync(
            Scope,
            Actor,
            current.WorkId,
            current.Owner,
            current.Epoch,
            attempt,
            attemptReceipt
        );
        await ExpectCode(
            () =>
                restartedStore.FinishAttemptAsync(
                    Scope,
                    Actor,
                    current.WorkId,
                    current.Owner,
                    current.Epoch,
                    attempt,
                    Element(new { outcome = "Changed" })
                ),
            "receipt_conflict"
        );
        var receipt = Receipt(current, scenario.Job.Id);
        await restartedStore.CompleteWorkAsync(
            Scope,
            Actor,
            current.WorkId,
            current.Owner,
            current.Epoch,
            receipt
        );
        await restartedStore.CompleteWorkAsync(
            Scope,
            Actor,
            current.WorkId,
            current.Owner,
            current.Epoch,
            receipt
        );
        await Assert
            .That((await restartedStore.GetJobAsync(Scope, scenario.Job.Id)).Job.State)
            .IsEqualTo("Finished");
        await Assert.That((await restartedStore.ListAsync(Scope, "executions")).Count).IsEqualTo(1);
        await Assert.That(await CountAsync(fixture, database, "cp_attempts")).IsEqualTo(1L);
        await Assert
            .That(
                await restartedStore.ClaimWorkAsync(
                    Scope,
                    Actor,
                    "build",
                    "retry",
                    TimeSpan.FromMinutes(1)
                )
            )
            .IsNull();
    }

    internal sealed record JobScenario(ManagementResource Job, ManagementResource Publication);

    internal static async Task<JobScenario> CreateJobAsync(RelationalControlPlaneStore store)
    {
        await store.RegisterBuildAsync(
            Scope,
            Actor,
            new(
                "Build",
                Element(
                    new
                    {
                        buildId = "build",
                        commands = new[]
                        {
                            new
                            {
                                id = "c",
                                revision = 1,
                                contractSignature = "sig",
                            },
                        },
                    }
                ),
                "build"
            )
        );
        var publication = await store.SaveDraftAsync(
            Scope,
            Actor,
            "publications",
            new(
                "Publication",
                Element(
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
                                experimentId = "experiment",
                                controlVariantId = "v",
                                variants = new[]
                                {
                                    new
                                    {
                                        id = "v",
                                        weight = 100,
                                        userTemplate = "Fixture output",
                                    },
                                },
                            },
                        },
                    }
                )
            )
        );
        publication = await store.FreezeAsync(
            Scope,
            Actor,
            "publications",
            publication.Id,
            publication.Revision
        );
        var dataset = await store.SaveDraftAsync(
            Scope,
            Actor,
            "datasets",
            new(
                "Dataset",
                Element(
                    new
                    {
                        purpose = "Regression",
                        cases = new[]
                        {
                            new
                            {
                                id = "case",
                                sourceFamilyId = "source",
                                input = new { text = "fixture" },
                                assets = Array.Empty<object>(),
                                reference = new { value = 10, completeness = "Exhaustive" },
                            },
                        },
                    }
                )
            )
        );
        dataset = await store.FreezeAsync(Scope, Actor, "datasets", dataset.Id, dataset.Revision);
        var benchmark = await store.SaveDraftAsync(
            Scope,
            Actor,
            "benchmarks",
            new(
                "Benchmark",
                Element(
                    new
                    {
                        datasetId = dataset.Id,
                        repeats = 1,
                        primaryCriterionId = "quality",
                        criteria = new[]
                        {
                            new
                            {
                                id = "quality",
                                version = 1,
                                scope = "Whole",
                                outputBinding = "",
                                referenceBinding = "",
                                scorer = new { kind = "Exact", version = "1" },
                            },
                        },
                    }
                )
            )
        );
        benchmark = await store.FreezeAsync(
            Scope,
            Actor,
            "benchmarks",
            benchmark.Id,
            benchmark.Revision
        );
        var job = await store.CreateBacktestAsync(
            Scope,
            Actor,
            "Backtest",
            new(
                "Backtest",
                dataset.Id,
                benchmark.Id,
                [new("candidate", "Candidate", "build", publication.Id, "c", "v")],
                true
            )
        );
        return new(job, publication);
    }

    internal static Task PreflightAsync(RelationalControlPlaneStore store, WorkClaim claim) =>
        store.RecordPreflightAsync(
            Scope,
            new(claim.Owner),
            claim,
            new(true, ContentIdentity.Hash(ContentIdentity.Normalize(claim.Request)))
        );

    internal static JsonElement Receipt(WorkClaim claim, string jobId)
    {
        var pin = claim.Request.GetProperty("pins")[0];
        var executionId = Guid.NewGuid();
        return Element(
            new
            {
                jobId,
                workId = claim.WorkId,
                candidateId = "candidate",
                caseId = "case",
                repeatIndex = 0,
                buildId = "build",
                publicationId = pin.GetProperty("candidate")
                    .GetProperty("publicationId")
                    .GetString(),
                publicationHash = pin.GetProperty("publicationHash").GetString(),
                commandId = "c",
                variantId = "v",
                purpose = "Backtest",
                role = "Primary",
                executionId,
                outcome = "Success",
                storageStatus = "Stored",
                value = 10,
                outputRef = new
                {
                    runId = Guid.NewGuid(),
                    artifactId = Guid.NewGuid(),
                    versionId = Guid.NewGuid(),
                    version = 1,
                    producer = new Dictionary<string, string>
                    {
                        ["$producer"] = "command",
                        ["id"] = executionId.ToString(),
                    },
                },
            }
        );
    }

    internal static async Task<ManagementResource> CompleteAndCompareAsync(
        RelationalControlPlaneStore store,
        ManagementResource job
    )
    {
        var claim = (
            await store.ClaimWorkAsync(
                Scope,
                Actor,
                "build",
                "fixture-worker",
                TimeSpan.FromMinutes(1)
            )
        )!;
        await PreflightAsync(store, claim);
        await store.StartAttemptAsync(
            Scope,
            Actor,
            claim.WorkId,
            claim.Owner,
            claim.Epoch,
            "attempt"
        );
        await store.FinishAttemptAsync(
            Scope,
            Actor,
            claim.WorkId,
            claim.Owner,
            claim.Epoch,
            "attempt",
            Element(new { outcome = "Success" })
        );
        var receipt = Receipt(claim, job.Id);
        await store.CompleteWorkAsync(
            Scope,
            Actor,
            claim.WorkId,
            claim.Owner,
            claim.Epoch,
            receipt
        );
        var assessment = await store.AssessDeterministicAsync(
            Scope,
            Actor,
            new(receipt.GetProperty("executionId").GetString()!, "quality")
        );
        return await store.SaveComparisonAsync(
            Scope,
            Actor,
            "Comparison",
            new([assessment.Id], "candidate")
        );
    }

    internal static async Task ExpectCode(Func<Task> action, string code)
    {
        try
        {
            await action();
        }
        catch (ManagementException error)
        {
            await Assert.That(error.Code).IsEqualTo(code);
            return;
        }
        throw new InvalidOperationException("Expected " + code);
    }

    internal static async Task<long> CountAsync(
        PostgresQualificationFixture fixture,
        string database,
        string table
    )
    {
        if (table is not ("cp_documents" or "cp_events" or "cp_outbox" or "cp_attempts"))
        {
            throw new ArgumentException("Unknown qualification table", nameof(table));
        }
        await using var db = fixture.CreateConnection(database);
        await db.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM " + table, db);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}
