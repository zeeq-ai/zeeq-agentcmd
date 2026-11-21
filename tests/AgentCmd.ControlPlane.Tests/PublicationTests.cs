using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd;
using Zeeq.AgentCmd.ControlPlane;
using Zeeq.AgentCmd.ControlPlane.Host;

namespace AgentCmd.ControlPlane.Tests;

public sealed class PublicationTests
{
    private static readonly WorkspaceScope Scope = new("app", "test", "one");
    private static readonly ManagementActor Actor = new("reviewer");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-05T12:00:00Z");

        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal sealed class FakeTarget(PublicationTargetProfile profile) : IPublicationTarget
    {
        private readonly object gate = new();
        private readonly Dictionary<string, PublicationContent> content = new(
            StringComparer.Ordinal
        );
        private PublicationPointer? pointer;
        public PublicationTargetProfile Profile { get; } = profile;
        public PublicationTargetCapabilities Capabilities => new("OfflineFake", true, true, false);
        public bool LoseAcknowledgement { get; set; }
        public bool UnavailableReads { get; set; }
        public bool CorruptReads { get; set; }
        public int ActivationCalls { get; private set; }

        public Task<PublicationPointer?> ReadCurrentAsync(
            CancellationToken cancellationToken = default
        )
        {
            if (UnavailableReads)
            {
                throw new IOException("injected read failure");
            }
            lock (gate)
            {
                return Task.FromResult(pointer);
            }
        }

        public Task<PublicationContent?> ReadRevisionAsync(
            string revision,
            CancellationToken cancellationToken = default
        )
        {
            lock (gate)
            {
                var value = content.GetValueOrDefault(revision);
                return Task.FromResult(
                    value is not null && CorruptReads ? value with { Json = "{}" } : value
                );
            }
        }

        public Task PutAsync(
            PublicationContent value,
            CancellationToken cancellationToken = default
        )
        {
            PublicationCodec.Verify(value);
            lock (gate)
            {
                if (content.TryGetValue(value.Revision, out var prior) && prior != value)
                {
                    throw new ManagementException("publication_revision_conflict", 409);
                }
                content.TryAdd(value.Revision, value);
            }
            return Task.CompletedTask;
        }

        public Task<PublicationActivation> ActivateAsync(
            PublicationPointer? expected,
            PublicationContent value,
            string operationId,
            CancellationToken cancellationToken = default
        )
        {
            lock (gate)
            {
                ActivationCalls++;
                if (content.GetValueOrDefault(value.Revision) != value)
                {
                    throw new ManagementException("publication_content_missing_or_changed");
                }
                if (pointer != expected)
                {
                    return Task.FromResult(new PublicationActivation(false, pointer));
                }
                pointer = new(
                    (pointer?.Generation ?? 0) + 1,
                    value.Revision,
                    value.ContentHash,
                    operationId
                );
                if (LoseAcknowledgement)
                {
                    LoseAcknowledgement = false;
                    throw new IOException("injected acknowledgement loss");
                }
                return Task.FromResult(new PublicationActivation(true, pointer));
            }
        }
    }

    private sealed record Fixture(
        RelationalControlPlaneStore Store,
        Clock Clock,
        FakeTarget Target,
        PublicationCoordinator Coordinator,
        ManagementResource Publication,
        ManagementResource Comparison,
        string DatabasePath
    )
    {
        public async Task<ManagementResource> ReviewAsync(
            ManagementResource? publication = null,
            string action = "Activate",
            string? evidenceId = null
        )
        {
            var selected = publication ?? Publication;
            var decision = await Store.SavePublicationDecisionAsync(
                Scope,
                Actor,
                Target.Profile,
                new(
                    Target.Profile.Id,
                    selected.Id,
                    selected.Revision,
                    selected.ContentHash,
                    "build",
                    action,
                    "Pass",
                    Comparison.Id,
                    "candidate",
                    "Reviewed exact evidence and remaining uncertainty",
                    evidenceId
                )
            );
            return await Coordinator.ReviewAsync(
                Scope,
                Actor,
                Target.Profile.Id,
                new(decision.Id, decision.ContentHash, true)
            );
        }
    }

    private static JsonElement Element(object value) =>
        JsonSerializer.SerializeToElement(value, Json);

