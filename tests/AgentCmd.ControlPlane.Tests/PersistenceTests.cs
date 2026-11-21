using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd.ControlPlane;

namespace AgentCmd.ControlPlane.Tests;

public class PersistenceTests
{
    private static readonly WorkspaceScope Scope = new("catalog", "test", "one");
    private static readonly ManagementActor Actor = new("test-operator");

    private static JsonElement J(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private sealed class Fixture : IDisposable
    {
        internal string DirectoryPath { get; } =
            Path.Combine(Path.GetTempPath(), "agentcmd-store-" + Guid.NewGuid().ToString("N"));
        internal string DatabasePath => Path.Combine(DirectoryPath, "management.db");

        internal RelationalControlPlaneStore Store() =>
            new(
                new("Sqlite", () =>
                    new SqliteConnection(
                        new SqliteConnectionStringBuilder
                        {
                            DataSource = DatabasePath,
                            Pooling = false,
                        }.ToString()
                    )
                )
            );

        internal Fixture() => Directory.CreateDirectory(DirectoryPath);

        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }

    [Test]
    public async Task DraftFreezeAndRevisionSurviveANewStoreInstance()
    {
        using var fixture = new Fixture();
        var store = fixture.Store();
        await store.InitializeAsync();
        var draft = await store.SaveDraftAsync(
            Scope,
            Actor,
            "datasets",
            new("cases", J("{\"cases\":[]}"))
        );
        var edited = await store.SaveDraftAsync(
            Scope,
            Actor,
            "datasets",
            new("updated", J("{\"cases\":[],\"purpose\":\"Regression\"}")),
            draft.Id,
            draft.Revision
        );
        var frozen = await store.FreezeAsync(Scope, Actor, "datasets", edited.Id, edited.Revision);
        var reopened = await fixture.Store().ReadAsync(Scope, "datasets", draft.Id);
        await Assert.That(reopened!.State).IsEqualTo("Frozen");
        await Assert.That(reopened.Revision).IsEqualTo(3L);
        await Assert.That(reopened.ContentHash).IsEqualTo(frozen.ContentHash);
        await Assert
            .That(await store.ReadAsync(Scope with { Partition = "other" }, "datasets", draft.Id))
            .IsNull();
        var immutable = false;
        try
        {
            await store.SaveDraftAsync(
                Scope,
                Actor,
                "datasets",
                new("wrong", draft.Body),
                draft.Id,
                frozen.Revision
            );
        }
        catch (ManagementException error) when (error.Code == "immutable_resource")
        {
            immutable = true;
        }
        await Assert.That(immutable).IsTrue();
    }

    [Test]
    public async Task TwoWritersCannotCommitTheSameExpectedRevision()
    {
        using var fixture = new Fixture();
        var store = fixture.Store();
        await store.InitializeAsync();
        var draft = await store.SaveDraftAsync(
            Scope,
            Actor,
            "datasets",
            new("cases", J("{\"cases\":[]}"))
        );
        async Task<bool> Write(string name)
        {
            try
            {
                await fixture
                    .Store()
                    .SaveDraftAsync(
                        Scope,
                        Actor,
                        "datasets",
                        new(name, draft.Body),
                        draft.Id,
                        draft.Revision
                    );
                return true;
            }
            catch (ManagementException error) when (error.Status == 409)
            {
                return false;
            }
        }
        var writes = await Task.WhenAll(Write("one"), Write("two"));
        await Assert.That(writes.Count(value => value)).IsEqualTo(1);
    }

    [Test]
    public async Task FailedOutboxInsertRollsBackTheDraftAndAudit()
    {
        using var fixture = new Fixture();
        var store = fixture.Store();
        await store.InitializeAsync();
        await using (
            var connection = new SqliteConnection(
                $"Data Source={fixture.DatabasePath};Pooling=False"
            )
        )
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TRIGGER deny_delivery BEFORE INSERT ON cp_outbox BEGIN SELECT RAISE(ABORT,'injected'); END";
            await command.ExecuteNonQueryAsync();
        }
        var rejected = false;
        try
        {
            await store.SaveDraftAsync(Scope, Actor, "datasets", new("cases", J("{\"cases\":[]}")));
        }
        catch (ManagementException)
        {
            rejected = true;
        }
        await Assert.That(rejected).IsTrue();
        await Assert.That((await store.ListAsync(Scope, "datasets")).Count).IsEqualTo(0);
        await using var verify = new SqliteConnection(
            $"Data Source={fixture.DatabasePath};Pooling=False"
        );
        await verify.OpenAsync();
        await using var count = verify.CreateCommand();
        count.CommandText = "SELECT count(*) FROM cp_events";
        await Assert.That(Convert.ToInt64(await count.ExecuteScalarAsync())).IsEqualTo(0L);
    }

    [Test]
    public async Task ImmutableBuildRegistrationReplaysContentAndRejectsConflicts()
    {
        using var fixture = new Fixture();
        var store = fixture.Store();
        await store.InitializeAsync();
        var request = new SaveResource("build", J("{\"buildId\":\"b1\",\"commands\":[]}"));
        var first = await store.RegisterBuildAsync(Scope, Actor, request);
        var replay = await store.RegisterBuildAsync(Scope, Actor, request);
        await Assert.That(replay.Id).IsEqualTo(first.Id);
        await Assert.That(replay.Revision).IsEqualTo(1L);
        var conflict = false;
        try
        {
            await store.RegisterBuildAsync(
                Scope,
                Actor,
                request with
                {
                    Body = J("{\"buildId\":\"b1\",\"commands\":[1]}"),
                }
            );
        }
        catch (ManagementException error) when (error.Code == "build_identity_conflict")
        {
            conflict = true;
        }
        await Assert.That(conflict).IsTrue();
    }

    [Test]
    public async Task AssetsAreVerifiedAndRemainInsideTheirAuthorizedNamespace()
    {
        using var fixture = new Fixture();
        var store = new FileAssetStore(Path.Combine(fixture.DirectoryPath, "assets"), 1024);
        await using var bytes = new MemoryStream(Encoding.UTF8.GetBytes("retained source"));
        var asset = await store.PutAsync(Scope, bytes);
        await using var retained = await store.OpenAsync(Scope, asset.Hash);
        await Assert.That(retained.Length).IsEqualTo(asset.Length);
        var isolated = false;
        try
        {
            await using var wrong = await store.OpenAsync(
                Scope with
                {
                    Partition = "other",
                },
                asset.Hash
            );
        }
        catch (ManagementException error) when (error.Code == "asset_not_retained")
        {
            isolated = true;
        }
        await Assert.That(isolated).IsTrue();
        var bounded = false;
        try
        {
            await using var large = new MemoryStream(new byte[2048]);
            await store.PutAsync(Scope, large);
        }
        catch (ManagementException error) when (error.Code == "asset_too_large")
        {
            bounded = true;
        }
        await Assert.That(bounded).IsTrue();
        await Assert
            .That(
                Directory
                    .GetFiles(
                        Path.Combine(fixture.DirectoryPath, "assets"),
                        ".pending-*",
                        SearchOption.AllDirectories
                    )
                    .Length
            )
            .IsEqualTo(0);
    }
}