    private static async Task<Fixture> CreateAsync()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "agentcmd-publication-" + Guid.NewGuid().ToString("N") + ".db"
        );
        var clock = new Clock();
        var store = new RelationalControlPlaneStore(
            new("Sqlite", () => new SqliteConnection("Data Source=" + path)),
            clock
        );
        await store.InitializeAsync();
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
            new("Publication", Body("First"))
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
            new("Dataset", Element(new { cases = new[] { new { id = "case" } } }))
        );
        dataset = await store.FreezeAsync(Scope, Actor, "datasets", dataset.Id, dataset.Revision);
        var benchmark = await store.SaveDraftAsync(
            Scope,
            Actor,
            "benchmarks",
            new(
                "Benchmark",
                Element(new { datasetId = dataset.Id, primaryCriterionId = "quality" })
            )
        );
        benchmark = await store.FreezeAsync(
            Scope,
            Actor,
            "benchmarks",
            benchmark.Id,
            benchmark.Revision
        );
        // Seed exact evidence to isolate publication protocol from provider/worker execution.
        var assessment = await store.TransactionAsync(
            async (db, tx) =>
            {
                var job = await store.InsertAsync(
                    db,
                    tx,
                    Scope,
                    Actor,
                    "jobs",
                    Guid.NewGuid().ToString("N"),
                    "Job",
                    "Completed",
                    ContentIdentity.Normalize(
                        Element(
                            new
                            {
                                datasetId = dataset.Id,
                                datasetHash = dataset.ContentHash,
                                benchmarkId = benchmark.Id,
                                benchmarkHash = benchmark.ContentHash,
                                pins = new[]
                                {
                                    new
                                    {
                                        publicationHash = publication.ContentHash,
                                        candidate = new
                                        {
                                            id = "candidate",
                                            publicationId = publication.Id,
                                            buildId = "build",
                                        },
                                    },
                                },
                            }
                        )
                    ),
                    CancellationToken.None
                );
                return await store.InsertAsync(
                    db,
                    tx,
                    Scope,
                    Actor,
                    "assessments",
                    Guid.NewGuid().ToString("N"),
                    "Quality",
                    "Scored",
                    ContentIdentity.Normalize(
                        Element(
                            new
                            {
                                jobId = job.Id,
                                candidateId = "candidate",
                                datasetHash = dataset.ContentHash,
                                benchmarkHash = benchmark.ContentHash,
                                score = 1,
                            }
                        )
                    ),
                    CancellationToken.None
                );
            },
            CancellationToken.None
        );
        var comparison = await store.SaveComparisonAsync(
            Scope,
            Actor,
            "Comparison",
            new([assessment.Id], "candidate")
        );
        var target = new FakeTarget(new("target", Scope, "binding", "binding-v1", "build", true));
        return new(store, clock, target, new(store, new([target])), publication, comparison, path);
    }

    private static JsonElement Body(string prompt, int weight = 100) =>
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
                                userTemplate = prompt,
                                weight,
                            },
                        },
                    },
                },
            }
        );

    private static async Task ExpectCode(Func<Task> action, string code)
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

    [Test]
    public async Task ConcurrentReviewsConflictAndOldRevisionPinsSurviveNewActivation()
    {
        var fixture = await CreateAsync();
        var first = await fixture.ReviewAsync();
        var second = await fixture.ReviewAsync();
        var deliveries = await Task.WhenAll(
            fixture.Coordinator.DeliverAsync(Scope, Actor, first.Id, first.Revision),
            fixture.Coordinator.DeliverAsync(Scope, Actor, second.Id, second.Revision)
        );
        await Assert.That(deliveries.Count(d => d.State == "Activated")).IsEqualTo(1);
        await Assert.That(deliveries.Count(d => d.State == "TargetDiverged")).IsEqualTo(1);
        var old = RelationalControlPlaneStore.Operation(first).Content;
        var changedJson = ContentIdentity.Normalize(Body("Second"));
        var changed = new PublicationContent(
            "next",
            ContentIdentity.Hash(changedJson),
            changedJson
        );
        await fixture.Target.PutAsync(changed);
        await fixture.Target.ActivateAsync(
            await fixture.Target.ReadCurrentAsync(),
            changed,
            "external-change"
        );
        ExperimentRevision.TryCreate(old.Revision, out var revision);
        ExperimentLookup.TryCreate(Scope.Partition, revision, out var lookup);
        var result = await new PublicationSnapshotSource(fixture.Target).GetAsync(
            lookup!,
            CancellationToken.None
        );
        await Assert.That(result is SourceResult<ExperimentSnapshot>.Found).IsTrue();
        var view = ((SourceResult<ExperimentSnapshot>.Found)result).Release.Commands["c"];
        await Assert
            .That(((TemplateChange.Replaced)view.Variants["v"].Prompt.User).Text)
            .IsEqualTo("First");
        await Assert.That(await fixture.Target.ReadRevisionAsync(old.Revision)).IsEqualTo(old);
    }

    [Test]
    public async Task LostAcknowledgementReconcilesAfterRestartWithoutAnotherSend()
    {
        var fixture = await CreateAsync();
        var operation = await fixture.ReviewAsync();
        fixture.Target.LoseAcknowledgement = true;
        var unknown = await fixture.Coordinator.DeliverAsync(
            Scope,
            Actor,
            operation.Id,
            operation.Revision
        );
        await Assert.That(unknown.State).IsEqualTo("Unknown");
        await ExpectCode(
            () => fixture.Coordinator.DeliverAsync(Scope, Actor, unknown.Id, unknown.Revision),
            "publication_operation_not_deliverable"
        );
        var restarted = new RelationalControlPlaneStore(
            new("Sqlite", () => new SqliteConnection("Data Source=" + fixture.DatabasePath)),
            fixture.Clock
        );
        var coordinator = new PublicationCoordinator(restarted, new([fixture.Target]));
        var reconciled = await coordinator.ReconcileAsync(
            Scope,
            Actor,
            unknown.Id,
            unknown.Revision
        );
        await Assert.That(reconciled.State).IsEqualTo("Activated");
        await Assert.That(fixture.Target.ActivationCalls).IsEqualTo(1);
    }

    [Test]
    public async Task LostAcknowledgementThenExternalChangeIsDivergedAndUnreadableIsUnknown()
    {
        var fixture = await CreateAsync();
        var operation = await fixture.ReviewAsync();
        fixture.Target.LoseAcknowledgement = true;
        var unknown = await fixture.Coordinator.DeliverAsync(
            Scope,
            Actor,
            operation.Id,
            operation.Revision
        );
        var otherJson = ContentIdentity.Normalize(Body("Other"));
        var other = new PublicationContent("other", ContentIdentity.Hash(otherJson), otherJson);
        await fixture.Target.PutAsync(other);
        await fixture.Target.ActivateAsync(
            await fixture.Target.ReadCurrentAsync(),
            other,
            "other-operation"
        );
        var diverged = await fixture.Coordinator.ReconcileAsync(
            Scope,
            Actor,
            unknown.Id,
            unknown.Revision
        );
        await Assert.That(diverged.State).IsEqualTo("TargetDiverged");
        fixture.Target.UnavailableReads = true;
        var unreadable = await fixture.Coordinator.ReconcileAsync(
            Scope,
            Actor,
            diverged.Id,
            diverged.Revision
        );
        await Assert.That(unreadable.State).IsEqualTo("Unknown");
        await Assert.That(fixture.Target.ActivationCalls).IsEqualTo(2);
    }

    [Test]
    public async Task GenerationTokenRejectsAbaAndImmutableContentCannotBeReplaced()
    {
        var fixture = await CreateAsync();
        var operation = await fixture.ReviewAsync();
        var applied = await fixture.Coordinator.DeliverAsync(
            Scope,
            Actor,
            operation.Id,
            operation.Revision
        );
        var oldPointer = await fixture.Target.ReadCurrentAsync();
        var oldContent = RelationalControlPlaneStore.Operation(applied).Content;
        var nextJson = ContentIdentity.Normalize(Body("Next"));
        var next = new PublicationContent("next", ContentIdentity.Hash(nextJson), nextJson);
        await fixture.Target.PutAsync(next);
        await fixture.Target.ActivateAsync(oldPointer, next, "next-op");
        await fixture.Target.ActivateAsync(
            await fixture.Target.ReadCurrentAsync(),
            oldContent,
            "back-op"
        );
        var stale = await fixture.Target.ActivateAsync(oldPointer, next, "stale-op");
        await Assert.That(stale.Applied).IsFalse();
        await Assert.That(stale.Current!.Generation).IsEqualTo(3L);
        await ExpectCode(
            () => fixture.Target.PutAsync(next with { Revision = oldContent.Revision }),
            "publication_revision_conflict"
        );
    }

    [Test]
    public async Task AdoptionNeedsFreshAuthenticatedReportsAndDoesNotFollowPublicationAcknowledgement()
    {
        var fixture = await CreateAsync();
        var operation = await fixture.ReviewAsync();
        var delivered = await fixture.Coordinator.DeliverAsync(
            Scope,
            Actor,
            operation.Id,
            operation.Revision
        );
        await Assert
            .That((await fixture.Store.ReadPublicationAdoptionAsync(Scope, delivered.Id)).State)
            .IsEqualTo("Unknown");
        var pin = RelationalControlPlaneStore.Operation(delivered);
        var report = new PublicationHostReport(
            "target",
            "build",
            "binding",
            "binding-v1",
            pin.Content.Revision,
            pin.Content.ContentHash,
            1
        );
        await fixture.Store.ReportPublicationHostAsync(
            Scope,
            new("host-one"),
            fixture.Target.Profile,
            report
        );
        var adoption = await fixture.Store.ReadPublicationAdoptionAsync(Scope, operation.Id);
        await Assert.That(adoption.Hosts.Single().HostId).IsEqualTo("host-one");
        await Assert.That(adoption.Hosts.Single().State).IsEqualTo("Reported");
        await fixture.Store.ReportPublicationHostAsync(
            Scope,
            new("host-one"),
            fixture.Target.Profile,
            report with
            {
                Sequence = 2,
                BindingRevision = "old-binding",
            }
        );
        await Assert
            .That(
                (await fixture.Store.ReadPublicationAdoptionAsync(Scope, operation.Id))
                    .Hosts.Single()
                    .State
            )
            .IsEqualTo("OtherPublication");
        await ExpectCode(
            () =>
                fixture.Store.ReportPublicationHostAsync(
                    Scope,
                    new("host-one"),
                    fixture.Target.Profile,
                    report
                ),
            "host_report_stale"
        );
        fixture.Clock.Now += TimeSpan.FromMinutes(6);
        await Assert
            .That((await fixture.Store.ReadPublicationAdoptionAsync(Scope, operation.Id)).State)
            .IsEqualTo("Unknown");
    }

    [Test]
    public async Task DecisionChecksEvidencePolicyAndNamesManualAuthority()
    {
        var fixture = await CreateAsync();
        var request = new PublicationDecisionRequest(
            "target",
            fixture.Publication.Id,
            fixture.Publication.Revision,
            fixture.Publication.ContentHash,
            "build",
            "Activate",
            "Pass",
            fixture.Comparison.Id,
            "candidate",
            "Manual review"
        );
        await ExpectCode(
            () =>
                fixture.Store.SavePublicationDecisionAsync(
                    Scope,
                    Actor,
                    fixture.Target.Profile with
                    {
                        AllowManualDecisions = false,
                    },
                    request
                ),
            "manual_publication_decision_not_permitted"
        );
        await ExpectCode(
            () =>
                fixture.Store.SavePublicationDecisionAsync(
                    Scope,
                    Actor,
                    fixture.Target.Profile,
                    request with
                    {
                        CandidateId = "unrelated",
                    }
                ),
            "publication_report_candidate_missing"
        );
        var decision = await fixture.Store.SavePublicationDecisionAsync(
            Scope,
            Actor,
            fixture.Target.Profile,
            request
        );
        await Assert.That(decision.Body.GetProperty("author").GetString()).IsEqualTo(Actor.Subject);
        await Assert
            .That(decision.Body.GetProperty("automatedAcceptance").GetString())
            .IsEqualTo("NotAsserted");
        await ExpectCode(
            () =>
                fixture.Coordinator.ReviewAsync(
                    Scope,
                    Actor,
                    "target",
                    new(decision.Id, decision.ContentHash, false)
                ),
            "publication_review_required"
        );
    }

    [Test]
    public async Task ExplicitRestoreUsesOldIncidentEvidenceAndPreservesOldContent()
    {
        var fixture = await CreateAsync();
        var first = await fixture.ReviewAsync();
        await fixture.Coordinator.DeliverAsync(Scope, Actor, first.Id, first.Revision);
        var restore = await fixture.Store.SaveDraftAsync(
            Scope,
            Actor,
            "publications",
            new(
                "Restore",
                Element(
                    new
                    {
                        buildId = "build",
                        restoreConfiguredDefaults = true,
                        commands = Array.Empty<object>(),
                    }
                )
            )
        );
        restore = await fixture.Store.FreezeAsync(
            Scope,
            Actor,
            "publications",
            restore.Id,
            restore.Revision
        );
        var operation = await fixture.ReviewAsync(
            restore,
            "RestoreConfiguredDefaults",
            fixture.Publication.Id
        );
        var delivered = await fixture.Coordinator.DeliverAsync(
            Scope,
            Actor,
            operation.Id,
            operation.Revision
        );
        ExperimentLookup.TryCreate(Scope.Partition, null, out var lookup);
        var found = await new PublicationSnapshotSource(fixture.Target).GetAsync(
            lookup!,
            CancellationToken.None
        );
        await Assert
            .That(((SourceResult<ExperimentSnapshot>.Found)found).Release.Commands.Count)
            .IsEqualTo(0);
        await Assert.That(delivered.State).IsEqualTo("Activated");
        await Assert
            .That(
                await fixture.Target.ReadRevisionAsync(
                    RelationalControlPlaneStore.Operation(first).Content.Revision
                )
            )
            .IsNotNull();
        var zero = await fixture.Store.SaveDraftAsync(
            Scope,
            Actor,
            "publications",
            new("Bad stop", Body("Stop", 0))
        );
        zero = await fixture.Store.FreezeAsync(
            Scope,
            Actor,
            "publications",
            zero.Id,
            zero.Revision
        );
        await ExpectCode(() => fixture.ReviewAsync(zero), "publication_allocation_invalid");
    }

    [Test]
    public async Task CorruptHistoryAndForeignScopeAreRejectedByRuntimeSource()
    {
        var fixture = await CreateAsync();
        var operation = await fixture.ReviewAsync();
        await fixture.Coordinator.DeliverAsync(Scope, Actor, operation.Id, operation.Revision);
        var source = new PublicationSnapshotSource(fixture.Target);
        ExperimentLookup.TryCreate("foreign", null, out var foreign);
        var denied = await source.GetAsync(foreign!, CancellationToken.None);
        await Assert
            .That(
                denied
                    is SourceResult<ExperimentSnapshot>.Unavailable
                    {
                        Failure: SourceFailure.AuthorizationDenied
                    }
            )
            .IsTrue();
        fixture.Target.CorruptReads = true;
        ExperimentLookup.TryCreate(Scope.Partition, null, out var lookup);
        var corrupt = await source.GetAsync(lookup!, CancellationToken.None);
        await Assert
            .That(
                corrupt
                    is SourceResult<ExperimentSnapshot>.Invalid
                    {
                        Failure: SourceFailure.InvalidRelease
                    }
            )
            .IsTrue();
        await ExpectCode(
            () =>
                fixture.Coordinator.ReviewAsync(
                    Scope,
                    Actor,
                    "target",
                    new("unused", "unused", true)
                ),
            "publication_target_unreadable"
        );
    }

    [Test]
    public async Task UnconfiguredRegistryAndPostgresConstructionNeverResolveCredentials()
    {
        var resolved = false;
        var target = new PublicationPostgresTarget(
            new("target", Scope, "binding", "v1", "build"),
            "TEST_REFERENCE",
            (_, _) =>
            {
                resolved = true;
                throw new InvalidOperationException("must not resolve");
            }
        );
        await Assert.That(target.Capabilities.Qualified).IsTrue();
        await Assert.That(resolved).IsFalse();
        await Assert.That(new PublicationTargetRegistry([]).ForScope(Scope).Count).IsEqualTo(0);
        await ExpectCode(
            () => Task.FromResult(new PublicationTargetRegistry([]).Resolve(Scope, "target")),
            "publication_target_unconfigured"
        );
    }

    [Test]
    public async Task PortableRouteAndSystemModesPreserveDeclaredRuntimeMeaning()
    {
        var variant = new Dictionary<string, object?>
        {
            ["id"] = "v",
            ["weight"] = 100,
            ["userTemplate"] = "Read {Document}",
            ["systemMode"] = "inherit",
            ["systemTemplate"] = null,
            ["parameters"] = new Dictionary<string, string> { ["Locale"] = "en" },
            ["route"] = new
            {
                key = new { id = "review" },
                provider = "provider",
                model = "model",
                temperature = 0.2,
                defaults = new
                {
                    temperature = 0.4,
                    maxOutputTokens = 200,
                    topP = 0.9,
                    topK = 5,
                    reasoningEffort = "High",
                    reasoningOutput = "Summary",
                },
            },
        };
        PublicationContent Content()
        {
            var json = ContentIdentity.Normalize(
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
                                experimentId = "e",
                                controlVariantId = "v",
                                variants = new[] { variant },
                            },
                        },
                    }
                )
            );
            return new("modes", ContentIdentity.Hash(json), json);
        }
        ExperimentVariant Read() =>
            PublicationCodec.Snapshot(Content()).Commands["c"].Variants["v"];
        var inherited = Read();
        await Assert.That(inherited.Prompt.System is TemplateChange.Inherited).IsTrue();
        await Assert.That(inherited.Route!.Key.Id).IsEqualTo("review");
        await Assert
            .That(inherited.Route.Defaults!.ReasoningEffort)
            .IsEqualTo(Microsoft.Extensions.AI.ReasoningEffort.High);
        await Assert
            .That(inherited.Route.Defaults.ReasoningOutput)
            .IsEqualTo(Microsoft.Extensions.AI.ReasoningOutput.Summary);
        await Assert.That(inherited.ParameterValues["Locale"].GetString()).IsEqualTo("en");
        variant["systemMode"] = "remove";
        await Assert.That(Read().Prompt.System is TemplateChange.Removed).IsTrue();
        variant["systemMode"] = "replace";
        variant["systemTemplate"] = "System";
        await Assert.That(((TemplateChange.Replaced)Read().Prompt.System).Text).IsEqualTo("System");
        variant.Remove("systemMode");
        variant["systemTemplate"] = null;
        await Assert.That(Read().Prompt.System is TemplateChange.Removed).IsTrue();
        variant.Remove("systemTemplate");
        await Assert.That(Read().Prompt.System is TemplateChange.Inherited).IsTrue();
        variant["profile"] = "cannot-resolve-a-key-alone";
        await ExpectCode(
            () => Task.FromResult(Read()),
            "publication_profile_key_requires_typed_resolution"
        );
        variant.Remove("profile");
        variant["parameterOverrides"] = new { Locale = "unexpected-alias" };
        await ExpectCode(() => Task.FromResult(Read()), "publication_variant_field_unsupported");
    }

    [Test]
    public async Task HostHandlerUsesPrincipalAndDeniesMissingPermission()
    {
        var fixture = await CreateAsync();
        var services = new ServiceCollection()
            .AddSingleton(new ControlPlaneOptions())
            .AddSingleton(new PublicationTargetRegistry([fixture.Target]))
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.User = new(
            new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "authenticated-host"),
                    new Claim("agentcmd.permission", "Read"),
                ],
                "Test"
            )
        );
        var handler = new PublicationHandler(fixture.Store, services);
        var request = new PublicationHostReport(
            "target",
            "build",
            "binding",
            "binding-v1",
            null,
            null,
            1
        );
        await ExpectCode(
            () => handler.ReportAsync(context, request, CancellationToken.None),
            "action_forbidden"
        );
        context.User = new(
            new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "authenticated-host"),
                    new Claim("agentcmd.permission", "Operations.Manage"),
                    new Claim("agentcmd.application", Scope.Application),
                    new Claim("agentcmd.environment", Scope.Environment),
                    new Claim("agentcmd.partition", Scope.Partition),
                ],
                "Test"
            )
        );
        context.Request.Headers["X-AgentCmd-Application"] = Scope.Application;
        context.Request.Headers["X-AgentCmd-Environment"] = Scope.Environment;
        context.Request.Headers["X-AgentCmd-Partition"] = Scope.Partition;
        await handler.ReportAsync(context, request, CancellationToken.None);
        var stored = (await fixture.Store.ListAsync(Scope, "hosts")).Single();
        await Assert
            .That(stored.Body.GetProperty("hostId").GetString())
            .IsEqualTo("authenticated-host");
    }
}
